using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Pcap;

namespace NosSmoothCustomClient.Diagnostics;

/// <summary>
/// Holds the process the capture was bound to, so diagnostics can follow the same target.
/// </summary>
public sealed class CaptureTarget
{
    /// <summary>Gets or sets the captured process.</summary>
    public Process? Process { get; set; }
}

/// <summary>
/// Reports, on a slow clock, whether the capture is actually seeing anything.
/// </summary>
/// <remarks>
/// A capture that yields nothing looks identical from the outside whatever the cause, so this
/// separates the three: no TCP connection at all means the driver or the privileges are the
/// problem; a live connection with no frames means the traffic is not being decoded; frames with no
/// recognised packets means the protocol differs from what this build models. Without it, every
/// diagnosis is a guess.
/// </remarks>
public sealed class CaptureDiagnosticsService : BackgroundService
{
    private readonly CaptureTarget _target;
    private readonly ProcessTcpManager _tcpManager;
    private readonly PacketCounter _counter;
    private readonly ILogger<CaptureDiagnosticsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureDiagnosticsService"/> class.
    /// </summary>
    /// <param name="target">The captured process.</param>
    /// <param name="tcpManager">The TCP connection manager.</param>
    /// <param name="counter">The packet counter.</param>
    /// <param name="logger">The logger.</param>
    public CaptureDiagnosticsService
    (
        CaptureTarget target,
        ProcessTcpManager tcpManager,
        PacketCounter counter,
        ILogger<CaptureDiagnosticsService> logger
    )
    {
        _target = target;
        _tcpManager = tcpManager;
        _counter = counter;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_target.Process is not { } process)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var reportedConnections = false;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
              try
              {
                var connections = await ReadConnectionsAsync(process.Id).ConfigureAwait(false);

                if (connections is null)
                {
                    continue;
                }

                if (connections.Count == 0)
                {
                    _logger.LogWarning
                    (
                        "No TCP connection found for pid {Pid}. The client may not be logged in, " +
                        "or the capture driver is not seeing it - check that Npcap is installed and " +
                        "that this process is elevated.",
                        process.Id
                    );

                    reportedConnections = false;
                    continue;
                }

                if (!reportedConnections)
                {
                    _logger.LogInformation
                    (
                        "{Count} TCP connection(s) on pid {Pid}: {Connections}",
                        connections.Count,
                        process.Id,
                        string.Join(", ", connections.Select(c => $"{Ip(c.LocalAddr)}:{c.LocalPort} -> {Ip(c.RemoteAddr)}:{c.RemotePort}"))
                    );

                    reportedConnections = true;
                }

                if (_counter.Total == 0)
                {
                    _logger.LogWarning
                    (
                        "A connection is live but no packet has been decoded yet. If this persists, " +
                        "the server's encryption most likely differs from the one NosSmooth implements."
                    );

                    continue;
                }

                _logger.LogInformation
                (
                    "Capture healthy: {Server} inbound, {Client} outbound frame(s) so far.",
                    _counter.FromServer,
                    _counter.FromClient
                );
              }
              catch (Exception ex) when (ex is not OperationCanceledException)
              {
                  _logger.LogWarning(ex, "Capture diagnostics tick failed; continuing.");
              }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task<IReadOnlyList<TcpConnection>?> ReadConnectionsAsync(int processId)
    {
        try
        {
            return await _tcpManager.GetConnectionsAsync(processId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Observing must never be able to stop what is being observed: the host is configured
            // to shut down on an unhandled BackgroundService exception, so a failed diagnostic
            // would take the capture with it. Report and keep going.
            _logger.LogWarning(ex, "Capture diagnostics could not read the TCP connections; continuing.");
            return null;
        }
    }

    private static string Ip(long address)
    {
        try
        {
            // TcpConnection widens an IPv4 address into an Int64; IPAddress only accepts the four
            // significant bytes, and throws on the eight BitConverter would hand it.
            return new System.Net.IPAddress(address & 0xFFFFFFFFL).ToString();
        }
        catch (ArgumentException)
        {
            return address.ToString();
        }
    }
}
