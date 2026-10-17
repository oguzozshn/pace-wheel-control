namespace PaceWheel.Core.Hid;

/// <summary>
/// Minimal HID report descriptor parser (HID 1.11, section 6.2.2). Decodes short and long items,
/// tracks the global/local state and sums report sizes per report ID.
/// </summary>
public static class HidDescriptorParser
{
    private struct GlobalState
    {
        public ushort UsagePage;
        public uint ReportSize;
        public uint ReportCount;
        public byte ReportId;
    }

    public static HidDescriptorInfo Parse(ReadOnlySpan<byte> descriptor)
    {
        var warnings = new List<string>();
        var items = Tokenize(descriptor, warnings);

        var lines = new List<HidDecodedLine>();
        var topLevel = new List<HidTopLevelCollection>();
        var reportBits = new Dictionary<(HidReportKind Kind, byte Id), int>();
        var reportCollection = new Dictionary<(HidReportKind Kind, byte Id), string>();

        var global = new GlobalState();
        var globalStack = new Stack<GlobalState>();
        var collectionStack = new Stack<string>();
        var localUsages = new List<uint>();
        int depth = 0;

        foreach (var item in items)
        {
            string text;
            int lineDepth = depth;

            switch (item.Type)
            {
                case HidItemType.Main:
                    switch (item.Tag)
                    {
                        case 0x8:
                        case 0x9:
                        case 0xB:
                        {
                            var kind = item.Tag switch { 0x8 => HidReportKind.Input, 0x9 => HidReportKind.Output, _ => HidReportKind.Feature };
                            text = $"{kind} ({MainFlags(item.Data, kind)})";
                            var key = (kind, global.ReportId);
                            reportBits[key] = reportBits.GetValueOrDefault(key) + (int)(global.ReportSize * global.ReportCount);
                            if (!reportCollection.ContainsKey(key))
                            {
                                reportCollection[key] = string.Join(" > ", collectionStack.Reverse().Where(n => n.Length > 0));
                            }
                            break;
                        }
                        case 0xA:
                        {
                            string usageName = localUsages.Count > 0 ? UsageName(localUsages[0], global.UsagePage) : "";
                            text = $"Collection ({CollectionType(item.Data)})";
                            if (depth == 0)
                            {
                                uint u = localUsages.Count > 0 ? localUsages[0] : 0;
                                ushort page = u > 0xFFFF ? (ushort)(u >> 16) : global.UsagePage;
                                topLevel.Add(new HidTopLevelCollection(page, (ushort)u));
                            }
                            collectionStack.Push(usageName);
                            depth++;
                            break;
                        }
                        case 0xC:
                            if (depth == 0)
                            {
                                warnings.Add($"End Collection without matching Collection at offset 0x{item.Offset:X4}.");
                            }
                            else
                            {
                                depth--;
                                collectionStack.Pop();
                            }
                            lineDepth = depth;
                            text = "End Collection";
                            break;
                        default:
                            text = $"Main (reserved tag 0x{item.Tag:X}) 0x{item.Data:X}";
                            break;
                    }
                    localUsages.Clear();
                    break;

                case HidItemType.Global:
                    text = DecodeGlobal(item, ref global, globalStack, warnings);
                    break;

                case HidItemType.Local:
                    text = DecodeLocal(item, global.UsagePage, localUsages);
                    break;

                case HidItemType.Long:
                    text = $"Long Item (tag 0x{item.Tag:X2}, {item.DataSize} data bytes)";
                    break;

                default:
                    text = $"Reserved item type, tag 0x{item.Tag:X}, data 0x{item.Data:X}";
                    break;
            }

            lines.Add(new HidDecodedLine(item, lineDepth, text));
        }

        if (depth != 0)
        {
            warnings.Add($"{depth} collection(s) not closed at end of descriptor.");
        }

        var reports = reportBits
            .Select(kv => new HidReportInfo(kv.Key.Kind, kv.Key.Id, kv.Value, reportCollection.GetValueOrDefault(kv.Key, "")))
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.ReportId)
            .ToList();

        return new HidDescriptorInfo
        {
            Items = items,
            Lines = lines,
            TopLevelCollections = topLevel,
            Reports = reports,
            Warnings = warnings,
        };
    }

    private static List<HidItem> Tokenize(ReadOnlySpan<byte> d, List<string> warnings)
    {
        var items = new List<HidItem>();
        int i = 0;
        while (i < d.Length)
        {
            byte prefix = d[i];
            if (prefix == 0xFE)
            {
                if (i + 2 >= d.Length)
                {
                    warnings.Add($"Truncated long item at offset 0x{i:X4}.");
                    break;
                }
                int size = d[i + 1];
                int length = 3 + size;
                if (i + length > d.Length)
                {
                    warnings.Add($"Truncated long item at offset 0x{i:X4}.");
                    break;
                }
                items.Add(new HidItem(i, d.Slice(i, length).ToArray(), HidItemType.Long, d[i + 2], 0, size));
                i += length;
                continue;
            }

            int dataSize = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            if (i + 1 + dataSize > d.Length)
            {
                warnings.Add($"Truncated item at offset 0x{i:X4}.");
                break;
            }
            uint data = 0;
            for (int k = 0; k < dataSize; k++)
            {
                data |= (uint)d[i + 1 + k] << (8 * k);
            }
            items.Add(new HidItem(i, d.Slice(i, 1 + dataSize).ToArray(), (HidItemType)((prefix >> 2) & 0x03), prefix >> 4, data, dataSize));
            i += 1 + dataSize;
        }
        return items;
    }

    private static string DecodeGlobal(HidItem item, ref GlobalState g, Stack<GlobalState> stack, List<string> warnings)
    {
        switch (item.Tag)
        {
            case 0x0:
                g.UsagePage = (ushort)item.Data;
                return $"Usage Page ({HidUsageNames.Page(g.UsagePage)})";
            case 0x1:
                return $"Logical Minimum ({SignedText(item)})";
            case 0x2:
                return $"Logical Maximum ({SignedText(item)})";
            case 0x3:
                return $"Physical Minimum ({SignedText(item)})";
            case 0x4:
                return $"Physical Maximum ({SignedText(item)})";
            case 0x5:
            {
                int exponent = (int)(item.Data & 0x0F);
                if (exponent > 7)
                {
                    exponent -= 16;
                }
                return $"Unit Exponent ({exponent})";
            }
            case 0x6:
                return $"Unit (0x{item.Data:X})";
            case 0x7:
                g.ReportSize = item.Data;
                return $"Report Size ({item.Data})";
            case 0x8:
                g.ReportId = (byte)item.Data;
                return $"Report ID (0x{item.Data:X2})";
            case 0x9:
                g.ReportCount = item.Data;
                return $"Report Count ({item.Data})";
            case 0xA:
                stack.Push(g);
                return "Push";
            case 0xB:
                if (stack.Count == 0)
                {
                    warnings.Add($"Pop without Push at offset 0x{item.Offset:X4}.");
                }
                else
                {
                    g = stack.Pop();
                }
                return "Pop";
            default:
                return $"Global (reserved tag 0x{item.Tag:X}) 0x{item.Data:X}";
        }
    }

    /// <summary>
    /// Shows the signed value, plus the unsigned reading for negative 1- and 2-byte values.
    /// Windows treats e.g. "26 FF FF" as 65535 when the logical minimum is not negative.
    /// </summary>
    private static string SignedText(HidItem item) =>
        item.SignedData < 0 && item.DataSize < 4 ? $"{item.SignedData}, unsigned {item.Data}" : item.SignedData.ToString();

    private static string DecodeLocal(HidItem item, ushort usagePage, List<uint> usages)
    {
        uint usage = item.DataSize == 4 ? item.Data : item.Data & 0xFFFF;
        switch (item.Tag)
        {
            case 0x0:
                usages.Add(usage);
                return $"Usage ({UsageName(usage, usagePage)})";
            case 0x1:
                usages.Add(usage);
                return $"Usage Minimum ({UsageName(usage, usagePage)})";
            case 0x2:
                return $"Usage Maximum ({UsageName(usage, usagePage)})";
            case 0x3:
                return $"Designator Index ({item.Data})";
            case 0x4:
                return $"Designator Minimum ({item.Data})";
            case 0x5:
                return $"Designator Maximum ({item.Data})";
            case 0x7:
                return $"String Index ({item.Data})";
            case 0x8:
                return $"String Minimum ({item.Data})";
            case 0x9:
                return $"String Maximum ({item.Data})";
            case 0xA:
                return $"Delimiter ({(item.Data == 1 ? "Open" : "Close")})";
            default:
                return $"Local (reserved tag 0x{item.Tag:X}) 0x{item.Data:X}";
        }
    }

    /// <summary>Usage name; 32-bit usages carry their own page in the high word.</summary>
    private static string UsageName(uint usage, ushort currentPage)
    {
        if (usage > 0xFFFF)
        {
            ushort page = (ushort)(usage >> 16);
            return $"{HidUsageNames.Page(page)}: {HidUsageNames.Usage(page, (ushort)usage)}";
        }
        return HidUsageNames.Usage(currentPage, (ushort)usage);
    }

    private static string CollectionType(uint type) => type switch
    {
        0x00 => "Physical",
        0x01 => "Application",
        0x02 => "Logical",
        0x03 => "Report",
        0x04 => "Named Array",
        0x05 => "Usage Switch",
        0x06 => "Usage Modifier",
        >= 0x80 and <= 0xFF => $"Vendor 0x{type:X2}",
        _ => $"Reserved 0x{type:X2}",
    };

    private static string MainFlags(uint data, HidReportKind kind)
    {
        var parts = new List<string>
        {
            (data & 0x001) != 0 ? "Cnst" : "Data",
            (data & 0x002) != 0 ? "Var" : "Ary",
            (data & 0x004) != 0 ? "Rel" : "Abs",
        };
        if ((data & 0x008) != 0) parts.Add("Wrap");
        if ((data & 0x010) != 0) parts.Add("NLin");
        if ((data & 0x020) != 0) parts.Add("NPrf");
        if ((data & 0x040) != 0) parts.Add("Null");
        if (kind != HidReportKind.Input && (data & 0x080) != 0) parts.Add("Vol");
        if ((data & 0x100) != 0) parts.Add("Buf");
        return string.Join(", ", parts);
    }
}
