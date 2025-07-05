using System.Diagnostics;
using System.IO.Pipes;

namespace JeekTools;

public class SingleInstance : IDisposable
{
    private Mutex? _mutex;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private const string ShowWindowMessage = "SHOW_WINDOW";

    private NamedPipeServerStream? _pipeServer;
    private CancellationTokenSource? _cancellationTokenSource;

    public SingleInstance(string uniqueName)
    {
        _mutexName = $"{uniqueName}_SingleInstance_Mutex";
        _pipeName = $"{uniqueName}_SingleInstance_IPC_Pipe";

        // Check if another instance is already running
        _mutex = new Mutex(true, _mutexName, out var isNew);

        if (!isNew)
        {
            // Another instance is already running, send message to show window
            SendShowWindowMessage();
            _mutex.Dispose();
            _mutex = null;
            IsRunning = true;
        }
    }

    public bool IsRunning { get; private set; } = false;

    private void SendShowWindowMessage()
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            pipeClient.Connect(1000); // Wait up to 1 second
            using var writer = new StreamWriter(pipeClient);
            writer.WriteLine(ShowWindowMessage);
            writer.Flush();
        }
        catch (Exception ex)
        {
            // If pipe communication fails, fallback to window enumeration
            Debug.WriteLine($"Failed to send IPC message: {ex.Message}");
        }
    }

    public void StartIPCServer(Action showWindowCallback)
    {
        _cancellationTokenSource = new CancellationTokenSource();

        Task.Run(async () =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    _pipeServer = new NamedPipeServerStream(_pipeName, PipeDirection.In);
                    await _pipeServer.WaitForConnectionAsync(_cancellationTokenSource.Token);

                    using var reader = new StreamReader(_pipeServer);
                    var message = await reader.ReadLineAsync();

                    if (message == ShowWindowMessage)
                    {
                        showWindowCallback?.Invoke();
                    }

                    _pipeServer.Disconnect();
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"IPC Server error: {ex.Message}");
                    await Task.Delay(1000, _cancellationTokenSource.Token); // Wait before retrying
                }
                finally
                {
                    _pipeServer?.Dispose();
                    _pipeServer = null;
                }
            }
        }, _cancellationTokenSource.Token);
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _pipeServer?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        GC.SuppressFinalize(this);
    }
}