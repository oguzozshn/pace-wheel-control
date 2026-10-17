using PaceWheel.Core;
using PaceWheel.Core.Hid;

namespace PaceWheel.Cli.Commands;

internal static class HidCommands
{
    public static int List(TextWriter output)
    {
        var probes = ProbeAll(output);
        if (probes is null)
        {
            return 1;
        }

        foreach (var p in probes)
        {
            output.WriteLine($"[{p.Interface.Label}]");
            output.WriteLine($"  Path:            {p.Interface.DevicePath}");
            output.WriteLine($"  Product:         {p.ProductName ?? "?"}");
            output.WriteLine($"  Manufacturer:    {p.Manufacturer ?? "?"}");
            output.WriteLine($"  Serial number:   {(string.IsNullOrEmpty(p.SerialNumber) ? "(none)" : p.SerialNumber)}");
            output.WriteLine($"  Release (bcd):   {(p.ReleaseNumberBcd is int r ? $"0x{r:X4}" : "?")}");
            if (p.Descriptor is { } d)
            {
                foreach (var tlc in d.TopLevelCollections)
                {
                    output.WriteLine($"  Usage:           {tlc.PageName} / {tlc.Name} (page 0x{tlc.UsagePage:X4}, usage 0x{tlc.Usage:X4})");
                }
            }
            output.WriteLine($"  Report lengths:  input {Len(p.MaxInputReportLength)}, output {Len(p.MaxOutputReportLength)}, feature {Len(p.MaxFeatureReportLength)}  (bytes, incl. report ID byte)");
            output.WriteLine($"  Open (shared):   {p.OpenResult}");
            foreach (var e in p.Errors)
            {
                output.WriteLine($"  ! {e}");
            }
            output.WriteLine();
        }
        return 0;
    }

    public static int Descriptor(TextWriter output, bool hexOnly)
    {
        var probes = ProbeAll(output);
        if (probes is null)
        {
            return 1;
        }

        output.WriteLine("Note: Windows does not expose the original descriptor bytes. HidSharp rebuilds them");
        output.WriteLine("from the preparsed data, so the bytes below are equivalent but not byte-exact.");
        output.WriteLine();

        foreach (var p in probes)
        {
            output.WriteLine($"===== {p.Interface.Label}  {p.ProductName}");
            if (p.RawDescriptor is null || p.Descriptor is null)
            {
                output.WriteLine("  (descriptor not available)");
                foreach (var e in p.Errors)
                {
                    output.WriteLine($"  ! {e}");
                }
                output.WriteLine();
                continue;
            }

            output.WriteLine($"--- Hex ({p.RawDescriptor.Length} bytes)");
            foreach (var line in HidDescriptorFormatter.FormatHex(p.RawDescriptor))
            {
                output.WriteLine(line);
            }
            if (!hexOnly)
            {
                output.WriteLine("--- Decoded");
                foreach (var line in HidDescriptorFormatter.FormatDecoded(p.Descriptor))
                {
                    output.WriteLine(line);
                }
                output.WriteLine("--- Reports");
                foreach (var line in HidDescriptorFormatter.FormatReports(p.Descriptor))
                {
                    output.WriteLine(line);
                }
                foreach (var w in p.Descriptor.Warnings)
                {
                    output.WriteLine($"! {w}");
                }
            }
            output.WriteLine();
        }
        return 0;
    }

    private static List<HidInterfaceProbe>? ProbeAll(TextWriter output)
    {
        var interfaces = WheelHidEnumerator.Enumerate();
        if (interfaces.Count == 0)
        {
            output.WriteLine($"No HID device with VID 0x{WheelIds.VendorId:X4} / PID 0x{WheelIds.ProductId:X4} found. Is the wheel plugged in?");
            return null;
        }
        output.WriteLine($"Found {interfaces.Count} HID node(s) for {WheelIds.VendorId:X4}:{WheelIds.ProductId:X4}.");
        output.WriteLine();
        return interfaces.Select(i => i.Probe()).ToList();
    }

    private static string Len(int? length) => length?.ToString() ?? "?";
}
