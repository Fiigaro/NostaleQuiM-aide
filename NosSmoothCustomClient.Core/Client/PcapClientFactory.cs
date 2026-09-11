using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NosSmooth.Core.Commands;
using NosSmooth.Core.Packets;
using NosSmooth.Pcap;
using NosSmoothCustomClient.Configuration;

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

        try
        {
            if (clients.Count == 0)
            {
                throw new NosTaleProcessNotFoundException(BuildNotFoundMessage(verdicts));
            }

            // More than one match is not a preference to resolve, it is a signal that detection is
            // unreliable on this machine. Picking silently is how a capture ends up bound to the
            // wrong process, so ambiguity is fatal and the operator chooses.
            if (clients.Count > 1)
            {
                throw new NosTaleProcessNotFoundException(BuildAmbiguousMessage(clients));
            }

            var selected = clients[0];
            logger.LogInformation
            (
                "Detected NosTale client {Name} (pid {Pid}) at {Path}.",
                selected.Process.ProcessName,
                selected.Process.Id,
                selected.ExecutablePath
            );

            return selected.Process;
        }
        finally
        {
            // Release every handle except the one being returned.
            foreach (var verdict in verdicts)
            {
                if (clients.Count != 1 || !ReferenceEquals(verdict, clients[0]))
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
        => $"{clients.Count} processes look like NosTale clients, which means detection is not reliable here. "
           + "Refusing to guess - choose one with --pid <id>:"
           + Environment.NewLine
           + string.Join
           (
               Environment.NewLine,
               clients.Take(10).Select(c => $"  {c.Process.ProcessName} (pid {c.Process.Id})  {c.ExecutablePath}")
           )
           + Environment.NewLine
           + "Run with --list to see the full scan.";

    private static bool LooksLikeAGameClient(string processName)
    {
        // Only used to make a failure message actionable - never to select a process.
        string[] hints = { "nos", "tale", "game", "client", "launcher" };
        return hints.Any(h => processName.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
