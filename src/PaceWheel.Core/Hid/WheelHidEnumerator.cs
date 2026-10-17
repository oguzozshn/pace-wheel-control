using System.Globalization;
using System.Text.RegularExpressions;
using HidSharp;

namespace PaceWheel.Core.Hid;

/// <summary>One HID device node of the wheel. On Windows each top-level collection is its own node.</summary>
public sealed partial class WheelHidInterface
{
    public WheelHidInterface(HidDevice device)
    {
        Device = device;
        var mi = InterfaceRegex().Match(device.DevicePath);
        var col = CollectionRegex().Match(device.DevicePath);
        InterfaceNumber = mi.Success ? int.Parse(mi.Groups[1].Value, NumberStyles.HexNumber) : null;
        CollectionNumber = col.Success ? int.Parse(col.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public HidDevice Device { get; }
    public string DevicePath => Device.DevicePath;
    public int? InterfaceNumber { get; }
    public int? CollectionNumber { get; }

    public string Label =>
        (InterfaceNumber is int mi ? $"MI_{mi:X2}" : "MI_??") +
        (CollectionNumber is int col ? $" COL{col:D2}" : "");

    /// <summary>Reads everything we can learn about this node without sending anything to the device.</summary>
    public HidInterfaceProbe Probe()
    {
        var errors = new List<string>();

        T? Try<T>(string what, Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                errors.Add($"{what}: {ex.GetType().Name}: {ex.Message}");
                return default;
            }
        }

        byte[]? raw = Try("report descriptor", Device.GetRawReportDescriptor);
        string openResult;
        if (Device.TryOpen(new OpenConfiguration(), out var stream, out var openError))
        {
            stream.Dispose();
            openResult = "OK (shared read/write)";
        }
        else
        {
            openResult = $"failed: {openError?.Message ?? "unknown error"}";
        }

        return new HidInterfaceProbe
        {
            Interface = this,
            ProductName = Try("product name", Device.GetProductName),
            Manufacturer = Try("manufacturer", Device.GetManufacturer),
            SerialNumber = Try("serial number", Device.GetSerialNumber),
            ReleaseNumberBcd = Try("release number", () => (int?)Device.ReleaseNumberBcd),
            MaxInputReportLength = Try("input report length", () => (int?)Device.GetMaxInputReportLength()),
            MaxOutputReportLength = Try("output report length", () => (int?)Device.GetMaxOutputReportLength()),
            MaxFeatureReportLength = Try("feature report length", () => (int?)Device.GetMaxFeatureReportLength()),
            RawDescriptor = raw,
            Descriptor = raw is null ? null : HidDescriptorParser.Parse(raw),
            OpenResult = openResult,
            Errors = errors,
        };
    }

    [GeneratedRegex(@"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"col([0-9]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionRegex();
}

public sealed class HidInterfaceProbe
{
    public required WheelHidInterface Interface { get; init; }
    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public int? ReleaseNumberBcd { get; init; }
    public int? MaxInputReportLength { get; init; }
    public int? MaxOutputReportLength { get; init; }
    public int? MaxFeatureReportLength { get; init; }

    /// <summary>
    /// On Windows this is rebuilt by HidSharp from the preparsed data, not the bytes the firmware sent.
    /// It is equivalent for parsing, but item order and encoding can differ from the original.
    /// </summary>
    public byte[]? RawDescriptor { get; init; }

    public HidDescriptorInfo? Descriptor { get; init; }
    public required string OpenResult { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
}

public static class WheelHidEnumerator
{
    public static IReadOnlyList<WheelHidInterface> Enumerate(int vendorId = WheelIds.VendorId, int productId = WheelIds.ProductId) =>
        DeviceList.Local.GetHidDevices(vendorId, productId)
            .Select(d => new WheelHidInterface(d))
            .OrderBy(i => i.InterfaceNumber ?? int.MaxValue)
            .ThenBy(i => i.CollectionNumber ?? 0)
            .ToList();
}
