using System.Globalization;
using PaceWheel.Core.Ble;

namespace PaceWheel.Cli.Commands;

/// <summary>Phase 4 read-only Bluetooth LE commands. They never write to the wheel.</summary>
internal static class BleCommands
{
    public static int Scan(string[] args)
    {
        int seconds = Math.Clamp(IntOption(args, "--seconds") ?? 10, 1, 60);
        bool active = args.Contains("--active");
        bool all = args.Contains("--all");

        Console.WriteLine($"Listening for BLE advertisements for {seconds} s ({(active ? "active: sends scan requests" : "passive: listen only")})...");
        var sightings = WheelBle.ScanAsync(TimeSpan.FromSeconds(seconds), active, CancellationToken.None).GetAwaiter().GetResult();

        var shown = all ? sightings : sightings.Where(IsCandidate).ToList();
        Console.WriteLine($"Devices seen: {sightings.Count}; {(all ? "showing all" : $"wheel candidates: {shown.Count} (use --all to list everything)")}");
        foreach (var s in shown)
        {
            string services = s.ServiceUuids.Count == 0 ? "-" : string.Join(", ", s.ServiceUuids.Select(WheelBle.ShortUuid));
            Console.WriteLine($"  {s.AddressText}  rssi {s.BestRssi,4} dBm  adv x{s.Count,-3} {(s.Connectable ? "connectable" : "           ")}  name: {s.Name ?? "(none)"}  services: {services}");
        }
        if (!all && shown.Count == 0)
        {
            Console.WriteLine("No device named like the wheel or advertising service 0xFFE0.");
            Console.WriteLine("Passive scans only see advertisement data; the name may only be in the scan response (--active).");
        }
        return 0;
    }

    public static int Gatt(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        if (address is null)
        {
            Console.WriteLine("No --address given; scanning passively for 10 s to find the wheel...");
            var candidates = WheelBle.ScanAsync(TimeSpan.FromSeconds(10), active: false, CancellationToken.None)
                .GetAwaiter().GetResult().Where(IsCandidate).ToList();
            if (candidates.Count != 1)
            {
                Console.Error.WriteLine($"Found {candidates.Count} candidates; pass --address (see ble-scan).");
                return 1;
            }
            address = candidates[0].Address;
        }

        Console.WriteLine($"Discovering GATT services of {BleSighting.FormatAddress(address.Value)} (discovery only: no reads, writes or subscriptions)...");
        var services = WheelBle.DiscoverAsync(address.Value).GetAwaiter().GetResult();
        foreach (var service in services)
        {
            string marker = service.Uuid == WheelBle.ConfigService ? "   <- configuration service (protocol-research.md)" : "";
            Console.WriteLine($"Service {WheelBle.ShortUuid(service.Uuid)}{marker}");
            foreach (var c in service.Characteristics)
            {
                string descriptors = c.Descriptors.Count == 0 ? "" : $"  descriptors: {string.Join(", ", c.Descriptors.Select(WheelBle.ShortUuid))}";
                Console.WriteLine($"  Characteristic {WheelBle.ShortUuid(c.Uuid)}  [{c.Properties}]{descriptors}");
            }
        }
        return 0;
    }

    public static int Info(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        if (address is null)
        {
            Console.Error.WriteLine("Usage: ble-info --address XX:XX:XX:XX:XX:XX (see ble-scan --active)");
            return 1;
        }

        Console.WriteLine($"Reading Device Information, GAP and 0x2901 descriptions of {BleSighting.FormatAddress(address.Value)} (reads only, nothing is written)...");
        foreach (var (what, value, error) in WheelBle.ReadInfoAsync(address.Value).GetAwaiter().GetResult())
        {
            if (value is null)
            {
                Console.WriteLine($"  {what}: (read failed: {error})");
                continue;
            }
            string text = new string(System.Text.Encoding.UTF8.GetString(value).Select(ch => char.IsControl(ch) ? '.' : ch).ToArray());
            Console.WriteLine($"  {what}: \"{text}\"  hex {Convert.ToHexString(value)}");
        }
        return 0;
    }

    /// <summary>The read-state request (protocol-research.md): type 0x50, sub-command 0x01.</summary>
    private static readonly byte[] ReadStateRequest = [0x50, 0x01];

    public static int ReadState(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        if (address is null)
        {
            Console.Error.WriteLine("Usage: ble-read-state --address XX:XX:XX:XX:XX:XX [--send]");
            return 1;
        }
        bool send = args.Contains("--send");
        bool listenFfe1 = args.Contains("--listen-ffe1");
        int wait = Math.Clamp(IntOption(args, "--wait") ?? 2, 1, 10);

        Console.WriteLine($"Target: {BleSighting.FormatAddress(address.Value)}, service 0xFFE0: commands to 0xFFE1, responses on 0xFFE2");
        Console.WriteLine("Writes, in this order:");
        Console.WriteLine("  1. CCCD (0x2902) of 0xFFE2 <- 01 00   enable notifications (standard BLE subscription)");
        Console.WriteLine($"  2. 0xFFE1 <- {BitConverter.ToString(ReadStateRequest).Replace('-', ' ')}       read state request (write with response)");
        string ffe1Note = listenFfe1 ? " and 0xFFE1 (handler only, nothing written for 0xFFE1)" : "";
        Console.WriteLine($"  3. wait {wait} s and log every notification on 0xFFE2{ffe1Note}");
        Console.WriteLine("  4. CCCD (0x2902) of 0xFFE2 <- 00 00   disable notifications, then disconnect");
        Console.WriteLine("Nothing else is written. Expected answer: D0 ... (mode, firmware version, pedal, shifter, angle mode).");

        if (!send)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run: nothing was sent. Add --send to perform these steps.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Sending...");
        var notifications = WheelBle.ExchangeAsync(address.Value, ReadStateRequest, TimeSpan.FromSeconds(wait), listenFfe1).GetAwaiter().GetResult();
        Console.WriteLine($"Notifications received: {notifications.Count}");
        foreach (var (source, n) in notifications)
        {
            Console.WriteLine($"  from {source}: {Convert.ToHexString(n)}");
            if (n.Length >= 8 && n[0] == 0xD0)
            {
                Console.WriteLine($"    state: mode {n[2]}, version 0x{n[3] | (n[4] << 8):X4}, pedal state {n[5]}, shifter state {n[6]}, angle mode {n[7]}");
            }
        }
        return 0;
    }

    /// <summary>Reads all six profile pages (51 &lt;mode&gt; &lt;page&gt;) and saves the raw answers.</summary>
    public static int Backup(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        if (address is null)
        {
            Console.Error.WriteLine("Usage: ble-backup --address XX:XX:XX:XX:XX:XX [--mode n] [--out file] [--send]");
            return 1;
        }
        int mode = IntOption(args, "--mode") ?? 1;
        if (mode is < 0 or > 255)
        {
            Console.Error.WriteLine("--mode must be 0..255 (read it with ble-read-state).");
            return 1;
        }
        string outPath = StringOption(args, "--out") ?? Path.Combine("backups", $"profile-mode{mode}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        bool send = args.Contains("--send");
        var requests = Enumerable.Range(0, 6).Select(page => new byte[] { 0x51, (byte)mode, (byte)page }).ToList();

        Console.WriteLine($"Target: {BleSighting.FormatAddress(address.Value)}, service 0xFFE0: commands to 0xFFE1, responses on 0xFFE2");
        Console.WriteLine("Writes, in this order:");
        Console.WriteLine("  1. CCCD (0x2902) of 0xFFE2 <- 01 00   enable notifications");
        for (int i = 0; i < requests.Count; i++)
        {
            Console.WriteLine($"  {i + 2}. 0xFFE1 <- {BitConverter.ToString(requests[i]).Replace('-', ' ')}    read profile page {i} (mode {mode}); wait up to 2 s for D1, then 100 ms");
        }
        Console.WriteLine($"  {requests.Count + 2}. CCCD (0x2902) of 0xFFE2 <- 00 00   disable notifications, then disconnect");
        Console.WriteLine($"Nothing else is written. Answers are saved to {outPath}.");

        if (!send)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run: nothing was sent. Add --send to perform these steps.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Sending...");
        var notifications = WheelBle.ExchangeAsync(address.Value, requests, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

        var lines = new List<string>
        {
            $"# Profile backup of {BleSighting.FormatAddress(address.Value)}, mode {mode}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "# One notification per line as received on 0xFFE2 (hex). D1 = profile page; byte 2 = page number.",
        };
        foreach (var (source, n) in notifications)
        {
            lines.Add($"{source} {Convert.ToHexString(n)}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllLines(outPath, lines);

        Console.WriteLine($"Notifications received: {notifications.Count}");
        foreach (var (source, n) in notifications)
        {
            string page = n.Length >= 3 && n[0] == 0xD1 ? $"page {n[2]}" : "";
            Console.WriteLine($"  from {source}: {Convert.ToHexString(n)}  {page}");
        }
        var pages = notifications.Where(x => x.Data.Length >= 3 && x.Data[0] == 0xD1).Select(x => (int)x.Data[2]).Distinct().Count();
        Console.WriteLine($"Profile pages received: {pages} of 6. Saved to {Path.GetFullPath(outPath)}");
        return pages == 6 ? 0 : 2;
    }

    /// <summary>
    /// Sends user-approved requests given as hex, e.g. --request "53 09 01 28 50 50 50" --request "51 01 02".
    /// Refuses the types that rewrite or wipe stored settings in bulk (52 write profile, 53 08 reset).
    /// </summary>
    public static int Send(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        var requests = new List<byte[]>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--request")
            {
                try
                {
                    requests.Add(Convert.FromHexString(args[i + 1].Replace(" ", "")));
                }
                catch (FormatException)
                {
                    Console.Error.WriteLine($"Not hex: {args[i + 1]}");
                    return 1;
                }
            }
        }
        if (address is null || requests.Count == 0)
        {
            Console.Error.WriteLine("Usage: ble-send --address XX:XX:XX:XX:XX:XX --request \"hex bytes\" [--request ...] [--send]");
            return 1;
        }
        foreach (var r in requests)
        {
            if (r.Length < 2 || r[0] is not (0x50 or 0x51 or 0x53) || (r[0] == 0x53 && r[1] == 0x08))
            {
                Console.Error.WriteLine($"Refused: {Convert.ToHexString(r)}. Only 50 (state), 51 (read profile) and 53 edit commands other than 53 08 (reset) are allowed.");
                return 1;
            }
        }
        bool send = args.Contains("--send");

        Console.WriteLine($"Target: {BleSighting.FormatAddress(address.Value)}, service 0xFFE0: commands to 0xFFE1, responses on 0xFFE2");
        Console.WriteLine("Writes, in this order:");
        Console.WriteLine("  1. CCCD (0x2902) of 0xFFE2 <- 01 00   enable notifications");
        for (int i = 0; i < requests.Count; i++)
        {
            Console.WriteLine($"  {i + 2}. 0xFFE1 <- {BitConverter.ToString(requests[i]).Replace('-', ' ')}   (wait up to 2 s for the answer, then 100 ms)");
        }
        Console.WriteLine($"  {requests.Count + 2}. CCCD (0x2902) of 0xFFE2 <- 00 00   disable notifications, then disconnect");
        Console.WriteLine("Nothing else is written.");

        if (!send)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run: nothing was sent. Add --send to perform these steps.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Sending...");
        var notifications = WheelBle.ExchangeAsync(address.Value, requests, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Console.WriteLine($"Notifications received: {notifications.Count}");
        foreach (var (source, n) in notifications)
        {
            Console.WriteLine($"  from {source}: {BitConverter.ToString(n).Replace('-', ' ')}");
        }
        return 0;
    }

    public static int WheelGet(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        if (address is null)
        {
            Console.Error.WriteLine("Usage: wheel-get --address XX:XX:XX:XX:XX:XX");
            return 1;
        }
        Console.WriteLine("Reading state (50 01) and profile page 2 (51 <mode> 02); nothing else is written.");
        Console.WriteLine(WheelSettings.ReadAsync(address.Value).GetAwaiter().GetResult());
        return 0;
    }

    /// <summary>Changes angle, FFB strength and/or spring, keeping everything else; dry run by default.</summary>
    public static int WheelSet(string[] args)
    {
        ulong? address = ParseAddress(StringOption(args, "--address"));
        int? angle = IntOption(args, "--angle"), ffb = IntOption(args, "--ffb"), spring = IntOption(args, "--spring");
        if (address is null || (angle, ffb, spring) == (null, null, null))
        {
            Console.Error.WriteLine("Usage: wheel-set --address XX:XX:XX:XX:XX:XX [--angle 90-1080] [--ffb 0-100] [--spring 0-100] [--send]");
            return 1;
        }
        if (angle is < WheelSettings.MinAngle or > WheelSettings.MaxAngle || ffb is < 0 or > 100 || spring is < 0 or > 100)
        {
            Console.Error.WriteLine($"Out of range: angle {WheelSettings.MinAngle}-{WheelSettings.MaxAngle}, ffb 0-100, spring 0-100.");
            return 1;
        }
        bool send = args.Contains("--send");

        Console.WriteLine("Steps:");
        Console.WriteLine("  1. read state (50 01) and profile page 2 (51 <mode> 02); save them to backups/");
        if (angle is int a) Console.WriteLine($"  -> 0xFFE1 <- {Hex(WheelSettings.AngleCommand(a))}   angle {a}°");
        if (ffb is int f) Console.WriteLine($"  -> 0xFFE1 <- 53 03 <mode> {f:X2} <current reverse>   FFB strength {f}");
        if (spring is int s) Console.WriteLine($"  -> 0xFFE1 <- 53 09 01 {s:X2} <current damper> <current friction> <current inertia>   spring {s}");
        Console.WriteLine("  2. read page 2 again and compare; print the undo command");
        Console.WriteLine("Each BLE session also writes 0xFFE2's CCCD (01 00 at the start, 00 00 at the end).");

        if (!send)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run: nothing was sent. Add --send to perform these steps.");
            return 0;
        }

        var before = WheelSettings.ReadAsync(address.Value).GetAwaiter().GetResult();
        Console.WriteLine($"Before: {before}");
        string backup = Path.Combine("backups", $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory("backups");
        File.WriteAllText(backup, $"# wheel-set backup {DateTime.Now:yyyy-MM-dd HH:mm:ss} {BleSighting.FormatAddress(address.Value)}{Environment.NewLine}{before}{Environment.NewLine}");

        var commands = new List<byte[]>();
        if (angle is int a2) commands.Add(WheelSettings.AngleCommand(a2));
        if (ffb is int f2) commands.Add(before.FfbCommand(f2));
        if (spring is int s2) commands.Add(before.SpringCommand(s2));
        foreach (var c in commands)
        {
            Console.WriteLine($"Sending {Hex(c)}");
        }
        WheelBle.ExchangeAsync(address.Value, commands, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

        var after = WheelSettings.ReadAsync(address.Value).GetAwaiter().GetResult();
        Console.WriteLine($"After:  {after}");
        bool ok = (angle is null || after.AngleDegrees == angle) && (ffb is null || after.FfbStrength == ffb) && (spring is null || after.Spring == spring);
        Console.WriteLine(ok ? "Readback matches." : "WARNING: readback does not match the requested values.");
        var undo = new List<string>();
        if (angle is not null) undo.Add($"--angle {before.AngleDegrees}");
        if (ffb is not null) undo.Add($"--ffb {before.FfbStrength}");
        if (spring is not null) undo.Add($"--spring {before.Spring}");
        Console.WriteLine($"Backup: {Path.GetFullPath(backup)}");
        Console.WriteLine($"Undo:   pacewheel wheel-set --address {BleSighting.FormatAddress(address.Value)} {string.Join(" ", undo)} --send");
        return ok ? 0 : 2;
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

    private static bool IsCandidate(BleSighting s) =>
        s.ServiceUuids.Contains(WheelBle.ConfigService) ||
        (s.Name is string n && (n.Contains("WHEEL", StringComparison.OrdinalIgnoreCase) || n.Contains("V987", StringComparison.OrdinalIgnoreCase)));

    private static ulong? ParseAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string hex = text.Replace(":", "").Replace("-", "");
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }

    private static int? IntOption(string[] args, string name) =>
        StringOption(args, name) is string s && int.TryParse(s, out int v) ? v : null;

    private static string? StringOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
