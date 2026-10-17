using System.Diagnostics;
using PaceWheel.Core;
using PaceWheel.Core.DirectInput;
using PaceWheel.Core.Hid;
using Vortice.DirectInput;

namespace PaceWheel.Cli.Commands;

internal static class DirectInputCommands
{
    private static readonly (Guid Guid, string Name)[] AxisGuids =
    [
        (ObjectGuid.XAxis, "X"), (ObjectGuid.YAxis, "Y"), (ObjectGuid.ZAxis, "Z"),
        (ObjectGuid.RxAxis, "Rx"), (ObjectGuid.RyAxis, "Ry"), (ObjectGuid.RzAxis, "Rz"),
    ];

    public static int Info(TextWriter output)
    {
        using var directInput = new WheelDirectInput();
        var controllers = directInput.GameControllers();
        output.WriteLine($"DirectInput game controllers attached: {controllers.Count}");
        foreach (var c in controllers)
        {
            string marker = WheelIds.MatchesProductGuid(c.ProductGuid) ? "  <- wheel" : "";
            output.WriteLine($"  {c.InstanceName}  (product {c.ProductGuid}){marker}");
        }
        output.WriteLine();

        var instance = directInput.FindWheel();
        if (instance is null)
        {
            output.WriteLine("The wheel was not found among DirectInput game controllers.");
            return 1;
        }

        using var device = directInput.CreateDevice(instance);
        var info = DirectInputDeviceInfo.Read(instance, device);
        var caps = info.Capabilities;

        output.WriteLine("--- Device");
        output.WriteLine($"  Instance name:     {instance.InstanceName}");
        output.WriteLine($"  Product name:      {instance.ProductName}");
        output.WriteLine($"  Instance GUID:     {instance.InstanceGuid}");
        output.WriteLine($"  Product GUID:      {instance.ProductGuid}");
        output.WriteLine($"  FFB driver GUID:   {instance.ForceFeedbackDriverGuid}");
        output.WriteLine($"  Type / subtype:    {instance.Type} / {instance.Subtype}");
        output.WriteLine($"  HID:               {instance.IsHumanInterfaceDevice}, usage page 0x{(int)instance.UsagePage:X2}, usage 0x{(int)instance.Usage:X2}");
        output.WriteLine();

        output.WriteLine("--- Capabilities");
        output.WriteLine($"  Flags:             {caps.Flags}");
        output.WriteLine($"  Axes / buttons / POVs: {caps.AxeCount} / {caps.ButtonCount} / {caps.PovCount}");
        output.WriteLine($"  FFB sample period: {caps.ForceFeedbackSamplePeriod} µs, min time resolution {caps.ForceFeedbackMinimumTimeResolution} µs");
        output.WriteLine($"  Firmware / hardware / driver version: 0x{caps.FirmwareRevision:X} / 0x{caps.HardwareRevision:X} / 0x{caps.DriverVersion:X}");
        output.WriteLine($"  FFB state:         {info.ForceFeedbackState ?? "(needs exclusive acquire, DIERR_NOTEXCLUSIVEACQUIRED)"}");
        output.WriteLine();

        output.WriteLine("--- Properties");
        foreach (var (name, value) in info.Properties)
        {
            output.WriteLine($"  {name,-36} {value}");
        }
        output.WriteLine();

        output.WriteLine($"--- Objects ({info.Objects.Count})");
        output.WriteLine("  (Name is localized by Windows; HID usage is the English name from the usage tables.)");
        output.WriteLine($"  {"Offset",6}  {"Type",-12} {"Name",-28} {"Inst",4}  {"Usage",-9} {"HID usage",-28} {"Rpt",4}  Details");
        foreach (var o in info.Objects)
        {
            var details = new List<string>();
            if (o.Range is { } r) details.Add($"range {r.Minimum}..{r.Maximum}");
            if (o.DeadZone is int dz) details.Add($"deadzone {dz}");
            if (o.Saturation is int sat) details.Add($"saturation {sat}");
            if (o.IsForceFeedbackActuator) details.Add("FFB actuator");
            output.WriteLine($"  {o.Offset,6}  {o.Type,-12} {o.Name,-28} {o.InstanceNumber,4}  {o.UsagePage:X2}:{o.Usage:X4}   {HidUsageNames.Usage(o.UsagePage, o.Usage),-28} {o.ReportId,4}  {string.Join(", ", details)}");
        }
        output.WriteLine();

        output.WriteLine($"--- Force feedback effects ({info.Effects.Count})");
        foreach (var e in info.Effects)
        {
            output.WriteLine($"  {e.Name,-24} {EffectTypeText(e.Type),-40} {e.Guid}");
            output.WriteLine($"  {"",-24} static: {e.StaticParameters}");
            output.WriteLine($"  {"",-24} dynamic: {e.DynamicParameters}");
        }
        return 0;
    }

    public static int Monitor(string[] args)
    {
        int? seconds = IntOption(args, "--seconds");
        int threshold = IntOption(args, "--threshold") ?? 64;
        string? logPath = StringOption(args, "--log");
        bool dashboard = !Console.IsOutputRedirected && !Console.IsInputRedirected && !args.Contains("--changes");

        using var directInput = new WheelDirectInput();
        var instance = directInput.FindWheel();
        if (instance is null)
        {
            Console.Error.WriteLine("The wheel was not found among DirectInput game controllers.");
            return 1;
        }

        using var window = new MessageWindow();
        using var device = directInput.CreateDevice(instance);
        var caps = device.Capabilities;
        var presentAxes = PresentAxes(device);

        using var log = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
        log?.WriteLine($"# monitor session {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {instance.InstanceName}");

        var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        Console.CancelKeyPress += onCancel;

        var stats = new InputSessionStats();
        var clock = Stopwatch.StartNew();
        int dashboardTop = 0;
        try
        {
            using var monitor = new InputMonitor(device, window.Handle);
            WheelInputSnapshot? previous = null;
            WheelInputSnapshot? defaults = null;

            if (dashboard)
            {
                Console.CursorVisible = false;
                Console.WriteLine($"{instance.InstanceName}: live input. Press Esc or Ctrl+C to quit.");
                Console.WriteLine("(Values appear after the wheel sends its first report; nudge the wheel if nothing shows.)");
                dashboardTop = Console.CursorTop;
            }
            else
            {
                Console.WriteLine($"{instance.InstanceName}: printing input changes (threshold {threshold}). Ctrl+C to quit.");
            }

            while (!stop.IsSet)
            {
                if (seconds is int s && clock.Elapsed.TotalSeconds >= s)
                {
                    break;
                }
                if (dashboard && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                {
                    break;
                }

                var snapshot = WheelInputSnapshot.From(monitor.Read(), presentAxes, caps.ButtonCount, caps.PovCount);

                // Right after Acquire, DirectInput returns default (centered) values until the wheel
                // sends its first input report, which it only does when something changes.
                defaults ??= snapshot;
                if (previous is null && !snapshot.DiffFrom(defaults, 1).Any())
                {
                    stop.Wait(15);
                    continue;
                }

                stats.Add(snapshot);

                if (previous is null)
                {
                    string initial = $"{clock.ElapsedMilliseconds,7} ms  first report  " +
                        string.Join(" ", snapshot.Axes.Select(a => $"{a.Name}={a.Value}"));
                    log?.WriteLine(initial);
                    if (!dashboard)
                    {
                        Console.WriteLine(initial);
                    }
                    previous = snapshot;
                }
                else
                {
                    var changes = snapshot.DiffFrom(previous, threshold).ToList();
                    if (changes.Count > 0)
                    {
                        string line = $"{clock.ElapsedMilliseconds,7} ms  {string.Join("  ", changes)}";
                        log?.WriteLine(line);
                        if (!dashboard)
                        {
                            Console.WriteLine(line);
                        }
                        // The reference only moves on a reported change, so slow axis movement
                        // still shows up once it adds up to the threshold.
                        previous = snapshot;
                    }
                }

                if (dashboard)
                {
                    DrawDashboard(dashboardTop, snapshot);
                }

                stop.Wait(15);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (dashboard)
            {
                Console.CursorVisible = true;
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- Session summary");
        log?.WriteLine("# summary");
        foreach (var line in stats.Summary())
        {
            Console.WriteLine(line);
            log?.WriteLine(line);
        }
        return 0;
    }

    private static void DrawDashboard(int top, WheelInputSnapshot s)
    {
        const int barWidth = 40;
        Console.SetCursorPosition(0, top);
        foreach (var (name, value) in s.Axes)
        {
            // DirectInput maps every HID axis to its default range 0..65535.
            int filled = Math.Clamp((int)Math.Round(value / 65535.0 * barWidth), 0, barWidth);
            Console.WriteLine($"{name,-3} {value,6} [{new string('#', filled)}{new string('.', barWidth - filled)}]".PadRight(Console.WindowWidth - 1));
        }
        for (int i = 0; i < s.Povs.Count; i++)
        {
            Console.WriteLine($"POV{i} {WheelInputSnapshot.PovText(s.Povs[i]),-8}".PadRight(Console.WindowWidth - 1));
        }

        var grid = string.Join(" ", s.Buttons.Chunk(8).Select(chunk => new string(chunk.Select(b => b ? 'X' : '.').ToArray())));
        var pressed = s.Buttons.Select((b, i) => (b, i)).Where(t => t.b).Select(t => (t.i + 1).ToString());
        Console.WriteLine($"Buttons {grid}  pressed: {string.Join(",", pressed)}".PadRight(Console.WindowWidth - 1));
    }

    private static HashSet<string> PresentAxes(IDirectInputDevice8 device)
    {
        var names = new HashSet<string>();
        int sliders = 0;
        foreach (var o in device.GetObjects(DeviceObjectTypeFlags.Axis))
        {
            var match = AxisGuids.FirstOrDefault(a => a.Guid == o.ObjectType);
            if (match.Name is not null)
            {
                names.Add(match.Name);
            }
            else if (o.ObjectType == ObjectGuid.Slider && sliders < 2)
            {
                names.Add($"S{sliders++}");
            }
        }
        return names;
    }

    private static string EffectTypeText(EffectType type)
    {
        int baseType = (int)type & 0xFF;
        string name = baseType switch
        {
            1 => "ConstantForce",
            2 => "RampForce",
            3 => "Periodic",
            4 => "Condition",
            5 => "CustomForce",
            0xFF => "Hardware",
            _ => $"0x{baseType:X2}",
        };
        var flags = new[]
        {
            EffectType.Attack, EffectType.Fade, EffectType.Saturation, EffectType.TwoCoefficients,
            EffectType.TwoSaturations, EffectType.DeadBand, EffectType.StartDelay,
        }.Where(f => (type & f) != 0).Select(f => f.ToString());
        return string.Join(", ", new[] { name }.Concat(flags));
    }

    private static int? IntOption(string[] args, string name) =>
        StringOption(args, name) is string s && int.TryParse(s, out int v) ? v : null;

    private static string? StringOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
