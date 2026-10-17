using PaceWheel.Cli.Commands;

namespace PaceWheel.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        try
        {
            return command switch
            {
                "list" => HidCommands.List(Console.Out),
                "descriptor" => HidCommands.Descriptor(Console.Out, rest.Contains("--hex-only")),
                "di-info" => DirectInputCommands.Info(Console.Out),
                "monitor" => DirectInputCommands.Monitor(rest),
                "notes" => NotesCommand.Run(rest),
                "ffb-status" => ForceFeedbackCommands.Status(rest),
                "gain" => ForceFeedbackCommands.Gain(rest),
                "autocenter" => ForceFeedbackCommands.AutoCenter(rest),
                "test" => ForceFeedbackCommands.Test(rest),
                "ble-scan" => BleCommands.Scan(rest),
                "ble-gatt" => BleCommands.Gatt(rest),
                "ble-info" => BleCommands.Info(rest),
                "ble-read-state" => BleCommands.ReadState(rest),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            pacewheel - diagnostics for the Rampage Pace V987 wheel (USB 11FF:2370)

            Usage: pacewheel <command> [options]

            Commands:
              list                    List every HID node of the wheel with usage and report lengths
              descriptor [--hex-only] Dump each node's HID report descriptor as hex and decoded
              di-info                 DirectInput view: capabilities, objects, FFB effect types
              monitor [options]       Live view of axes, POV and buttons (non-exclusive, read-only)
                  --seconds <n>       Stop after n seconds
                  --threshold <n>     Minimum axis change to report (default 64 of 0..65535)
                  --log <file>        Append every change and the session summary to a file
                  --changes           Print change lines instead of the live dashboard
                  --raw               Also read MI_00's raw HID input reports (shared, read-only)
              notes [--path <file>]   Regenerate the diagnostic block in docs/device-notes.md

            Force feedback (acquires the wheel exclusively while running; Esc or Ctrl+C stops):
              ffb-status              Show FFB gain, autocenter and device FFB state
              gain <0-100>            Set DIPROP_FFGAIN
              autocenter on|off [--hold <s>]
                                      Set DIPROP_AUTOCENTER, optionally keep the device held to feel it
              test <constant|spring|damper|sine> [options]
                                      Play one test effect on the wheel axis
                  --strength <1-20>   Effect strength in percent (default 10, hard cap 20)
                  --seconds <1-5>     Effect duration (default 2, hard cap 5)
                  --gain <0-100>      Set device gain for this run
                  --autocenter on|off Set autocenter for this run
                  --left              Constant force pushes left instead of right

            Bluetooth LE, read-only (the wheel's configuration channel, see docs/protocol-research.md):
              ble-scan [--seconds n] [--active] [--all]
                                      List BLE devices that look like the wheel (passive by default)
              ble-gatt [--address XX:XX:XX:XX:XX:XX]
                                      List GATT services/characteristics; no reads, writes or subscriptions
              ble-info --address XX:XX:XX:XX:XX:XX
                                      Read Device Information, GAP and 0x2901 descriptions (reads only)
              ble-read-state --address XX:XX:XX:XX:XX:XX [--send]
                                      Show (dry run) or send the read-state request 50 01 on 0xFFE2
            """);
    }
}
