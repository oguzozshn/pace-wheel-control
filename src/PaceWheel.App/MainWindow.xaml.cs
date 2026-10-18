using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaceWheel.Core.Ble;
using PaceWheel.Core.DirectInput;
using Vortice.DirectInput;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PaceWheel.App;

public partial class MainWindow : FluentWindow
{
    // Steering, throttle, brake, clutch (docs/device-notes.md, input mapping).
    private static readonly IReadOnlySet<string> Axes = new HashSet<string> { "X", "Z", "Rz", "S0" };

    // H-shifter gears 1-6 are buttons 17-22, reverse is 23.
    private const int FirstGearButton = 17;
    private const int ReverseButton = 23;

    private readonly DispatcherTimer _inputTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<Ellipse> _buttonDots = new();
    private WheelDirectInput? _directInput;
    private IDirectInputDevice8? _device;
    private InputMonitor? _monitor;
    private int _buttonCount;
    private int _povCount;

    private WheelSettings? _current;
    private WheelSettings? _beforeLastChange;
    private WheelKeyMap? _keyMap;
    private readonly Dictionary<byte, ComboBox> _mapCombos = new();
    private List<(byte Key, byte Old, byte New)>? _lastMapChanges;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _inputTimer.Tick += (_, _) => PollInput();
        AngleSetting.ValueChanged += (_, _) => UpdateApplyState();
        FfbSetting.ValueChanged += (_, _) => UpdateApplyState();
        SpringSetting.ValueChanged += (_, _) => UpdateApplyState();
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

    // ---- Navigation: home screen and detail pages -------------------------------------------

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        string page = (string)((FrameworkElement)sender).Tag;
        HomeView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        var nav = page switch { "Mapping" => NavMapping, "Log" => NavLog, _ => NavSettings };
        if (nav.IsChecked == true)
        {
            ShowPage(page);
        }
        nav.IsChecked = true;
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        DetailView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
    }

    private void OnNavChecked(object sender, RoutedEventArgs e) => ShowPage((string)((FrameworkElement)sender).Tag);

    private void ShowPage(string page)
    {
        // Checked fires during InitializeComponent, before the pages exist.
        if (PageSettings is null || PageMapping is null || PageLog is null)
        {
            return;
        }
        PageSettings.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageMapping.Visibility = page == "Mapping" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
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
                SetBadge(UsbDot, UsbText, false, "USB: bulunamadı");
                DeviceSubtitle.Text = "Direksiyon USB'de bulunamadı. Kabloyu kontrol et ve uygulamayı yeniden başlat.";
                return;
            }
            _device = _directInput.CreateDevice(instance);
            _buttonCount = _device.Capabilities.ButtonCount;
            _povCount = _device.Capabilities.PovCount;
            _monitor = new InputMonitor(_device, new WindowInteropHelper(this).Handle);
            CreateButtonDots(_buttonCount);
            SetBadge(UsbDot, UsbText, true, "USB: bağlı");
            DeviceSubtitle.Text = $"{instance.InstanceName} · canlı giriş (salt okunur)";
            _inputTimer.Start();
        }
        catch (Exception ex)
        {
            SetBadge(UsbDot, UsbText, false, "USB: hata");
            DeviceSubtitle.Text = $"Giriş okunamıyor: {ex.Message}";
        }
    }

    private void CreateButtonDots(int count)
    {
        ButtonGrid.Children.Clear();
        _buttonDots.Clear();
        for (int i = 0; i < count; i++)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Margin = new Thickness(3), Fill = (Brush)FindResource("TrackBrush"), ToolTip = $"Düğme {i + 1}" };
            _buttonDots.Add(dot);
            ButtonGrid.Children.Add(dot);
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
            SetBadge(UsbDot, UsbText, false, "USB: bağlantı koptu");
            DeviceSubtitle.Text = $"Giriş kesildi: {ex.Message}";
            return;
        }

        var axes = s.Axes.ToDictionary(a => a.Name, a => a.Value);
        WheelView.SetPosition(axes.GetValueOrDefault("X", 32767));
        ThrottlePedal.SetValue(axes.GetValueOrDefault("Z"));
        BrakePedal.SetValue(axes.GetValueOrDefault("Rz"));
        ClutchPedal.SetValue(axes.GetValueOrDefault("S0"));
        PovText.Text = s.Povs.Count > 0 ? PovArrow(s.Povs[0]) : "•";

        var accent = (Brush)FindResource("RacingAccentBrush");
        var idle = (Brush)FindResource("TrackBrush");
        int gear = 0;
        for (int i = 0; i < s.Buttons.Length && i < _buttonDots.Count; i++)
        {
            _buttonDots[i].Fill = s.Buttons[i] ? accent : idle;
            int number = i + 1;
            if (s.Buttons[i] && number >= FirstGearButton && number <= ReverseButton)
            {
                gear = number;
            }
        }
        GearText.Text = gear == 0 ? "N" : gear == ReverseButton ? "R" : (gear - FirstGearButton + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string PovArrow(int pov) => pov < 0 || pov == 0xFFFF ? "•" : (pov / 4500) switch
    {
        0 => "↑", 1 => "↗", 2 => "→", 3 => "↘", 4 => "↓", 5 => "↙", 6 => "←", 7 => "↖", _ => "•",
    };

    // ---- Wheel settings over BLE ------------------------------------------------------------

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        await RunBusy("Direksiyon aranıyor (10 sn, aktif tarama)…", async () =>
        {
            var found = (await WheelBle.ScanAsync(TimeSpan.FromSeconds(10), active: true, CancellationToken.None))
                .FirstOrDefault(d => d.Name == WheelBle.WheelName || d.ServiceUuids.Contains(WheelBle.ConfigService));
            if (found is null)
            {
                Log("Direksiyon bulunamadı. USB kablosu takılı ve mobil uygulama kapalı mı?");
                Notify("Direksiyon bulunamadı", "USB kablosu takılı ve mobil uygulama kapalı olmalı.", ControlAppearance.Caution);
                return;
            }
            AddressBox.Text = found.AddressText;
            SetBadge(BleDot, BleText, true, "Bluetooth: bulundu");
            Log($"Bulundu: {found.Name ?? "(isimsiz)"} · {found.AddressText} · {found.BestRssi} dBm");
        });
    }

    private async void OnRead(object sender, RoutedEventArgs e)
    {
        if (ParseAddress() is not ulong address)
        {
            return;
        }
        await RunBusy("Ayarlar okunuyor (50 01, 51 <mod> 02)…", async () =>
        {
            ShowSettings(await WheelSettings.ReadAsync(address));
            Log($"Okundu: {_current}");
            Notify("Ayarlar okundu", $"Açı {_current!.AngleDegrees}°, FFB {_current.FfbStrength}, yay {_current.Spring}", ControlAppearance.Info);
        });
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_current is null || ParseAddress() is not ulong address)
        {
            return;
        }
        var commands = _current.BuildChangeCommands(AngleSetting.Value, FfbSetting.Value, SpringSetting.Value);
        await SendChange(address, _current, commands, "Uygula");
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_current is null || _beforeLastChange is not { } old || ParseAddress() is not ulong address)
        {
            return;
        }
        var commands = _current.BuildChangeCommands(old.AngleDegrees, old.FfbStrength, old.Spring);
        await SendChange(address, _current, commands, "Geri al");
    }

    /// <summary>Shows the exact bytes, asks for confirmation, backs up, sends and reads back.</summary>
    private async Task SendChange(ulong address, WheelSettings before, IReadOnlyList<byte[]> commands, string what)
    {
        if (commands.Count == 0)
        {
            Notify("Değişiklik yok", "Direksiyondaki değerlerle aynı.", ControlAppearance.Secondary);
            return;
        }

        if (!await ConfirmBytesAsync(what, commands, "Ardından ayarlar geri okunup kontrol edilir (50 01, 51 <mod> 02). Önce yedek alınır. Başka hiçbir şey yazılmaz."))
        {
            return;
        }

        await RunBusy($"{what}: {commands.Count} komut gönderiliyor…", async () =>
        {
            Log($"Yedek kaydedildi: {SaveBackup(address, before)}");
            foreach (var c in commands)
            {
                Log($"Gönderiliyor: {BitConverter.ToString(c).Replace('-', ' ')}");
            }
            var after = await WheelSettings.ApplyAsync(address, commands);
            _beforeLastChange = before;
            ShowSettings(after);
            Log($"Geri okundu: {after}");
            Notify($"{what} tamam", "Ayarlar direksiyona yazıldı ve geri okunarak doğrulandı.", ControlAppearance.Success);
        });
    }

    /// <summary>Shows every byte that will be written and returns true only on an explicit "Gönder".</summary>
    private async Task<bool> ConfirmBytesAsync(string what, IReadOnlyList<byte[]> commands, string afterwards)
    {
        string bytes = string.Join(Environment.NewLine, commands.Select(c => "0xFFE1 ← " + BitConverter.ToString(c).Replace('-', ' ')));
        var dialog = new ContentDialog(DialogHost)
        {
            Title = $"{what}: baytları onayla",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Direksiyona sırayla şunlar yazılacak:", Margin = new Thickness(0, 0, 0, 8) },
                    new System.Windows.Controls.TextBox
                    {
                        Text = $"0xFFE2 CCCD ← 01 00{Environment.NewLine}{bytes}{Environment.NewLine}0xFFE2 CCCD ← 00 00",
                        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.NoWrap,
                        MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    new TextBlock { Text = afterwards, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.75 },
                },
            },
            PrimaryButtonText = "Gönder",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonAppearance = ControlAppearance.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            return true;
        }
        Log($"{what} iptal edildi; hiçbir şey gönderilmedi.");
        return false;
    }

    // ---- Button mapping ---------------------------------------------------------------------

    private async void OnReadMap(object sender, RoutedEventArgs e)
    {
        if (ParseAddress() is not ulong address)
        {
            return;
        }
        await RunBusy("Tuş haritası okunuyor (51 <mod> 00..02)…", async () =>
        {
            int mode = _current?.Mode ?? (await WheelSettings.ReadAsync(address)).Mode;
            ShowKeyMap(await WheelKeyMap.ReadAsync(address, mode));
            Log($"Tuş haritası okundu (mod {mode}).");
        });
    }

    private async void OnApplyMap(object sender, RoutedEventArgs e)
    {
        if (_keyMap is null || ParseAddress() is not ulong address)
        {
            return;
        }
        var changes = new List<(byte Key, byte Old, byte New)>();
        foreach (var (key, combo) in _mapCombos)
        {
            byte old = _keyMap.TargetOf(key) ?? key;
            if (combo.SelectedValue is byte target && target != old)
            {
                changes.Add((key, old, target));
            }
        }
        await SendKeyMapChanges(address, changes, "Tuş atama");
    }

    private async void OnUndoMap(object sender, RoutedEventArgs e)
    {
        if (_keyMap is null || _lastMapChanges is null || ParseAddress() is not ulong address)
        {
            return;
        }
        var changes = _lastMapChanges.Select(c => (c.Key, c.New, c.Old)).ToList();
        await SendKeyMapChanges(address, changes, "Tuş atamayı geri al");
    }

    private async Task SendKeyMapChanges(ulong address, List<(byte Key, byte Old, byte New)> changes, string what)
    {
        if (_keyMap is null || changes.Count == 0)
        {
            Notify("Değişiklik yok", "Seçimler direksiyondaki haritayla aynı.", ControlAppearance.Secondary);
            return;
        }
        var map = _keyMap;
        var commands = changes.Select(c => map.RemapCommand(c.Key, c.New)).ToList();
        string summary = string.Join(", ", changes.Select(c => $"{KeyName(c.Key)} → {WheelKeyMap.NameOf(c.New)}"));
        if (!await ConfirmBytesAsync(what, commands, $"{summary}. Ardından tuş haritası geri okunup kontrol edilir (51 <mod> 00..02). Önce yedek alınır."))
        {
            return;
        }

        await RunBusy($"{what}: {commands.Count} komut gönderiliyor…", async () =>
        {
            Log($"Yedek kaydedildi: {SaveMapBackup(address, map)}");
            foreach (var c in commands)
            {
                Log($"Gönderiliyor: {BitConverter.ToString(c).Replace('-', ' ')}");
            }
            await WheelBle.ExchangeAsync(address, commands, TimeSpan.FromSeconds(2));
            var after = await WheelKeyMap.ReadAsync(address, map.Mode);
            bool ok = changes.All(c => after.TargetOf(c.Key) == c.New);
            _lastMapChanges = changes;
            ShowKeyMap(after);
            Log(ok ? $"Geri okundu, doğrulandı: {summary}" : "UYARI: geri okunan harita istenenle uyuşmuyor.");
            Notify(ok ? $"{what} tamam" : "Doğrulanamadı",
                ok ? "Tuş haritası direksiyona yazıldı ve geri okunarak doğrulandı." : "Geri okunan harita istenenle uyuşmuyor; kaydı kontrol et.",
                ok ? ControlAppearance.Success : ControlAppearance.Danger);
        });
    }

    private void ShowKeyMap(WheelKeyMap map)
    {
        _keyMap = map;
        _mapCombos.Clear();
        MapGrid.Children.Clear();
        MapGrid.RowDefinitions.Clear();
        MapModeText.Text = $"mod {map.Mode}";

        int row = 0;
        foreach (var key in WheelKeyMap.RemappableKeys)
        {
            if (map.TargetOf(key.Code) is not byte current)
            {
                continue;
            }
            MapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock { Text = key.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            var combo = new ComboBox
            {
                ItemsSource = WheelKeyMap.Targets,
                DisplayMemberPath = nameof(WheelKey.Name),
                SelectedValuePath = nameof(WheelKey.Code),
                SelectedValue = current,
                Margin = new Thickness(0, 4, 8, 4),
            };
            var badge = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            byte keyCode = key.Code;
            UpdateMapBadge(badge, keyCode, current, current);
            combo.SelectionChanged += (_, _) =>
            {
                UpdateMapBadge(badge, keyCode, current, combo.SelectedValue is byte b ? b : current);
                UpdateMapApplyState();
            };

            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            Grid.SetRow(combo, row);
            Grid.SetColumn(combo, 1);
            Grid.SetRow(badge, row);
            Grid.SetColumn(badge, 2);
            MapGrid.Children.Add(name);
            MapGrid.Children.Add(combo);
            MapGrid.Children.Add(badge);
            _mapCombos[key.Code] = combo;
            row++;
        }
        UpdateMapApplyState();
    }

    private void UpdateMapBadge(TextBlock badge, byte key, byte onWheel, byte selected)
    {
        if (selected != onWheel)
        {
            badge.Text = "● değişti";
            badge.Foreground = (Brush)FindResource("RacingAccentBrush");
        }
        else if (onWheel != key)
        {
            badge.Text = "özel";
            badge.Foreground = (Brush)FindResource("MutedTextBrush");
        }
        else
        {
            badge.Text = "";
        }
    }

    private void UpdateMapApplyState()
    {
        if (ApplyMapButton is null || UndoMapButton is null)
        {
            return;
        }
        ApplyMapButton.IsEnabled = !_busy && _keyMap is not null &&
            _mapCombos.Any(kv => kv.Value.SelectedValue is byte b && b != (_keyMap.TargetOf(kv.Key) ?? kv.Key));
        UndoMapButton.IsEnabled = !_busy && _lastMapChanges is not null;
    }

    private static string KeyName(byte code) =>
        WheelKeyMap.RemappableKeys.FirstOrDefault(k => k.Code == code)?.Name ?? $"0x{code:X2}";

    private static string SaveMapBackup(ulong address, WheelKeyMap map)
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaceWheel", "backups");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"keymap-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var lines = new List<string> { $"# {BleSighting.FormatAddress(address)} mode {map.Mode} {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        foreach (var k in WheelKeyMap.RemappableKeys)
        {
            byte target = map.TargetOf(k.Code) ?? 0;
            lines.Add($"{k.Name} (0x{k.Code:X2}) -> 0x{target:X2} {WheelKeyMap.NameOf(target)}");
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string SaveBackup(ulong address, WheelSettings settings)
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaceWheel", "backups");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, $"# {BleSighting.FormatAddress(address)} {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{settings}{Environment.NewLine}");
        return path;
    }

    private void ShowSettings(WheelSettings settings)
    {
        _current = settings;
        AngleSetting.Load(settings.AngleDegrees);
        FfbSetting.Load(settings.FfbStrength);
        SpringSetting.Load(settings.Spring);
        WheelView.RangeDegrees = settings.AngleDegrees;
        OtherSettingsText.Text = $"Sönüm {settings.Damper} · sürtünme {settings.Friction} · atalet {settings.Inertia} · firmware 0x{settings.FirmwareVersion:X4} · mod {settings.Mode}";
        SetBadge(BleDot, BleText, true, "Bluetooth: okundu");
        UpdateApplyState();
    }

    private void UpdateApplyState()
    {
        ApplyButton.IsEnabled = !_busy && _current is not null && (AngleSetting.IsChanged || FfbSetting.IsChanged || SpringSetting.IsChanged);
        UndoButton.IsEnabled = !_busy && _beforeLastChange is not null;
    }

    private ulong? ParseAddress()
    {
        string hex = AddressBox.Text.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            return value;
        }
        Notify("Adres gerekli", "Bluetooth adresini yaz ya da \"Bul\"a bas.", ControlAppearance.Caution);
        return null;
    }

    private async Task RunBusy(string message, Func<Task> action)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        FindButton.IsEnabled = ReadButton.IsEnabled = ReadMapButton.IsEnabled = false;
        UpdateApplyState();
        UpdateMapApplyState();
        BusyRing.Visibility = Visibility.Visible;
        Log(message);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"Hata: {ex.Message}");
            SetBadge(BleDot, BleText, false, "Bluetooth: hata");
            Notify("İşlem başarısız", ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            _busy = false;
            FindButton.IsEnabled = ReadButton.IsEnabled = ReadMapButton.IsEnabled = true;
            BusyRing.Visibility = Visibility.Collapsed;
            UpdateApplyState();
            UpdateMapApplyState();
        }
    }

    private void SetBadge(Ellipse dot, TextBlock text, bool ok, string label)
    {
        dot.Fill = (Brush)FindResource(ok ? "OkBrush" : "OffBrush");
        text.Text = label;
    }

    private void Notify(string title, string message, ControlAppearance appearance)
    {
        new Snackbar(SnackbarHost)
        {
            Title = title,
            Content = message,
            Appearance = appearance,
            Timeout = TimeSpan.FromSeconds(4),
        }.Show();
    }

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
