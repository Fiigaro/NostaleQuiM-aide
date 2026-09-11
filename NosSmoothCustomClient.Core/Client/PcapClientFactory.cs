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

        // Matches on the presence of a NostaleData directory next to the executable, which is how
        // NosSmooth itself identifies a client.
        var candidates = NosBrowserManager.GetAllNostaleProcesses().ToList();

        if (candidates.Count == 0)
        {
            throw new NosTaleProcessNotFoundException
            (
                "No running NosTale client was found. Start the game and log in first, then run again. " +
                "If the client is running, pass its pid explicitly with --pid <id>."
            );
        }

        if (candidates.Count > 1)
        {
            logger.LogWarning
            (
                "{Count} NosTale clients are running ({Pids}); listening to the first. Use --pid to choose.",
                candidates.Count,
                string.Join(", ", candidates.Select(p => p.Id))
            );
        }

        return candidates[0];
    }
}
