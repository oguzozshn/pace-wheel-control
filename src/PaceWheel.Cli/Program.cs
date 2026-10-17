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
            """);
    }
}
