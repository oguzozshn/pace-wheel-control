using Vortice.DirectInput;

namespace PaceWheel.Core.DirectInput;

public enum TestEffectKind
{
    Constant,
    Spring,
    Damper,
    Sine,
}

/// <summary>
/// Exclusive, background access to the wheel's force feedback. Only one process can hold this at a
/// time, so a game can't get FFB while a session is open. Dispose stops every effect, unloads them
/// and unacquires the device; always use it in a using block.
/// </summary>
public sealed class ForceFeedbackSession : IDisposable
{
    /// <summary>Hard cap for test effect strength (percent of full scale).</summary>
    public const int MaxTestStrengthPercent = 20;

    /// <summary>Hard cap for test effect duration.</summary>
    public static readonly TimeSpan MaxTestDuration = TimeSpan.FromSeconds(5);

    private const int DierrAcquired = unchecked((int)0x800700AA);
    private const int NoTrigger = -1; // DIEB_NOTRIGGER
    private const int XAxisOffset = 0; // DIJOFS_X in c_dfDIJoystick2

    private readonly IDirectInputDevice8 _device;
    private readonly MessageWindow _window;
    private readonly List<IDirectInputEffect> _effects = new();
    private bool _acquired;
    private bool _disposed;

    public ForceFeedbackSession(IDirectInputDevice8 device)
    {
        _device = device;
        _window = new MessageWindow("PaceWheel FFB");
        try
        {
            _device.SetCooperativeLevel(_window.Handle, CooperativeLevel.Exclusive | CooperativeLevel.Background).CheckError();
            _device.SetDataFormat<RawJoystickState>().CheckError();
            Acquire();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>DIPROP_FFGAIN as a percentage (DirectInput uses 0..10000).</summary>
    public int GainPercent
    {
        get => (_device.Properties.ForceFeedbackGain + 50) / 100;
        set => SetProperty(() => _device.Properties.ForceFeedbackGain = Math.Clamp(value, 0, 100) * 100);
    }

    /// <summary>DIPROP_AUTOCENTER.</summary>
    public bool AutoCenter
    {
        get => _device.Properties.AutoCenter;
        set => SetProperty(() => _device.Properties.AutoCenter = value);
    }

    public ForceFeedbackState State => _device.GetForceFeedbackState();

    public void SendCommand(ForceFeedbackCommand command) => _device.SendForceFeedbackCommand(command);

    /// <summary>Current steering position (DirectInput X, 0..65535; lower = left).</summary>
    public int ReadSteering()
    {
        _device.Poll();
        return _device.GetCurrentJoystickState().X;
    }

    /// <summary>
    /// Creates and starts one test effect on the X axis. Strength is capped at
    /// <see cref="MaxTestStrengthPercent"/> and duration at <see cref="MaxTestDuration"/>, so the
    /// effect always ends on its own, even if this process dies.
    /// </summary>
    public IDirectInputEffect StartTestEffect(TestEffectKind kind, int strengthPercent, TimeSpan duration, bool towardsRight = true)
    {
        int strength = Math.Clamp(strengthPercent, 1, MaxTestStrengthPercent) * 100;
        var length = duration > MaxTestDuration ? MaxTestDuration : duration;

        var parameters = new EffectParameters
        {
            Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
            Duration = (int)(length.TotalMilliseconds * 1000),
            SamplePeriod = 0,
            Gain = 10000,
            TriggerButton = NoTrigger,
            TriggerRepeatInterval = 0,
            StartDelay = 0,
        };
        parameters.SetAxes([XAxisOffset], [1]);

        Guid guid;
        switch (kind)
        {
            case TestEffectKind.Constant:
                guid = EffectGuid.ConstantForce;
                // On this wheel a positive magnitude on X turns the wheel left (felt test, 2026-10-17).
                parameters.Parameters = new ConstantForce { Magnitude = towardsRight ? -strength : strength };
                break;
            case TestEffectKind.Sine:
                guid = EffectGuid.Sine;
                parameters.Parameters = new PeriodicForce { Magnitude = strength, Offset = 0, Phase = 0, Period = 200_000 };
                break;
            case TestEffectKind.Spring:
            case TestEffectKind.Damper:
                guid = kind == TestEffectKind.Spring ? EffectGuid.Spring : EffectGuid.Damper;
                // Saturation caps the force the condition can produce at the requested strength.
                parameters.Parameters = new ConditionSet
                {
                    Conditions =
                    [
                        new Condition
                        {
                            Offset = 0,
                            PositiveCoefficient = strength,
                            NegativeCoefficient = strength,
                            PositiveSaturation = strength,
                            NegativeSaturation = strength,
                            DeadBand = 0,
                        },
                    ],
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var effect = _device.CreateEffect(guid, parameters);
        _effects.Add(effect);
        effect.Start(1).CheckError();
        return effect;
    }

    /// <summary>Stops every effect this session created and tells the device to stop all effects.</summary>
    public void StopAll()
    {
        foreach (var effect in _effects)
        {
            TryRun(() => effect.Stop());
        }
        if (_acquired)
        {
            TryRun(() => _device.SendForceFeedbackCommand(ForceFeedbackCommand.StopAll));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        StopAll();
        foreach (var effect in _effects)
        {
            TryRun(() => effect.Unload());
            TryRun(effect.Dispose);
        }
        _effects.Clear();
        if (_acquired)
        {
            TryRun(() => _device.Unacquire());
            _acquired = false;
        }
        _window.Dispose();
    }

    private void Acquire()
    {
        _device.Acquire().CheckError();
        _acquired = true;
    }

    /// <summary>Some properties can't be changed while acquired; retry once after unacquiring.</summary>
    private void SetProperty(Action set)
    {
        try
        {
            set();
        }
        catch (Exception ex) when (ex.HResult == DierrAcquired)
        {
            _device.Unacquire();
            _acquired = false;
            set();
            Acquire();
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Best effort during shutdown; the effect's finite duration is the safety net.
        }
    }
}
