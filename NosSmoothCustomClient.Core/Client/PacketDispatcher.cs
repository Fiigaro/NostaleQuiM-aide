using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.Packets;
using NosSmooth.PacketSerializer;
using Remora.Results;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Serializes a typed packet and pushes it out through the client.
/// </summary>
/// <remarks>
/// <see cref="INostaleClient.SendPacketAsync"/> only speaks strings. Going through
/// <see cref="IPacketSerializer"/> instead of interpolating packet strings by hand means the
/// outbound frames are built by the same generated converters that parse the inbound ones, so a
/// field order change cannot silently desynchronise the two directions.
/// </remarks>
public sealed class PacketDispatcher
{
    private readonly INostaleClient _client;
    private readonly IPacketSerializer _serializer;
    private readonly ILogger<PacketDispatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketDispatcher"/> class.
    /// </summary>
    /// <param name="client">The NosTale client.</param>
    /// <param name="serializer">The packet serializer.</param>
    /// <param name="logger">The logger.</param>
    public PacketDispatcher(INostaleClient client, IPacketSerializer serializer, ILogger<PacketDispatcher> logger)
    {
        _client = client;
        _serializer = serializer;
        _logger = logger;
    }

    /// <summary>
    /// Serializes and sends a client packet.
    /// </summary>
    /// <param name="packet">The packet to send.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A result.</returns>
    public async Task<Result> SendAsync(IPacket packet, CancellationToken ct = default)
    {
        var serialized = _serializer.Serialize(packet);
        if (!serialized.IsDefined(out var packetString))
        {
            _logger.LogError
            (
                "Could not serialize {Packet}: {Error}",
                packet.GetType().Name,
                serialized.ToFullString()
            );

            return Result.FromError(serialized);
        }

        var sent = await _client.SendPacketAsync(packetString, ct).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            _logger.LogError("Could not send '{Packet}': {Error}", packetString, sent.ToFullString());
        }

        return sent;
    }
}
