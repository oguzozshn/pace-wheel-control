using Vortice.DirectInput;

namespace PaceWheel.Core.DirectInput;

/// <summary>Owns the IDirectInput8 instance and finds the wheel among attached game controllers.</summary>
public sealed class WheelDirectInput : IDisposable
{
    private readonly IDirectInput8 _directInput = DInput.DirectInput8Create();

    public IReadOnlyList<DeviceInstance> GameControllers() =>
        _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly).ToList();

    public DeviceInstance? FindWheel() =>
        GameControllers().FirstOrDefault(d => WheelIds.MatchesProductGuid(d.ProductGuid));

    public IDirectInputDevice8 CreateDevice(DeviceInstance instance) => _directInput.CreateDevice(instance.InstanceGuid);

    public void Dispose() => _directInput.Dispose();
}

public sealed record DirectInputObjectInfo(
    string Name,
    string Type,
    int Offset,
    int InstanceNumber,
    ushort UsagePage,
    ushort Usage,
    byte ReportId,
    InputRange? Range,
    int? DeadZone,
    int? Saturation,
    bool IsForceFeedbackActuator);

public sealed record DirectInputDeviceInfo(
    DeviceInstance Instance,
    Capabilities Capabilities,
    IReadOnlyList<(string Name, string Value)> Properties,
    IReadOnlyList<DirectInputObjectInfo> Objects,
    IReadOnlyList<EffectInfo> Effects,
    string? ForceFeedbackState)
{
    // ForceFeedbackState is null when the device must be acquired exclusively to read it.

    /// <summary>Reads capabilities, properties, objects and effects. Does not acquire the device or create effects.</summary>
    public static DirectInputDeviceInfo Read(DeviceInstance instance, IDirectInputDevice8 device)
    {
        var properties = new List<(string, string)>();
        void Prop(string name, Func<object?> read)
        {
            try
            {
                properties.Add((name, read()?.ToString() ?? "(null)"));
            }
            catch (Exception ex)
            {
                properties.Add((name, $"(error: {ex.Message})"));
            }
        }

        var p = device.Properties;
        Prop("ProductName", () => p.ProductName);
        Prop("InstanceName", () => p.InstanceName);
        Prop("VendorId", () => $"0x{p.VendorId:X4}");
        Prop("ProductId", () => $"0x{p.ProductId:X4}");
        Prop("JoystickId", () => p.JoystickId);
        Prop("InterfacePath", () => p.InterfacePath);
        Prop("ClassGuid", () => p.ClassGuid);
        Prop("AxisMode", () => p.AxisMode);
        Prop("BufferSize", () => p.BufferSize);
        Prop("ForceFeedbackGain (DIPROP_FFGAIN)", () => p.ForceFeedbackGain);
        Prop("AutoCenter (DIPROP_AUTOCENTER)", () => p.AutoCenter);

        var objects = new List<DirectInputObjectInfo>();
        foreach (var o in device.GetObjects(DeviceObjectTypeFlags.All))
        {
            bool isAxis = (o.ObjectId.Flags & DeviceObjectTypeFlags.Axis) != 0;
            InputRange? range = null;
            int? deadZone = null, saturation = null;
            if (isAxis)
            {
                try
                {
                    var op = device.GetObjectPropertiesById(o.ObjectId);
                    range = op.Range;
                    deadZone = op.DeadZone;
                    saturation = op.Saturation;
                }
                catch
                {
                    // Not every object exposes these properties.
                }
            }

            objects.Add(new DirectInputObjectInfo(
                o.Name,
                ObjectTypeName(o),
                o.Offset,
                o.ObjectId.InstanceNumber,
                o.UsagePage,
                o.Usage,
                (byte)o.ReportId,
                range,
                deadZone,
                saturation,
                (o.ObjectId.Flags & DeviceObjectTypeFlags.ForceFeedbackActuator) != 0));
        }

        string? ffState;
        try
        {
            ffState = device.GetForceFeedbackState().ToString();
        }
        catch (Exception ex) when (ex.HResult == DierrNotExclusiveAcquired)
        {
            ffState = null;
        }

        return new DirectInputDeviceInfo(instance, device.Capabilities, properties, objects, device.GetEffects().ToList(), ffState);
    }

    private const int DierrNotExclusiveAcquired = unchecked((int)0x80040205);

    private static string ObjectTypeName(DeviceObjectInstance o)
    {
        var f = o.ObjectId.Flags;
        if ((f & DeviceObjectTypeFlags.Collection) != 0) return "Collection";
        if ((f & DeviceObjectTypeFlags.AbsoluteAxis) != 0) return "AbsAxis";
        if ((f & DeviceObjectTypeFlags.RelativeAxis) != 0) return "RelAxis";
        if ((f & DeviceObjectTypeFlags.PushButton) != 0) return "PushButton";
        if ((f & DeviceObjectTypeFlags.ToggleButton) != 0) return "ToggleButton";
        if ((f & DeviceObjectTypeFlags.PointOfViewController) != 0) return "POV";
        if ((f & DeviceObjectTypeFlags.Output) != 0) return "Output";
        if ((f & DeviceObjectTypeFlags.NoData) != 0) return "NoData";
        return $"0x{(long)f:X}";
    }
}
