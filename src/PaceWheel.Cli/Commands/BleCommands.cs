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
