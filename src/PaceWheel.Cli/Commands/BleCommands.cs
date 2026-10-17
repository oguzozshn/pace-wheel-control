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
