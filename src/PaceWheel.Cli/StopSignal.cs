namespace PaceWheel.Cli;

/// <summary>
/// Emergency stop: set by Ctrl+C, by Esc (when a console is attached) and on process exit.
/// The handlers only set a flag; the main thread does the DirectInput calls.
/// </summary>
internal sealed class StopSignal : IDisposable
{
    private readonly ManualResetEventSlim _event = new();
    private readonly bool _canReadKeys = !Console.IsInputRedirected;

    public StopSignal()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public bool IsSet
    {
        get
        {
            PollEscape();
            return _event.IsSet;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/>; returns true as soon as a stop was requested.</summary>
    public bool Wait(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsSet)
            {
                return true;
            }
            _event.Wait(TimeSpan.FromMilliseconds(10));
        }
        return IsSet;
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private void PollEscape()
    {
        if (!_canReadKeys)
        {
            return;
        }
        while (Console.KeyAvailable)
        {
            if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
            {
                _event.Set();
            }
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _event.Set();
    }

    private void OnProcessExit(object? sender, EventArgs e) => _event.Set();
}
