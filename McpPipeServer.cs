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
    Func<JsonNode, Task<JsonNode?>> dispatch)
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
        try
        {
            using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };

            while (!token.IsCancellationRequested
                   && await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;

                JsonNode? response;
                try
                {
                    var message = JsonNode.Parse(line)
                                  ?? throw new InvalidDataException("Empty JSON-RPC message.");
                    response = await dispatch(message).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = null,
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32700,
                            ["message"] = $"Parse error: {ex.Message}",
                        },
                    };
                }

                // Notifications produce no response; keep reading.
                if (response is null)
                    continue;

                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), token).ConfigureAwait(false);
            }
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
