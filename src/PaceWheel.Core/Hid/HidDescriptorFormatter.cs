using System.Text;

namespace PaceWheel.Core.Hid;

public static class HidDescriptorFormatter
{
    public static IEnumerable<string> FormatHex(byte[] bytes, int bytesPerLine = 16)
    {
        for (int offset = 0; offset < bytes.Length; offset += bytesPerLine)
        {
            int count = Math.Min(bytesPerLine, bytes.Length - offset);
            yield return $"{offset:X4}: {string.Join(" ", bytes.Skip(offset).Take(count).Select(b => b.ToString("X2")))}";
        }
    }

    /// <summary>One line per item: offset, item bytes, then the indented description.</summary>
    public static IEnumerable<string> FormatDecoded(HidDescriptorInfo info)
    {
        foreach (var line in info.Lines)
        {
            string hex = string.Join(" ", line.Item.Bytes.Take(5).Select(b => b.ToString("X2")));
            if (line.Item.Bytes.Length > 5)
            {
                hex += " ..";
            }
            yield return $"{line.Item.Offset:X4}  {hex,-17} {new string(' ', line.Depth * 2)}{line.Text}";
        }
    }

    public static IEnumerable<string> FormatReports(HidDescriptorInfo info)
    {
        if (info.Reports.Count == 0)
        {
            yield return "(no reports)";
            yield break;
        }
        foreach (var r in info.Reports)
        {
            var sb = new StringBuilder();
            sb.Append($"{r.Kind,-8} ID 0x{r.ReportId:X2}  {r.Bits,4} bits = {r.PayloadBytes,3} bytes");
            if (info.UsesReportIds)
            {
                sb.Append($" (+1 ID byte = {r.PayloadBytes + 1})");
            }
            if (!string.IsNullOrEmpty(r.Collection))
            {
                sb.Append($"  [{r.Collection}]");
            }
            yield return sb.ToString();
        }
    }
}
