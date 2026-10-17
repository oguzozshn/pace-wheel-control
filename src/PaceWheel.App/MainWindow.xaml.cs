using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PaceWheel.Core.Ble;
using PaceWheel.Core.DirectInput;
using Vortice.DirectInput;

namespace PaceWheel.App;

public partial class MainWindow : Window
{
    // Steering, throttle, brake, clutch (docs/device-notes.md, input mapping).
    private static readonly IReadOnlySet<string> Axes = new HashSet<string> { "X", "Z", "Rz", "S0" };

    // H-shifter gears 1-6 are buttons 17-22, reverse is 23.
    private const int FirstGearButton = 17;

    private readonly DispatcherTimer _inputTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private WheelDirectInput? _directInput;
    private IDirectInputDevice8? _device;
    private InputMonitor? _monitor;
    private int _buttonCount;
    private int _povCount;

    private WheelSettings? _current;
    private WheelSettings? _beforeLastChange;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _inputTimer.Tick += (_, _) => PollInput();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartInput();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _inputTimer.Stop();
        _monitor?.Dispose();
        _device?.Dispose();
        _directInput?.Dispose();
    }

    // ---- Live input -------------------------------------------------------------------------

    private void StartInput()
    {
        try
        {
            _directInput = new WheelDirectInput();
            var instance = _directInput.FindWheel();
            if (instance is null)
            {
                InputStatus.Text = "Wheel not found over USB.";
                return;
            }
            _device = _directInput.CreateDevice(instance);
            _buttonCount = _device.Capabilities.ButtonCount;
            _povCount = _device.Capabilities.PovCount;
            var hwnd = new WindowInteropHelper(this).Handle;
            _monitor = new InputMonitor(_device, hwnd);
            InputStatus.Text = $"{instance.InstanceName} (non-exclusive, read-only)";
            _inputTimer.Start();
        }
        catch (Exception ex)
        {
            InputStatus.Text = $"Input not available: {ex.Message}";
        }
    }

    private void PollInput()
    {
        if (_monitor is null)
        {
            return;
        }
        WheelInputSnapshot s;
        try
        {
            s = WheelInputSnapshot.From(_monitor.Read(), Axes, _buttonCount, _povCount);
        }
        catch (Exception ex)
        {
            _inputTimer.Stop();
            InputStatus.Text = $"Input lost: {ex.Message}";
            return;
        }

        var axes = s.Axes.ToDictionary(a => a.Name, a => a.Value);
        SteeringBar.Value = axes.GetValueOrDefault("X");
        SteeringText.Text = (axes.GetValueOrDefault("X") - 32767).ToString(CultureInfo.InvariantCulture);
        ThrottleBar.Value = axes.GetValueOrDefault("Z");
        BrakeBar.Value = axes.GetValueOrDefault("Rz");
        ClutchBar.Value = axes.GetValueOrDefault("S0");
        PovText.Text = s.Povs.Count > 0 ? $"D-pad: {WheelInputSnapshot.PovText(s.Povs[0])}" : "D-pad: -";

        var pressed = s.Buttons.Select((down, i) => (down, number: i + 1)).Where(b => b.down).Select(b => b.number).ToList();
        ButtonsText.Text = "Buttons: " + (pressed.Count == 0 ? "-" : string.Join(", ", pressed));
        int gear = pressed.FirstOrDefault(b => b >= FirstGearButton && b <= FirstGearButton + 6);
        GearText.Text = gear == 0 ? "Gear: neutral" : gear == FirstGearButton + 6 ? "Gear: R" : $"Gear: {gear - FirstGearButton + 1}";
    }

    // ---- Wheel settings over BLE ------------------------------------------------------------

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        await RunBusy("Scanning for the wheel (10 s, active scan)...", async () =>
        {
            var found = (await WheelBle.ScanAsync(TimeSpan.FromSeconds(10), active: true, CancellationToken.None))
                .FirstOrDefault(d => d.Name == WheelBle.WheelName || d.ServiceUuids.Contains(WheelBle.ConfigService));
            if (found is null)
            {
                Log("Wheel not found. Is the USB cable plugged in and the mobile app closed?");
                return;
            }
            AddressBox.Text = found.AddressText;
            Log($"Found {found.Name ?? "(no name)"} at {found.AddressText}, rssi {found.BestRssi} dBm.");
        });
    }

    private async void OnRead(object sender, RoutedEventArgs e)
    {
        if (ParseAddress() is not ulong address)
        {
            return;
        }
        await RunBusy("Reading settings (50 01, 51 <mode> 02)...", async () =>
        {
            ShowSettings(await WheelSettings.ReadAsync(address));
            Log($"Read: {_current}");
        });
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_current is null || ParseAddress() is not ulong address)
        {
            return;
        }
        var commands = _current.BuildChangeCommands((int)AngleSlider.Value, (int)FfbSlider.Value, (int)SpringSlider.Value);
        await SendChange(address, _current, commands, "Apply");
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_current is null || _beforeLastChange is not { } old || ParseAddress() is not ulong address)
        {
            return;
        }
        var commands = _current.BuildChangeCommands(old.AngleDegrees, old.FfbStrength, old.Spring);
        await SendChange(address, _current, commands, "Undo");
    }

    /// <summary>Shows the exact bytes, asks for confirmation, backs up, sends and reads back.</summary>
    private async Task SendChange(ulong address, WheelSettings before, IReadOnlyList<byte[]> commands, string what)
    {
        if (commands.Count == 0)
        {
            Log("Nothing to change.");
            return;
        }

        string bytes = string.Join(Environment.NewLine, commands.Select(c => "  0xFFE1 <- " + BitConverter.ToString(c).Replace('-', ' ')));
        var answer = MessageBox.Show(this,
            $"{what}: these bytes will be written to the wheel, in this order:{Environment.NewLine}{Environment.NewLine}" +
            $"  0xFFE2 CCCD <- 01 00{Environment.NewLine}{bytes}{Environment.NewLine}  0xFFE2 CCCD <- 00 00{Environment.NewLine}{Environment.NewLine}" +
            "Then the settings are read back (50 01, 51 <mode> 02). Nothing else is written. Send?",
            "Confirm bytes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            Log($"{what} cancelled; nothing was sent.");
            return;
        }

        await RunBusy($"{what}: sending {commands.Count} command(s)...", async () =>
        {
            string backup = SaveBackup(address, before);
            Log($"Backup saved: {backup}");
            foreach (var c in commands)
            {
                Log($"Sending {BitConverter.ToString(c).Replace('-', ' ')}");
            }
            var after = await WheelSettings.ApplyAsync(address, commands);
            _beforeLastChange = before;
            ShowSettings(after);
            Log($"Read back: {after}");
            UndoButton.IsEnabled = true;
        });
    }

    private static string SaveBackup(ulong address, WheelSettings settings)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaceWheel", "backups");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, $"# {BleSighting.FormatAddress(address)} {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{settings}{Environment.NewLine}");
        return path;
    }

    private void ShowSettings(WheelSettings settings)
    {
        _current = settings;
        CurrentText.Text = $"Current: {settings}";
        AngleSlider.Value = Math.Clamp(settings.AngleDegrees, WheelSettings.MinAngle, WheelSettings.MaxAngle);
        FfbSlider.Value = settings.FfbStrength;
        SpringSlider.Value = settings.Spring;
        AngleSlider.IsEnabled = FfbSlider.IsEnabled = SpringSlider.IsEnabled = ApplyButton.IsEnabled = true;
        UpdateSliderLabels();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();

    private void UpdateSliderLabels()
    {
        // Slider minimums raise ValueChanged during InitializeComponent, before all elements exist.
        if (AngleValue is null || FfbValue is null || SpringValue is null || AngleSlider is null || FfbSlider is null || SpringSlider is null)
        {
            return;
        }
        AngleValue.Text = ((int)AngleSlider.Value).ToString(CultureInfo.InvariantCulture);
        FfbValue.Text = ((int)FfbSlider.Value).ToString(CultureInfo.InvariantCulture);
        SpringValue.Text = ((int)SpringSlider.Value).ToString(CultureInfo.InvariantCulture);
    }

    private ulong? ParseAddress()
    {
        string hex = AddressBox.Text.Replace(":", "").Replace("-", "").Trim();
        if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) && hex.Length == 12)
        {
            return value;
        }
        Log("Enter the wheel's BLE address or press \"Find wheel\".");
        return null;
    }

    private async Task RunBusy(string message, Func<Task> action)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        FindButton.IsEnabled = ReadButton.IsEnabled = ApplyButton.IsEnabled = UndoButton.IsEnabled = false;
        Log(message);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            _busy = false;
            FindButton.IsEnabled = ReadButton.IsEnabled = true;
            ApplyButton.IsEnabled = _current is not null;
            UndoButton.IsEnabled = _beforeLastChange is not null;
        }
    }

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
