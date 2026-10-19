namespace PaceWheel.Core.Ble;

/// <summary>One key code of the wheel's mapping protocol (docs/protocol-research.md, button mapping).</summary>
public sealed record WheelKey(byte Code, string Name);

/// <summary>
/// The wheel's button map as stored in profile pages 0-2: for every physical key, the key code it sends.
/// Codes and page order come from the mobile app's KeyCode and DeviceProfile classes.
/// </summary>
public sealed class WheelKeyMap
{
    /// <summary>Key order of the map bytes in profile pages 0, 1 and 2.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<byte>> PageSequence =
    [
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x11, 0x10],
        [0x0E, 0x0F, 0x2F, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21],
        [0x22, 0x23],
    ];

    /// <summary>Physical keys shown for remapping (axes, the reserved slot and LX are left out).</summary>
    public static readonly IReadOnlyList<WheelKey> RemappableKeys =
    [
        new(0x0A, "A"), new(0x09, "B"), new(0x0B, "X"), new(0x08, "Y"),
        new(0x0C, "LB (L1)"), new(0x0D, "RB (R1)"), new(0x0E, "LT (L2)"), new(0x0F, "RT (R2)"),
        new(0x00, "Back (View)"), new(0x03, "Start (Menu)"), new(0x01, "SL (L3)"), new(0x02, "SR (R3)"),
        new(0x10, "Mode (Home)"), new(0x11, "Share"),
        new(0x04, "D-pad yukarı"), new(0x06, "D-pad aşağı"), new(0x07, "D-pad sol"), new(0x05, "D-pad sağ"),
        new(0x17, "GU (vites yukarı)"), new(0x18, "GD (vites aşağı)"), new(0x19, "El freni"), new(0x1A, "Vites değiştirme"),
        new(0x1B, "Vites 1"), new(0x1C, "Vites 2"), new(0x1D, "Vites 3"), new(0x1E, "Vites 4"),
        new(0x1F, "Vites 5"), new(0x20, "Vites 6"), new(0x21, "Geri vites"),
    ];

    /// <summary>Codes a key can be mapped to. The app's RESET code (0x2E) is deliberately not offered.</summary>
    public static readonly IReadOnlyList<WheelKey> Targets = BuildTargets();

    /// <summary>
    /// DirectInput button (1-based) each gamepad code arrives as (docs/device-notes.md, input mapping).
    /// Share and the gear-up/down/handbrake codes were never captured, so they are missing.
    /// </summary>
    private static readonly Dictionary<byte, int> DirectInputButtons = new()
    {
        [0x0A] = 1, [0x09] = 2, [0x0B] = 3, [0x08] = 4, [0x0C] = 5, [0x0D] = 6, [0x0E] = 7, [0x0F] = 8,
        [0x00] = 9, [0x03] = 10, [0x01] = 11, [0x02] = 12, [0x10] = 13,
        [0x1B] = 17, [0x1C] = 18, [0x1D] = 19, [0x1E] = 20, [0x1F] = 21, [0x20] = 22, [0x21] = 23,
    };

    /// <summary>D-pad codes and the POV angle (hundredths of a degree) they report.</summary>
    private static readonly Dictionary<byte, int> PovAngles = new() { [0x04] = 0, [0x05] = 9000, [0x06] = 18000, [0x07] = 27000 };

    /// <summary>The DirectInput button a code shows up as, or null if unknown or not a gamepad button.</summary>
    public static int? DirectInputButtonOf(byte code) => DirectInputButtons.TryGetValue(code, out int b) ? b : null;

    /// <summary>Whether a code can be seen through DirectInput (a known gamepad button or a D-pad direction).</summary>
    public static bool IsDetectable(byte code) => DirectInputButtons.ContainsKey(code) || PovAngles.ContainsKey(code);

    /// <summary>
    /// Whether a code is active in the given DirectInput state. Null when the code can't be seen through
    /// DirectInput at all (keyboard, mouse, unassigned or never-captured codes).
    /// </summary>
    public static bool? IsActive(byte code, bool[] buttons, IReadOnlyList<int> povs)
    {
        if (DirectInputButtonOf(code) is int button)
        {
            return button <= buttons.Length && buttons[button - 1];
        }
        if (PovAngles.TryGetValue(code, out int angle))
        {
            if (povs.Count == 0)
            {
                return null;
            }
            int pov = povs[0];
            if (pov < 0 || pov >= 36000)
            {
                return false;
            }
            // Diagonals light both neighbouring directions.
            int diff = Math.Abs(pov - angle);
            return Math.Min(diff, 36000 - diff) <= 4500;
        }
        return null;
    }

    private readonly Dictionary<byte, byte> _map;

    private WheelKeyMap(int mode, Dictionary<byte, byte> map)
    {
        Mode = mode;
        _map = map;
    }

    public int Mode { get; }

    /// <summary>The code a physical key currently sends, or null if it wasn't in the pages read.</summary>
    public byte? TargetOf(byte key) => _map.TryGetValue(key, out byte target) ? target : null;

    public static string NameOf(byte code) =>
        Targets.FirstOrDefault(t => t.Code == code)?.Name ?? $"0x{code:X2}";

    /// <summary>53 02 &lt;mode&gt; &lt;key&gt; &lt;target&gt;</summary>
    public byte[] RemapCommand(byte key, byte target)
    {
        if (!RemappableKeys.Any(k => k.Code == key) || !Targets.Any(t => t.Code == target))
        {
            throw new ArgumentOutOfRangeException(nameof(key), "Key or target is not in the allowed lists.");
        }
        return [0x53, 0x02, (byte)Mode, key, target];
    }

    /// <summary>Reads profile pages 0-2 of the given mode (51 &lt;mode&gt; 00..02) in one BLE session.</summary>
    public static async Task<WheelKeyMap> ReadAsync(ulong address, int mode)
    {
        var requests = Enumerable.Range(0, 3).Select(page => new byte[] { 0x51, (byte)mode, (byte)page }).ToList();
        var answers = await WheelBle.ExchangeAsync(address, requests, TimeSpan.FromSeconds(3));

        var map = new Dictionary<byte, byte>();
        for (int page = 0; page < PageSequence.Count; page++)
        {
            var data = answers.Select(a => a.Data).FirstOrDefault(d => d.Length >= 3 && d[0] == 0xD1 && d[2] == page)
                ?? throw new InvalidOperationException($"Profile page {page} did not arrive.");
            var keys = PageSequence[page];
            for (int i = 0; i < keys.Count && 3 + i < data.Length; i++)
            {
                map[keys[i]] = data[3 + i];
            }
        }
        return new WheelKeyMap(mode, map);
    }

    private static List<WheelKey> BuildTargets()
    {
        var list = new List<WheelKey>
        {
            new(0x0A, "A"), new(0x09, "B"), new(0x0B, "X"), new(0x08, "Y"),
            new(0x0C, "LB (L1)"), new(0x0D, "RB (R1)"), new(0x0E, "LT (L2)"), new(0x0F, "RT (R2)"),
            new(0x00, "Back (View)"), new(0x03, "Start (Menu)"), new(0x01, "SL (L3)"), new(0x02, "SR (R3)"),
            new(0x10, "Mode (Home)"), new(0x11, "Share"),
            new(0x04, "D-pad yukarı"), new(0x06, "D-pad aşağı"), new(0x07, "D-pad sol"), new(0x05, "D-pad sağ"),
            new(0x17, "GU (vites yukarı)"), new(0x18, "GD (vites aşağı)"), new(0x19, "El freni"), new(0x1A, "Vites değiştirme"),
            new(0x1B, "Vites 1"), new(0x1C, "Vites 2"), new(0x1D, "Vites 3"), new(0x1E, "Vites 4"),
            new(0x1F, "Vites 5"), new(0x20, "Vites 6"), new(0x21, "Geri vites"),
            new(0x30, "Fare sol"), new(0x31, "Fare sağ"), new(0x32, "Fare orta"),
            new(0xFE, "Atanmamış"),
        };
        for (int i = 0; i < 26; i++) list.Add(new((byte)(0x40 + i), $"Klavye {(char)('A' + i)}"));
        for (int i = 1; i <= 9; i++) list.Add(new((byte)(0x5A + i - 1), $"Klavye {i}"));
        list.Add(new(0x63, "Klavye 0"));
        for (int i = 1; i <= 12; i++) list.Add(new((byte)(0x64 + i - 1), $"Klavye F{i}"));
        list.AddRange(
        [
            new(0x70, "Klavye Esc"), new(0x74, "Klavye Tab"), new(0x75, "Klavye Caps Lock"),
            new(0x76, "Klavye Sol Ctrl"), new(0x77, "Klavye Sol Shift"), new(0x78, "Klavye Sol Alt"),
            new(0x7A, "Klavye Sağ Ctrl"), new(0x7B, "Klavye Sağ Shift"), new(0x7C, "Klavye Sağ Alt"),
            new(0x7E, "Klavye Enter"), new(0x7F, "Klavye Backspace"), new(0x88, "Klavye Boşluk"),
            new(0x89, "Klavye ↑"), new(0x8A, "Klavye ↓"), new(0x8B, "Klavye ←"), new(0x8C, "Klavye →"),
        ]);
        return list;
    }
}
