using System.Globalization;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// The parsed command line, shared by both front-ends so they accept exactly the same switches.
/// </summary>
/// <param name="Mode">The transport to bind.</param>
/// <param name="Paused">Whether the loop should start without acting.</param>
/// <param name="Verbose">Whether per-tick detail is logged.</param>
/// <param name="ProcessId">An explicitly chosen NosTale process, when given.</param>
/// <param name="ListProcesses">Whether to print the process scan and exit.</param>
/// <param name="Trace">Whether to log every raw packet.</param>
/// <param name="TestInput">Whether to run the background-input test and exit.</param>
/// <param name="Play">Whether the bot may actually press keys in the game.</param>
/// <param name="TestKey">A single key to test, instead of the full sequence.</param>
/// <param name="RecordWaypoints">Whether to record a patrol route instead of running the bot.</param>
public sealed record CommandLine(RunMode Mode, bool Paused, bool Verbose, int? ProcessId, bool ListProcesses, bool Trace, bool TestInput, bool Play, string? TestKey, bool RecordWaypoints)
{
    /// <summary>
    /// Parses the arguments.
    /// </summary>
    /// <param name="args">The raw arguments.</param>
    /// <returns>The parsed command line.</returns>
    public static CommandLine Parse(string[] args)
    {
        var mode = RunMode.Simulate;

        if (Has(args, "--attach"))
        {
            mode = RunMode.Attach;
        }
        else if (Has(args, "--pcap") || Has(args, "--listen") || Has(args, "--record-waypoints"))
        {
            // Recording needs the character's position, which only the capture provides.
            mode = RunMode.Pcap;
        }

        return new CommandLine
        (
            mode,
            Has(args, "--paused") || Has(args, "--observe"),
            Has(args, "--verbose"),
            ReadProcessId(args),
            Has(args, "--list"),
            Has(args, "--trace"),
            Has(args, "--test-input"),
            Has(args, "--play"),
            ReadValue(args, "--key"),
            Has(args, "--record-waypoints")
        );
    }

    /// <summary>
    /// Checks whether this command line can run on the current OS.
    /// </summary>
    /// <param name="reason">The reason it cannot, when it cannot.</param>
    /// <returns>True when the mode is usable here.</returns>
    public bool IsSupportedHere(out string reason)
    {
        if (Mode == RunMode.Attach && !OperatingSystem.IsWindows())
        {
            reason = "--attach binds to a running NosTale process through Reloaded.Hooks and is Windows x86 only.";
            return false;
        }

        if (Mode == RunMode.Pcap && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            reason = "--pcap needs libpcap (Npcap on Windows).";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Gets the usage text.
    /// </summary>
    /// <returns>The usage text.</returns>
    public static string Usage
        => """
           Transports (choose one, default --simulate):
             --simulate        Synthesised frames, no game needed. Runs anywhere.
             --pcap            Read the real client's traffic off the wire. Needs Npcap and
                               administrator rights. Starts READ-ONLY.
             --attach          Bind to the game process in memory. Windows x86 only.

           Options:
             --pid <id>        The NosTale process to listen to (--pcap). Default: auto-detect.
             --paused          Start without acting; the pipeline still runs and logs.
             --play            Let the bot actually press keys in the game. Without it every
                               decision is only logged. Starts paused; press P to begin.
             --verbose         Log every tick.

           Diagnostics:
             --list            Print every running process with the reason it was or was not
                               taken for a NosTale client, then exit.
             --trace           Log every raw packet in both directions. Always on with --pcap.
             --record-waypoints
                               Record a patrol route: stand on a spot, point at it on the
                               minimap, press F9. F10 saves. Implies --pcap.
             --test-input      Send a few keys to the game window with the game in the
                               background, to find out whether that works at all. Exits after.
             --key <k>         With --test-input, send only this key (e.g. space, 1, r),
                               three times, instead of the whole sequence.
           """;

    private static bool Has(string[] args, string name)
        => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string? ReadValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int? ReadProcessId(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return null;
    }
}
