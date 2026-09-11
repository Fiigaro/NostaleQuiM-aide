using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NosSmooth.Core.Commands;
using NosSmooth.Core.Packets;
using NosSmooth.LocalBinding;
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
                return Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                throw new NosTaleProcessNotFoundException($"No process with pid {pid} is running.");
            }
        }

        var matches = new List<Process>();
        var unreadable = 0;
        var nearMisses = new List<string>();

        // Deliberately not NosBrowserManager.GetAllNostaleProcesses(): it maps IsProcessNostaleProcess
        // over every process with no guard, and that call reads MainModule, which throws for
        // protected and system processes. One such process anywhere on the machine takes the whole
        // enumeration down. Inspecting each process in isolation is the same detection, survivably.
        foreach (var process in Process.GetProcesses())
        {
            var matched = false;

            try
            {
                matched = NosBrowserManager.IsProcessNostaleProcess(process);

                if (!matched && LooksLikeAGameClient(process.ProcessName))
                {
                    nearMisses.Add($"{process.ProcessName} (pid {process.Id})");
                }
            }
            catch (Win32Exception)
            {
                // Protected, elevated, or a different bitness - not inspectable from here.
                unreadable++;
            }
            catch (InvalidOperationException)
            {
                // Exited between enumeration and inspection.
                unreadable++;
            }
            catch (NotSupportedException)
            {
                unreadable++;
            }

            if (matched)
            {
                matches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        if (unreadable > 0)
        {
            logger.LogDebug("{Count} process(es) could not be inspected and were skipped.", unreadable);
        }

        if (matches.Count == 0)
        {
            throw new NosTaleProcessNotFoundException(BuildNotFoundMessage(unreadable, nearMisses));
        }

        if (matches.Count > 1)
        {
            logger.LogWarning
            (
                "{Count} NosTale clients are running ({Pids}); listening to the first. Use --pid to choose.",
                matches.Count,
                string.Join(", ", matches.Select(p => p.Id))
            );
        }

        // Keep the one we bind to; release the handles on the rest.
        foreach (var extra in matches.Skip(1))
        {
            extra.Dispose();
        }

        return matches[0];
    }

    private static string BuildNotFoundMessage(int unreadable, IReadOnlyList<string> nearMisses)
    {
        var message = "No running NosTale client was found. Start the game and log in first, then run again.";

        if (nearMisses.Count > 0)
        {
            message += Environment.NewLine
                       + "These processes look like game clients but have no NostaleData directory next to them: "
                       + string.Join(", ", nearMisses.Take(8))
                       + Environment.NewLine
                       + "If yours is among them, select it explicitly with --pid <id>.";
        }

        if (unreadable > 0)
        {
            message += Environment.NewLine
                       + $"{unreadable} process(es) could not be inspected (protected, or a different bitness). "
                       + "If the client is running as administrator, run this from an elevated prompt too, "
                       + "or pass --pid <id>.";
        }

        return message;
    }

    private static bool LooksLikeAGameClient(string processName)
    {
        // Only used to make the failure message actionable - never to select a process.
        string[] hints = { "nos", "tale", "game", "client", "launcher" };
        return hints.Any(h => processName.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
