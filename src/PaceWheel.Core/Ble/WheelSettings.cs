namespace PaceWheel.Core.Ble;

/// <summary>
/// The wheel's stored settings from profile page 2 (see docs/protocol-research.md), plus the
/// edit commands that change them. Only the commands confirmed on the real wheel are built here.
/// </summary>
public sealed record WheelSettings(int Mode, int FirmwareVersion, int AngleDegrees, int FfbStrength, int Spring, int Damper, int Friction, int Inertia, bool FfbReverse)
{
    public const int MinAngle = 90;
    public const int MaxAngle = 1080;

    /// <summary>Reads the state (mode, firmware) and profile page 2 over two short BLE sessions.</summary>
    public static async Task<WheelSettings> ReadAsync(ulong address)
    {
        var state = (await WheelBle.ExchangeAsync(address, [0x50, 0x01], TimeSpan.FromSeconds(3)))
            .Select(n => n.Data).FirstOrDefault(d => d.Length >= 8 && d[0] == 0xD0)
            ?? throw new InvalidOperationException("No state answer (D0) from the wheel.");
        int mode = state[2];

        var page = (await WheelBle.ExchangeAsync(address, [0x51, (byte)mode, 0x02], TimeSpan.FromSeconds(3)))
            .Select(n => n.Data).FirstOrDefault(d => d.Length >= 13 && d[0] == 0xD1 && d[2] == 0x02)
            ?? throw new InvalidOperationException("No profile page 2 answer (D1) from the wheel.");

        // Page 2 after the 3-byte header: 2 key map bytes, angle (u16 LE), FFB, spring, damper, friction, inertia, reverse.
        return new WheelSettings(
            mode,
            state[3] | (state[4] << 8),
            page[5] | (page[6] << 8),
            page[7], page[8], page[9], page[10], page[11],
            page[12] == 1);
    }

    /// <summary>53 04 01 01 &lt;angle lo&gt; &lt;angle hi&gt;</summary>
    public static byte[] AngleCommand(int degrees)
    {
        if (degrees is < MinAngle or > MaxAngle)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), $"Angle must be {MinAngle}..{MaxAngle}.");
        }
        return [0x53, 0x04, 0x01, 0x01, (byte)(degrees & 0xFF), (byte)(degrees >> 8)];
    }

    /// <summary>53 03 &lt;mode&gt; &lt;ffb&gt; &lt;reverse&gt;</summary>
    public byte[] FfbCommand(int strength) =>
        [0x53, 0x03, (byte)Mode, (byte)CheckPercent(strength, nameof(strength)), FfbReverse ? (byte)1 : (byte)0];

    /// <summary>53 09 01 &lt;spring&gt; &lt;damper&gt; &lt;friction&gt; &lt;inertia&gt;, keeping the other three values.</summary>
    public byte[] SpringCommand(int spring) =>
        [0x53, 0x09, 0x01, (byte)CheckPercent(spring, nameof(spring)), (byte)Damper, (byte)Friction, (byte)Inertia];

    /// <summary>The edit commands that change only the requested values, keeping the rest of this profile.</summary>
    public IReadOnlyList<byte[]> BuildChangeCommands(int? angle, int? ffb, int? spring)
    {
        var commands = new List<byte[]>();
        if (angle is int a && a != AngleDegrees) commands.Add(AngleCommand(a));
        if (ffb is int f && f != FfbStrength) commands.Add(FfbCommand(f));
        if (spring is int s && s != Spring) commands.Add(SpringCommand(s));
        return commands;
    }

    /// <summary>Sends approved change commands, then reads the settings back.</summary>
    public static async Task<WheelSettings> ApplyAsync(ulong address, IReadOnlyList<byte[]> commands)
    {
        if (commands.Count > 0)
        {
            await WheelBle.ExchangeAsync(address, commands, TimeSpan.FromSeconds(2));
        }
        return await ReadAsync(address);
    }

    public override string ToString() =>
        $"mode {Mode}, firmware 0x{FirmwareVersion:X4}, angle {AngleDegrees}°, FFB {FfbStrength}, spring {Spring}, damper {Damper}, friction {Friction}, inertia {Inertia}, FFB reverse {(FfbReverse ? "on" : "off")}";

    private static int CheckPercent(int value, string name) =>
        value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(name, "Value must be 0..100.");
}
