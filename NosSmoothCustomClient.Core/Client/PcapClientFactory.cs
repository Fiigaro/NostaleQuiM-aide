using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NosSmooth.Core.Commands;
using NosSmooth.Core.Packets;
using NosSmooth.Pcap;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Raised when no usable NosTale process could be found for the capture transport.
/// </summary>
public sealed class NosTaleProcessNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NosTaleProcessNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public NosTaleProcessNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Builds the packet-capture client.
/// </summary>
/// <remarks>
/// NosSmooth.Pcap ships no DI extension, so the client is assembled by hand. The one piece of real
/// logic here is picking the process: the capture is bound to a single client's TCP connections, so
/// an ambiguous or missing choice has to fail loudly rather than silently listen to nothing.
/// </remarks>
public static class PcapClientFactory
{
    /// <summary>
    /// Creates the capture client from the container.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>The client.</returns>
    /// <exception cref="NosTaleProcessNotFoundException">Thrown when no process could be selected.</exception>
    public static PcapNostaleClient Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<PcapOptions>();
        var logger = services.GetRequiredService<ILogger<PcapNostaleClient>>();
        var process = SelectProcess(options, services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PcapClientFactory)));

        // Let the diagnostics follow the same target.
        services.GetRequiredService<CaptureTarget>().Process = process;

        logger.LogInformation
        (
            "Capture bound to process {ProcessName} (pid {ProcessId}), decoding with {Encoding}.",
            process.ProcessName,
            process.Id,
            options.ResolveEncoding().WebName
        );

        return new PcapNostaleClient
        (
            process,
            options.InitialEncryptionKey,
            options.ResolveEncoding(),
            services.GetRequiredService<PcapNostaleManager>(),
            services.GetRequiredService<ProcessTcpManager>(),
            services.GetRequiredService<IPacketHandler>(),
            services.GetRequiredService<CommandProcessor>(),
            services.GetRequiredService<IOptions<PcapNostaleOptions>>(),
            logger
        );
    }

    private static Process SelectProcess(PcapOptions options, ILogger logger)
    {
        if (options.ProcessId is { } pid)
        {
            try
            {
                var chosen = Process.GetProcessById(pid);
                logger.LogInformation("Using process {Name} (pid {Pid}) as requested.", chosen.ProcessName, pid);
                return chosen;
            }
            catch (ArgumentException)
            {
                throw new NosTaleProcessNotFoundException($"No process with pid {pid} is running.");
            }
        }

        var verdicts = NosTaleProcessScanner.Scan();
        var clients = verdicts.Where(v => v.IsClient).ToList();
        ProcessVerdict? kept = null;

        try
        {
            if (clients.Count == 0)
            {
                throw new NosTaleProcessNotFoundException(BuildNotFoundMessage(verdicts));
            }

            // Several matches usually means leftover instances beside the one being played. A
            // window is the reliable discriminator: exactly one windowed client is an unambiguous
            // answer, anything else is a genuine choice the operator has to make, because picking
            // silently is how a capture ends up bound to the wrong process.
            var selected = clients[0];

            if (clients.Count > 1)
            {
                var windowed = clients.Where(c => c.HasWindow).ToList();

                if (windowed.Count != 1)
                {
                    throw new NosTaleProcessNotFoundException(BuildAmbiguousMessage(clients));
                }

                selected = windowed[0];
                logger.LogInformation
                (
                    "{Count} clients are running; {Windowed} is the only one with a window, using it.",
                    clients.Count,
                    selected.Process.Id
                );
            }

            logger.LogInformation
            (
                "Capturing {Name} (pid {Pid}){Window} at {Path}.",
                selected.Process.ProcessName,
                selected.Process.Id,
                selected.HasWindow ? $" \"{selected.WindowTitle}\"" : " with no window",
                selected.ExecutablePath
            );

            kept = selected;
            return selected.Process;
        }
        finally
        {
            // Release every handle except the one being returned.
            foreach (var verdict in verdicts)
            {
                if (!ReferenceEquals(verdict, kept))
                {
                    verdict.Process.Dispose();
                }
            }
        }
    }

    private static string BuildNotFoundMessage(IReadOnlyList<ProcessVerdict> verdicts)
    {
        var unreadable = verdicts.Count(v => v.Reason.StartsWith("not inspectable", StringComparison.Ordinal));
        var candidates = verdicts
            .Where(v => v.ExecutablePath is not null && LooksLikeAGameClient(v.Process.ProcessName))
            .Select(v => $"  {v.Process.ProcessName} (pid {v.Process.Id})  {v.ExecutablePath}")
            .Take(10)
            .ToList();

        var message = "No running NosTale client was found. Start the game, log in, then run again."
                      + Environment.NewLine
                      + "Detection looks for a NostaleData directory next to the executable.";

        if (candidates.Count > 0)
        {
            message += Environment.NewLine
                       + "These look like game clients but have no NostaleData next to them:"
                       + Environment.NewLine
                       + string.Join(Environment.NewLine, candidates)
                       + Environment.NewLine
                       + "If yours is listed, select it with --pid <id>.";
        }

        if (unreadable > 0)
        {
            message += Environment.NewLine
                       + $"{unreadable} process(es) could not be inspected. If the client runs elevated, "
                       + "run this from an elevated prompt too, or pass --pid <id>.";
        }

        message += Environment.NewLine + "Run with --list to see every process and why it was rejected.";
        return message;
    }

    private static string BuildAmbiguousMessage(IReadOnlyList<ProcessVerdict> clients)
        => $"{clients.Count} NosTale clients are running and none stands out, so the choice is yours. "
           + "Pick the one you are playing with --pid <id>:"
           + Environment.NewLine
           + string.Join(Environment.NewLine, clients.Take(10).Select(c => "  " + c.Describe()))
           + Environment.NewLine
           + "The window title is usually the giveaway; a client with no window is a leftover instance.";

    private static bool LooksLikeAGameClient(string processName)
    {
        // Only used to make a failure message actionable - never to select a process.
        string[] hints = { "nos", "tale", "game", "client", "launcher" };
        return hints.Any(h => processName.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
