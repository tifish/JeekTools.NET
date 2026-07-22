using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

public sealed class DebugMcpHostOptions
{
    /// <summary>MCP serverInfo.name, e.g. "myapp-debug".</summary>
    public required string ServerName { get; init; }

    /// <summary>MCP serverInfo.title.</summary>
    public required string ServerTitle { get; init; }

    /// <summary>Object-path engine backing the standard tools.</summary>
    public required ObjectGraph Graph { get; init; }

    /// <summary>serverInfo.version, e.g. the build number.</summary>
    public Func<string> GetVersion { get; init; } = () => "0";

    /// <summary>Only when true does <see cref="DebugMcpHost.Start"/> listen
    /// (gate on Debug builds).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>First port to try; scans upward when taken.</summary>
    public int DefaultPort { get; init; } = 8737;

    /// <summary>How many consecutive ports to scan.</summary>
    public int PortScanCount { get; init; } = 100;

    /// <summary>Environment variable holding an explicit port override.</summary>
    public string? PortEnvironmentVariable { get; init; }

    /// <summary>Prefix for the cross-process port reservation mutex; defaults to
    /// "&lt;ServerName&gt;.DebugMcp.Port.".</summary>
    public string? PortMutexPrefix { get; init; }

    /// <summary>Marshals tool work onto the UI thread (with a timeout). Runs the
    /// function inline when unset.</summary>
    public Func<Func<object?>, Task<object?>>? UiInvoker { get; init; }

    /// <summary>Body text of the standard "describe" tool, built on the UI thread.</summary>
    public Func<string>? Describe { get; init; }

    /// <summary>Overrides the tools/list response, e.g. with a contract shared
    /// with an external stdio bridge. Defaults to a schema-less list built from
    /// the registered tool names.</summary>
    public Func<JsonArray>? ToolListProvider { get; init; }

    /// <summary>Called with the endpoint URL after the listener starts and with
    /// "" after it stops — write/remove discovery info here.</summary>
    public Action<string>? UrlChanged { get; init; }
}

/// <summary>
/// Debug MCP (Model Context Protocol) server over loopback HTTP so an AI agent
/// can inspect and drive the running app: standard tools read/write properties
/// by object path, execute commands and methods on the UI thread, list members,
/// and tail the LogManager log; apps register extra tools with
/// <see cref="AddTool"/>. Binding is loopback-only with Origin validation
/// (DNS-rebinding protection), and the port is reserved via a global mutex so
/// parallel instances scan to free ports.
/// </summary>
public sealed class DebugMcpHost
{
    public const string SupportedProtocolVersion = "2025-06-18";
    public static readonly string[] KnownProtocolVersions = ["2024-11-05", "2025-03-26", SupportedProtocolVersion];

    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DebugMcpHost));
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private readonly DebugMcpHostOptions _options;
    private readonly Dictionary<string, Func<JsonObject, Task<JsonObject>>> _tools = [];
    private HttpListener? _listener;
    private Mutex? _portReservation;

    /// <summary>Endpoint URL while listening, "" otherwise.</summary>
    public string Url { get; private set; } = "";

    public DebugMcpHost(DebugMcpHostOptions options)
    {
        _options = options;
        AddTool("describe", DescribeAsync);
        AddTool("get_value", GetValueAsync);
        AddTool("set_value", SetValueAsync);
        AddTool("invoke", InvokeAsync);
        AddTool("list_members", ListMembersAsync);
        AddTool("read_logs", args => Task.FromResult(ReadLogs(args)));
    }

    /// <summary>Registers (or replaces) a tool handler.</summary>
    public void AddTool(string name, Func<JsonObject, Task<JsonObject>> handler) =>
        _tools[name] = handler;

    public void Start()
    {
        if (!_options.Enabled || _listener != null)
            return;

        var envPort = 0;
        var hasExplicitPort = _options.PortEnvironmentVariable is { } variable
            && int.TryParse(Environment.GetEnvironmentVariable(variable), out envPort)
            && envPort > 0;
        var ports = hasExplicitPort ? [envPort] : Enumerable.Range(_options.DefaultPort, _options.PortScanCount);
        var mutexPrefix = _options.PortMutexPrefix ?? $"{_options.ServerName}.DebugMcp.Port.";

        // http.sys may require a URL ACL for the explicit 127.0.0.1 prefix;
        // "localhost" is exempt and still binds loopback only.
        Exception? lastError = null;
        foreach (var port in ports)
        {
            var reservation = new Mutex(
                initiallyOwned: true,
                mutexPrefix + port,
                out var portAvailable);
            if (!portAvailable)
            {
                reservation.Dispose();
                continue;
            }

            var started = false;
            foreach (var host in new[] { "localhost", "127.0.0.1" })
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://{host}:{port}/");
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    listener.Close();
                    continue;
                }

                _listener = listener;
                _portReservation = reservation;
                started = true;
                Url = $"http://{host}:{port}/mcp";
                _options.UrlChanged?.Invoke(Url);
                _ = Task.Run(() => ListenLoopAsync(listener));
                Log.ZLogInformation($"Debug MCP server listening on {Url}");
                return;
            }

            if (!started)
            {
                reservation.ReleaseMutex();
                reservation.Dispose();
            }
        }

        if (hasExplicitPort)
            Log.ZLogError(lastError, $"Debug MCP server could not start on explicit port {envPort}");
        else
            Log.ZLogError(lastError, $"Debug MCP server could not start on ports {_options.DefaultPort}-{_options.DefaultPort + _options.PortScanCount - 1}");
    }

    public void Stop()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // Shutting down; nothing useful to do.
        }

        _listener = null;
        if (_portReservation is { } reservation)
        {
            _portReservation = null;
            try { reservation.ReleaseMutex(); } finally { reservation.Dispose(); }
        }

        Url = "";
        _options.UrlChanged?.Invoke("");
    }

    /// <summary>Marshals a function through the configured UI invoker.</summary>
    public async Task<T> OnUiAsync<T>(Func<T> func)
    {
        if (_options.UiInvoker is not { } invoker)
            return func();

        var result = await invoker(() => func()).ConfigureAwait(false);
        return (T)result!;
    }

    public static JsonObject ToolText(string text, bool isError = false)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError)
            result["isError"] = true;
        return result;
    }

    public static JsonObject InitializeResult(string name, string title, string version, string? requestedVersion)
    {
        var protocol = KnownProtocolVersions.Contains(requestedVersion) ? requestedVersion! : SupportedProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = name, ["title"] = title, ["version"] = version },
        };
    }

    #region HTTP + JSON-RPC

    private async Task ListenLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (!listener.IsListening)
                    break;
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;

            // DNS-rebinding protection: browsers send Origin; only loopback is legit.
            var origin = request.Headers["Origin"];
            if (origin != null && !IsLoopbackOrigin(origin))
            {
                response.StatusCode = 403;
                return;
            }

            if (request.Url?.AbsolutePath is not ("/mcp" or "/"))
            {
                response.StatusCode = 404;
                return;
            }

            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                // No SSE stream support; MCP clients treat 405 on GET as "POST only".
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? responseNode;
            try
            {
                var message = JsonNode.Parse(body);
                responseNode = message switch
                {
                    JsonObject single => await HandleMessageAsync(single),
                    JsonArray batch => await HandleBatchAsync(batch),
                    _ => RpcError(null, -32600, "Invalid request"),
                };
            }
            catch (JsonException ex)
            {
                responseNode = RpcError(null, -32700, $"Parse error: {ex.Message}");
            }

            if (responseNode == null)
            {
                // Notifications get 202 Accepted with no body.
                response.StatusCode = 202;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(responseNode.ToJsonString());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Debug MCP request failed");
            try
            {
                response.StatusCode = 500;
            }
            catch
            {
                // Response already started; nothing to do.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Client may have disconnected.
            }
        }
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback;
    }

    private async Task<JsonNode?> HandleBatchAsync(JsonArray batch)
    {
        var results = new JsonArray();
        foreach (var item in batch)
        {
            if (item is not JsonObject message)
                continue;
            if (await HandleMessageAsync(message) is { } result)
                results.Add(result);
        }

        return results.Count > 0 ? results : null;
    }

    private async Task<JsonNode?> HandleMessageAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var isRequest = id != null;
        var method = message["method"]?.GetValue<string>();
        if (method == null)
            return null; // A response or malformed message; nothing to answer.

        try
        {
            switch (method)
            {
                case "initialize":
                    var requested = (message["params"] as JsonObject)?["protocolVersion"]?.GetValue<string>();
                    return RpcResult(id, InitializeResult(
                        _options.ServerName, _options.ServerTitle, _options.GetVersion(), requested));
                case "ping":
                    return RpcResult(id, new JsonObject());
                case "tools/list":
                    return RpcResult(id, new JsonObject { ["tools"] = BuildToolList() });
                case "tools/call":
                    return RpcResult(id, await HandleToolCallAsync(message["params"] as JsonObject));
                default:
                    if (method.StartsWith("notifications/", StringComparison.Ordinal))
                        return null;
                    return isRequest ? RpcError(id, -32601, $"Method not found: {method}") : null;
            }
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Debug MCP method {method} failed");
            return isRequest ? RpcError(id, -32603, ex.Message) : null;
        }
    }

    private JsonArray BuildToolList()
    {
        if (_options.ToolListProvider is { } provider)
            return provider();

        var tools = new JsonArray();
        foreach (var name in _tools.Keys)
        {
            tools.Add(new JsonObject
            {
                ["name"] = name,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            });
        }

        return tools;
    }

    private static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task<JsonObject> HandleToolCallAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("tools/call requires params.name");
        var args = parameters["arguments"] as JsonObject ?? [];

        try
        {
            if (!_tools.TryGetValue(name, out var handler))
                throw new InvalidOperationException($"Unknown tool: {name}");
            return await handler(args);
        }
        catch (Exception ex)
        {
            var error = ex is TimeoutException
                ? "Timed out waiting for the UI thread; the app may be blocked or showing a nested dialog."
                : ex.ToString();
            return ToolText(ObjectGraph.Truncate(error, 4000), isError: true);
        }
    }

    #endregion

    #region Standard tools

    private async Task<JsonObject> DescribeAsync(JsonObject args)
    {
        if (_options.Describe is not { } describe)
            return ToolText($"{_options.ServerTitle} at {Url} (version {_options.GetVersion()}).");

        return ToolText(await OnUiAsync(describe));
    }

    private async Task<JsonObject> GetValueAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        var depth = Math.Clamp(args["depth"]?.GetValue<int>() ?? 1, 0, 5);

        var node = await OnUiAsync(() => ObjectGraph.FormatValue(_options.Graph.Resolve(path), depth));
        return ToolText(node?.ToJsonString(PrettyOptions) ?? "null");
    }

    private async Task<JsonObject> SetValueAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        var valueNode = args["value"];

        await OnUiAsync<object?>(() =>
        {
            _options.Graph.SetValue(path, valueNode);
            return null;
        });

        return ToolText($"Set {path}.");
    }

    private async Task<JsonObject> InvokeAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        var callArgs = args["args"] as JsonArray ?? [];
        var depth = Math.Clamp(args["depth"]?.GetValue<int>() ?? 1, 0, 5);

        var result = await OnUiAsync(() => _options.Graph.InvokeMember(path, callArgs));

        if (result is Task task)
        {
            await task.WaitAsync(TimeSpan.FromSeconds(60));
            var taskType = task.GetType();
            result = taskType.IsGenericType && taskType.GetGenericArguments()[0].Name != "VoidTaskResult"
                ? taskType.GetProperty("Result")?.GetValue(task)
                : "Task completed.";
        }

        var node = await OnUiAsync(() => ObjectGraph.FormatValue(result, depth));
        return ToolText(node?.ToJsonString(PrettyOptions) ?? "null");
    }

    private async Task<JsonObject> ListMembersAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        return ToolText(await OnUiAsync(() => _options.Graph.ListMembers(path)));
    }

    private static JsonObject ReadLogs(JsonObject args)
    {
        var lineCount = Math.Clamp(args["lines"]?.GetValue<int>() ?? 200, 1, 2000);
        var filter = args["filter"]?.GetValue<string>();

        var path = LogManager.CurrentRollingLogFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = LogManager.CurrentLogFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("No log file found.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int tailBytes = 1024 * 1024;
        var truncated = stream.Length > tailBytes;
        if (truncated)
            stream.Seek(-tailBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        if (truncated && lines.Count > 0)
            lines.RemoveAt(0); // Likely a partial line after seeking.

        IEnumerable<string> selected = lines;
        if (!string.IsNullOrEmpty(filter))
            selected = selected.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var result = selected.TakeLast(lineCount).ToList();
        return ToolText(result.Count == 0 ? "(no matching log lines)" : string.Join('\n', result));
    }

    public static string RequiredString(JsonObject args, string name) =>
        args[name]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Missing required argument '{name}'.");

    #endregion
}
