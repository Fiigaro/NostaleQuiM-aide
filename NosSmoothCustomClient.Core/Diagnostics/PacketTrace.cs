using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using Remora.Results;

namespace NosSmoothCustomClient.Diagnostics;

/// <summary>
/// Counts what the transport has actually delivered.
/// </summary>
/// <remarks>
/// Separating the count from the log is what makes a silent capture diagnosable: zero here with a
/// live TCP connection means the frames are not being decoded, while zero connections means the
/// capture never started.
/// </remarks>
public sealed class PacketCounter
{
    private long _server;
    private long _client;

    /// <summary>Gets how many server frames were seen.</summary>
    public long FromServer => Interlocked.Read(ref _server);

    /// <summary>Gets how many client frames were seen.</summary>
    public long FromClient => Interlocked.Read(ref _client);

    /// <summary>Gets the total.</summary>
    public long Total => FromServer + FromClient;

    /// <summary>Records a frame.</summary>
    /// <param name="source">Where it came from.</param>
    public void Count(PacketSource source)
    {
        if (source == PacketSource.Server)
        {
            Interlocked.Increment(ref _server);
        }
        else
        {
            Interlocked.Increment(ref _client);
        }
    }
}

/// <summary>
/// Records every packet, parsed or not, in both directions.
/// </summary>
/// <remarks>
/// A raw responder sees the frame before deserialization, so unknown and malformed packets show up
/// too. That is exactly what calibration needs: the real cast ids arrive in <c>ski</c>, the real
/// coordinates in <c>at</c> and <c>mv</c>, the consumable slots in <c>inv</c> - and any of those
/// could carry a shape this build does not model yet.
///
/// The count is taken first and the ring is written whole, before any filter runs. Both are
/// diagnostics: a capture narrowed to one header would otherwise report that it is seeing nothing,
/// which is the exact symptom of a capture that really is broken.
/// </remarks>
public sealed class PacketTraceResponder : IRawPacketResponder
{
    private readonly PacketCounter _counter;
    private readonly PacketLog _log;
    private readonly PacketFilter _filter;
    private readonly ILogger<PacketTraceResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketTraceResponder"/> class.
    /// </summary>
    /// <param name="counter">The counter.</param>
    /// <param name="log">The ring the window reads back.</param>
    /// <param name="filter">What is worth showing.</param>
    /// <param name="logger">The logger.</param>
    public PacketTraceResponder(PacketCounter counter, PacketLog log, PacketFilter filter, ILogger<PacketTraceResponder> logger)
    {
        _counter = counter;
        _log = log;
        _filter = filter;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs packetArgs, CancellationToken ct = default)
    {
        _counter.Count(packetArgs.Source);

        if (!_filter.Paused)
        {
            _log.Add(packetArgs.Source, packetArgs.PacketString);
        }

        if (_filter.TraceToLog && _filter.Allows(packetArgs.Source, packetArgs.PacketString))
        {
            _logger.LogInformation
            (
                "[{Direction}] {Packet}",
                packetArgs.Source == PacketSource.Server ? "IN " : "OUT",
                packetArgs.PacketString
            );
        }

        return Task.FromResult(Result.FromSuccess());
    }
}
