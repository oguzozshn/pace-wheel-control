using Vortice.DirectInput;

namespace PaceWheel.Core.DirectInput;

/// <summary>
/// Polls the wheel's joystick state with a non-exclusive, background cooperative level, so it can run
/// next to games or other tools. Unacquires on dispose.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    private readonly IDirectInputDevice8 _device;
    private bool _acquired;

    public InputMonitor(IDirectInputDevice8 device, IntPtr windowHandle)
    {
        _device = device;
        _device.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background).CheckError();
        _device.SetDataFormat<RawJoystickState>().CheckError();
        _device.Acquire().CheckError();
        _acquired = true;
    }

    public JoystickState Read()
    {
        _device.Poll();
        return _device.GetCurrentJoystickState();
    }

    public void Dispose()
    {
        if (_acquired)
        {
            _device.Unacquire();
            _acquired = false;
        }
    }
}

/// <summary>Snapshot of the values we display, in a form that is cheap to compare between polls.</summary>
public sealed record WheelInputSnapshot(IReadOnlyList<(string Name, int Value)> Axes, IReadOnlyList<int> Povs, bool[] Buttons)
{
    /// <summary>Takes the axes named in <paramref name="presentAxes"/> (X, Y, Z, Rx, Ry, Rz, S0, S1).</summary>
    public static WheelInputSnapshot From(JoystickState s, IReadOnlySet<string> presentAxes, int buttonCount, int povCount)
    {
        var all = new (string Name, int Value)[]
        {
            ("X", s.X), ("Y", s.Y), ("Z", s.Z),
            ("Rx", s.RotationX), ("Ry", s.RotationY), ("Rz", s.RotationZ),
            ("S0", s.Sliders[0]), ("S1", s.Sliders[1]),
        };
        var axes = all.Where(a => presentAxes.Contains(a.Name)).ToList();
        return new WheelInputSnapshot(axes, s.PointOfViewControllers.Take(povCount).ToArray(), s.Buttons.Take(buttonCount).ToArray());
    }

    /// <summary>Lists what changed compared to <paramref name="previous"/>, ignoring axis jitter below <paramref name="axisThreshold"/>.</summary>
    public IEnumerable<string> DiffFrom(WheelInputSnapshot previous, int axisThreshold)
    {
        for (int i = 0; i < Axes.Count; i++)
        {
            if (Math.Abs(Axes[i].Value - previous.Axes[i].Value) >= axisThreshold)
            {
                yield return $"{Axes[i].Name}={Axes[i].Value}";
            }
        }
        for (int i = 0; i < Povs.Count; i++)
        {
            if (Povs[i] != previous.Povs[i])
            {
                yield return $"POV{i}={PovText(Povs[i])}";
            }
        }
        for (int i = 0; i < Buttons.Length; i++)
        {
            if (Buttons[i] != previous.Buttons[i])
            {
                yield return $"B{i + 1} {(Buttons[i] ? "down" : "up")}";
            }
        }
    }

    public static string PovText(int pov) => pov < 0 || pov == 0xFFFF ? "center" : $"{pov / 100}°";
}

/// <summary>Collects min/max per axis and which buttons/POV directions were seen during a session.</summary>
public sealed class InputSessionStats
{
    private readonly Dictionary<string, (int Min, int Max, int First)> _axes = new();
    private readonly SortedSet<int> _buttons = new();
    private readonly SortedSet<int> _povValues = new();

    public void Add(WheelInputSnapshot s)
    {
        foreach (var (name, value) in s.Axes)
        {
            _axes[name] = _axes.TryGetValue(name, out var r)
                ? (Math.Min(r.Min, value), Math.Max(r.Max, value), r.First)
                : (value, value, value);
        }
        for (int i = 0; i < s.Buttons.Length; i++)
        {
            if (s.Buttons[i])
            {
                _buttons.Add(i + 1);
            }
        }
        foreach (int pov in s.Povs)
        {
            if (pov >= 0 && pov != 0xFFFF)
            {
                _povValues.Add(pov);
            }
        }
    }

    public IEnumerable<string> Summary()
    {
        foreach (var (name, r) in _axes)
        {
            string moved = r.Min == r.Max ? "not moved" : $"range seen {r.Min} .. {r.Max}";
            yield return $"Axis {name,-3} start {r.First,6}, {moved}";
        }
        yield return _buttons.Count == 0 ? "Buttons: none pressed" : $"Buttons pressed: {string.Join(", ", _buttons)}";
        yield return _povValues.Count == 0 ? "POV: not moved" : $"POV directions seen: {string.Join(", ", _povValues.Select(WheelInputSnapshot.PovText))}";
    }
}
