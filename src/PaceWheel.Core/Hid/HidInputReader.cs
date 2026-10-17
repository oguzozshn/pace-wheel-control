using HidSharp;

namespace PaceWheel.Core.Hid;

/// <summary>
/// Reads raw input reports from one HID node on a background thread. Read-only: it never writes
/// output or feature reports. Opened in shared mode, so DirectInput and games keep working.
/// </summary>
public sealed class HidInputReader : IDisposable
{
    private readonly HidStream _stream;
    private readonly Thread _thread;
    private readonly int _reportLength;
    private volatile bool _stopping;

    public HidInputReader(WheelHidInterface hidInterface)
    {
        Interface = hidInterface;
        _reportLength = hidInterface.Device.GetMaxInputReportLength();
        _stream = hidInterface.Device.Open();
        _stream.ReadTimeout = 200;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "HID input reader" };
    }

    public WheelHidInterface Interface { get; }

    /// <summary>Raised on the reader thread for every input report (first byte is the report ID).</summary>
    public event Action<byte[]>? ReportReceived;

    /// <summary>Raised on the reader thread when reading stops because of an error.</summary>
    public event Action<Exception>? Failed;

    public void Start() => _thread.Start();

    private void ReadLoop()
    {
        var buffer = new byte[_reportLength];
        while (!_stopping)
        {
            try
            {
                int count = _stream.Read(buffer, 0, buffer.Length);
                if (count > 0)
                {
                    ReportReceived?.Invoke(buffer[..count]);
                }
            }
            catch (TimeoutException)
            {
                // No report within the timeout; the wheel only reports on changes.
            }
            catch (Exception ex) when (!_stopping)
            {
                Failed?.Invoke(ex);
                return;
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _stream.Dispose();
        if (_thread.IsAlive)
        {
            _thread.Join(500);
        }
    }
}
