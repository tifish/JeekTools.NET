namespace Jx3mHelperTray;

public class PreventExit : IDisposable
{
    private static int _enterCount;

    public PreventExit()
    {
        Interlocked.Increment(ref _enterCount);
    }

    public void Dispose()
    {
        Interlocked.Decrement(ref _enterCount);
    }

    public static async Task WaitForExit()
    {
        while (_enterCount > 0)
            await Task.Delay(1000);
    }
}
