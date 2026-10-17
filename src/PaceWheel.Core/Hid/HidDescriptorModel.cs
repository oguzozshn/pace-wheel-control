namespace PaceWheel.Core.Hid;

public enum HidItemType
{
    Main = 0,
    Global = 1,
    Local = 2,
    Reserved = 3,
    Long = 4,
}

public enum HidReportKind
{
    Input,
    Output,
    Feature,
}

/// <summary>One item of a HID report descriptor, as it appears in the byte stream.</summary>
public sealed record HidItem(int Offset, byte[] Bytes, HidItemType Type, int Tag, uint Data, int DataSize)
{
    public int SignedData => DataSize switch
    {
        1 => (sbyte)Data,
        2 => (short)Data,
        4 => (int)Data,
        _ => 0,
    };
}

/// <summary>A decoded item with its collection nesting depth and a readable description.</summary>
public sealed record HidDecodedLine(HidItem Item, int Depth, string Text);

/// <summary>A top-level (application) collection, which Windows exposes as one HID device.</summary>
public sealed record HidTopLevelCollection(ushort UsagePage, ushort Usage)
{
    public string Name => HidUsageNames.Usage(UsagePage, Usage);
    public string PageName => HidUsageNames.Page(UsagePage);
}

/// <summary>Total size of one report (by kind and report ID), summed over all its main items.</summary>
public sealed record HidReportInfo(HidReportKind Kind, byte ReportId, int Bits, string Collection)
{
    /// <summary>Payload size in bytes, without the report ID byte.</summary>
    public int PayloadBytes => (Bits + 7) / 8;
}

public sealed class HidDescriptorInfo
{
    public required IReadOnlyList<HidItem> Items { get; init; }
    public required IReadOnlyList<HidDecodedLine> Lines { get; init; }
    public required IReadOnlyList<HidTopLevelCollection> TopLevelCollections { get; init; }
    public required IReadOnlyList<HidReportInfo> Reports { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>True when the descriptor declares report IDs (every report then starts with an ID byte).</summary>
    public bool UsesReportIds => Reports.Any(r => r.ReportId != 0);
}
