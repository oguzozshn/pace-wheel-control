using System.Diagnostics;
using PaceWheel.Core.DirectInput;
using Vortice.DirectInput;

namespace PaceWheel.Cli.Commands;

/// <summary>
/// Phase 2 commands. Each one acquires the wheel exclusively for its own lifetime and releases it on
/// exit, Esc, Ctrl+C or an exception.
/// </summary>
internal static class ForceFeedbackCommands
{
    private static readonly TimeSpan Countdown = TimeSpan.FromSeconds(3);

    public static int Status(string[] args) => WithSession(session =>
    {
        PrintState(session, "Current");
        return 0;
    });

    public static int Gain(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int percent) || percent is < 0 or > 100)
        {
            Console.Error.WriteLine("Usage: gain <0-100>");
            return 1;
        }

        return WithSession(session =>
        {
            int before = session.GainPercent;
            session.GainPercent = percent;
            Console.WriteLine($"DIPROP_FFGAIN: {before}% -> {session.GainPercent}% (read back)");
            Console.WriteLine("Note: the value belongs to this process's DirectInput session. See docs/device-notes.md for whether it persists.");
            return 0;
        });
    }

    public static int AutoCenter(string[] args)
    {
        bool? on = args.Length > 0 ? args[0].ToLowerInvariant() switch { "on" => true, "off" => false, _ => null } : null;
        if (on is null)
        {
            Console.Error.WriteLine("Usage: autocenter on|off [--hold <seconds>]");
            return 1;
        }
        int hold = Math.Clamp(IntOption(args, "--hold") ?? 0, 0, 120);

        if (hold > 0)
        {
            WarnHoldWheel($"Autocenter {(on.Value ? "on" : "off")} will be held for {hold} s. Turning it on can pull the wheel to center.");
        }

        using var stop = new StopSignal();
        if (hold > 0 && !CountdownOrAbort(stop))
        {
            return 2;
        }

        return WithSession(session =>
        {
            bool before = session.AutoCenter;
            session.AutoCenter = on.Value;
            Console.WriteLine($"DIPROP_AUTOCENTER: {OnOff(before)} -> {OnOff(session.AutoCenter)} (read back)");
            if (hold > 0)
            {
                Console.WriteLine($"Holding the device for {hold} s. Turn the wheel by hand to feel it. Esc or Ctrl+C releases it early.");
                bool aborted = stop.Wait(TimeSpan.FromSeconds(hold));
                Console.WriteLine(aborted ? "Stopped early; releasing the device." : "Releasing the device.");
            }
            return 0;
        });
    }

    public static int Test(string[] args)
    {
        if (args.Length == 0 || !Enum.TryParse<TestEffectKind>(args[0], ignoreCase: true, out var kind))
        {
            Console.Error.WriteLine("Usage: test <constant|spring|damper|sine> [--strength 1-20] [--seconds 1-5] [--gain 0-100] [--autocenter on|off] [--left]");
            return 1;
        }

        int strength = IntOption(args, "--strength") ?? 10;
        if (strength is < 1 or > ForceFeedbackSession.MaxTestStrengthPercent)
        {
            Console.Error.WriteLine($"--strength must be 1..{ForceFeedbackSession.MaxTestStrengthPercent} (percent).");
            return 1;
        }
        int seconds = IntOption(args, "--seconds") ?? 2;
        if (seconds < 1 || seconds > ForceFeedbackSession.MaxTestDuration.TotalSeconds)
        {
            Console.Error.WriteLine($"--seconds must be 1..{ForceFeedbackSession.MaxTestDuration.TotalSeconds}.");
            return 1;
        }
        int? gain = IntOption(args, "--gain");
        if (gain is < 0 or > 100)
        {
            Console.Error.WriteLine("--gain must be 0..100.");
            return 1;
        }
        string? autoCenterArg = StringOption(args, "--autocenter")?.ToLowerInvariant();
        if (autoCenterArg is not null and not "on" and not "off")
        {
            Console.Error.WriteLine("--autocenter must be on or off.");
            return 1;
        }
        bool towardsRight = !args.Contains("--left");

        WarnHoldWheel($"Test effect: {kind}, strength {strength}%, {seconds} s" +
            (kind == TestEffectKind.Constant ? $", pushing {(towardsRight ? "right" : "left")}" : "") +
            (gain is int g ? $", device gain {g}%" : ", device gain unchanged") + ".");

        using var stop = new StopSignal();
        if (!CountdownOrAbort(stop))
        {
            return 2;
        }

        return WithSession(session =>
        {
            // Start from a clean device state, like games do (SDL sends the same two commands).
            session.SendCommand(ForceFeedbackCommand.Reset);
            session.SendCommand(ForceFeedbackCommand.SetActuatorsOn);
            if (gain is int gp)
            {
                session.GainPercent = gp;
            }
            if (autoCenterArg is not null)
            {
                session.AutoCenter = autoCenterArg == "on";
            }
            PrintState(session, "Before");

            // DirectInput returns a default position until the wheel's first input report after
            // acquiring; the wheel reports a few times per second even at rest.
            if (stop.Wait(TimeSpan.FromMilliseconds(400)))
            {
                Console.WriteLine("Aborted before start.");
                return 2;
            }
            int start = session.ReadSteering();
            int min = start, max = start;

            var clock = Stopwatch.StartNew();
            var effect = session.StartTestEffect(kind, strength, TimeSpan.FromSeconds(seconds), towardsRight);
            Console.WriteLine($"Playing {kind}... (Esc or Ctrl+C stops)");

            bool aborted = false;
            var lastReport = TimeSpan.Zero;
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(200))
            {
                if (stop.Wait(TimeSpan.FromMilliseconds(20)))
                {
                    aborted = true;
                    break;
                }
                int x = session.ReadSteering();
                min = Math.Min(min, x);
                max = Math.Max(max, x);
                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(500))
                {
                    lastReport = clock.Elapsed;
                    Console.WriteLine($"  {clock.Elapsed.TotalSeconds,4:0.0} s  effect status: {Safe(() => effect.Status.ToString())}, device: {Safe(() => session.State.ToString())}");
                }
            }

            session.StopAll();
            Console.WriteLine(aborted ? "EMERGENCY STOP: effects stopped." : "Done; effect stopped.");

            // Objective measure of the effect: how far the steering moved from where it started.
            int left = start - min, right = max - start;
            string peak = left > right ? $"{left} left" : $"{right} right";
            Console.WriteLine($"MEASURED: steering start {start}, range {min}..{max}, peak displacement {peak} ({Math.Max(left, right) * 100.0 / 65535:0.0}% of full range)");
            PrintState(session, "After");
            return aborted ? 2 : 0;
        });
    }

    /// <summary>Opens an exclusive session, runs the action and always disposes (stop + unacquire).</summary>
    private static int WithSession(Func<ForceFeedbackSession, int> action)
    {
        using var directInput = new WheelDirectInput();
        var instance = directInput.FindWheel();
        if (instance is null)
        {
            Console.Error.WriteLine("The wheel was not found among DirectInput game controllers.");
            return 1;
        }

        using var device = directInput.CreateDevice(instance);
        ForceFeedbackSession session;
        try
        {
            session = new ForceFeedbackSession(device);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80040205) || ex.HResult == unchecked((int)0x80070005))
        {
            Console.Error.WriteLine($"Could not acquire the wheel exclusively; another application (a game?) probably holds it. ({ex.Message.Trim()})");
            return 1;
        }

        using (session)
        {
            return action(session);
        }
    }

    private static void PrintState(ForceFeedbackSession session, string label)
    {
        Console.WriteLine($"{label}: gain {Safe(() => session.GainPercent + "%")}, autocenter {Safe(() => OnOff(session.AutoCenter))}, device state {Safe(() => session.State.ToString())}");
    }

    private static void WarnHoldWheel(string what)
    {
        Console.WriteLine();
        Console.WriteLine("!!! HOLD THE WHEEL !!!  The wheel may move on its own.");
        Console.WriteLine($"    {what}");
        Console.WriteLine("    Esc or Ctrl+C stops immediately.");
        Console.WriteLine();
    }

    private static bool CountdownOrAbort(StopSignal stop)
    {
        for (int i = (int)Countdown.TotalSeconds; i > 0; i--)
        {
            Console.WriteLine($"Starting in {i}...");
            if (stop.Wait(TimeSpan.FromSeconds(1)))
            {
                Console.WriteLine("Aborted before start.");
                return false;
            }
        }
        return true;
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"(error 0x{ex.HResult:X8})";
        }
    }

    private static int? IntOption(string[] args, string name) =>
        StringOption(args, name) is string s && int.TryParse(s, out int v) ? v : null;

    private static string? StringOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
