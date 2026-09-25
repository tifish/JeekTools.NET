using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

/// <summary>
/// Accepts MCP sessions over a Windows named pipe instead of a TCP port. Nothing is
/// allocated, so the pipe name is stable across runs and can be hard-coded in a client's
/// config; there is no firewall prompt, and access is limited to the current user by the
/// pipe ACL rather than by a secret in a URL. Each accepted client owns a pipe instance,
/// which is also its MCP session, and the duplex stream leaves room for server-initiated
/// notifications that a POST/response HTTP listener cannot deliver.
///
/// Framing is one JSON-RPC message per line — the same framing the stdio adapter speaks on
/// the other end, so it forwards bytes without reframing.
/// </summary>
internal sealed class McpPipeServer(
    string pipeName,
    int maxSessions,
    TimeSpan idleTimeout,
    Func<JsonNode, CancellationToken, Task<JsonNode?>> dispatch)
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(McpPipeServer));
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private CancellationTokenSource? _cancellation;

    public string PipeName { get; } = pipeName;

    public void Start()
    {
        if (_cancellation is not null)
            return;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _ = Task.Run(() => AcceptLoopAsync(cancellation.Token));
        Log.ZLogInformation($"MCP pipe server listening on \\\\.\\pipe\\{PipeName}");
    }

    public void Stop()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateServerStream();
            }
            catch (Exception ex)
            {
                // Typically "all pipe instances are busy": back off and retry so the
                // server recovers once a session ends instead of dying for good.
                Log.ZLogWarning($"Could not create a pipe instance for {PipeName}: {ex.Message}");
                try { await Task.Delay(1000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                Log.ZLogWarning($"Pipe {PipeName} failed to accept a client: {ex.Message}");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // Serve on its own task and immediately loop so the next instance is listening.
            _ = Task.Run(() => ServeAsync(pipe, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var requests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var tasks = new ConcurrentDictionary<int, Task>();
        using var writeGate = new SemaphoreSlim(1, 1);
        var lastActivity = Stopwatch.GetTimestamp();
        var taskId = 0;
        StreamWriter? writer = null;

        async Task WriteAsync(JsonNode response)
        {
            await writeGate.WaitAsync(session.Token).ConfigureAwait(false);
            try
            {
                await writer!.WriteLineAsync(
                    response.ToJsonString().AsMemory(),
                    session.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
            }
            finally
            {
                writeGate.Release();
            }
        }

        async Task ProcessAsync(JsonNode message, string? requestKey)
        {
            CancellationTokenSource? request = null;
            try
            {
                request = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                if (requestKey is not null && !requests.TryAdd(requestKey, request))
                    throw new InvalidOperationException($"A request with id {requestKey} is already running.");

                var response = await dispatch(message, request.Token).ConfigureAwait(false);
                if (response is not null)
                    await WriteAsync(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // Session ended; there is nowhere left to send a response.
            }
            catch (Exception ex)
            {
                var response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = RequestId(message)?.DeepClone(),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32603,
                        ["message"] = ex.Message,
                    },
                };
                try { await WriteAsync(response).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* session ended */ }
            }
            finally
            {
                if (requestKey is not null)
                    requests.TryRemove(requestKey, out _);
                request?.Dispose();
            }
        }

        async Task ReapIdleSessionAsync()
        {
            if (idleTimeout <= TimeSpan.Zero || idleTimeout == Timeout.InfiniteTimeSpan)
                return;

            var interval = TimeSpan.FromSeconds(Math.Clamp(idleTimeout.TotalSeconds / 4, 1, 30));
            try
            {
                while (!session.IsCancellationRequested)
                {
                    await Task.Delay(interval, session.Token).ConfigureAwait(false);
                    if (requests.IsEmpty
                        && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)) >= idleTimeout)
                    {
                        Log.ZLogInformation($"Closing idle MCP pipe session on {PipeName}");
                        session.Cancel();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Session ended normally.
            }
        }

        try
        {
            using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var sessionWriter = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
            writer = sessionWriter;
            var idleReaper = ReapIdleSessionAsync();

            while (!session.IsCancellationRequested
                   && await reader.ReadLineAsync(session.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;

                Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                JsonNode message;
                try
                {
                    message = JsonNode.Parse(line)
                              ?? throw new InvalidDataException("Empty JSON-RPC message.");
                }
                catch (Exception ex)
                {
                    await WriteAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = null,
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32700,
                            ["message"] = $"Parse error: {ex.Message}",
                        },
                    }).ConfigureAwait(false);
                    continue;
                }

                if (TryGetCancelledRequest(message, out var cancelled))
                {
                    if (requests.TryGetValue(cancelled, out var request))
                        request.Cancel();
                    continue;
                }

                var key = RequestKey(message);
                var id = Interlocked.Increment(ref taskId);
                var task = ProcessAsync(message, key);
                tasks[id] = task;
                _ = task.ContinueWith(
                    _ => tasks.TryRemove(id, out var removed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            session.Cancel();
            await idleReaper.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Client disconnected mid-session; normal.
        }
        catch (OperationCanceledException)
        {
            // Server stopping.
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"MCP pipe session on {PipeName} ended with an error: {ex.Message}");
        }
        finally
        {
            try { session.Cancel(); } catch (ObjectDisposedException) { /* already ending */ }
            foreach (var request in requests.Values)
                try { request.Cancel(); } catch (ObjectDisposedException) { /* completed concurrently */ }

            try
            {
                await Task.WhenAll(tasks.Values).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // A handler may not observe cancellation; the closed pipe still releases the slot.
            }

            try
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
            catch (Exception)
            {
                // Already gone.
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JsonNode? RequestId(JsonNode message) => message switch
    {
        JsonObject single => single["id"],
        JsonArray batch => batch.OfType<JsonObject>().Select(entry => entry["id"]).FirstOrDefault(id => id is not null),
        _ => null,
    };

    private static string? RequestKey(JsonNode message) => RequestId(message)?.ToJsonString();

    private static bool TryGetCancelledRequest(JsonNode message, out string requestKey)
    {
        requestKey = "";
        if (message is not JsonObject notification
            || notification["method"]?.GetValue<string>() != "notifications/cancelled"
            || notification["params"]?["requestId"] is not { } requestId)
        {
            return false;
        }

        requestKey = requestId.ToJsonString();
        return true;
    }

    /// <summary>
    /// Creates a pipe instance readable and writable only by the account running the app
    /// (plus SYSTEM). This is what replaces the URL token of the HTTP transport: other
    /// users on the machine are refused by the kernel, not by a secret we have to store.
    /// </summary>
    private NamedPipeServerStream CreateServerStream()
    {
        var security = new PipeSecurity();
        if (WindowsIdentity.GetCurrent().User is { } user)
        {
            security.AddAccessRule(new PipeAccessRule(
                user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            Math.Max(1, maxSessions),
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
