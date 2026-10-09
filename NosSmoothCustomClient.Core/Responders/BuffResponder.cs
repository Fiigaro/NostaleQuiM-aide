using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Buffs;
using NosSmooth.Packets.Server.Entities;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Keeps the buff tracker aligned with what the server actually granted.
/// </summary>
/// <remarks>
/// The tracker's local clock is only an estimate, and a live session showed how far off a
/// hand-written one can be: buffs assumed to last two minutes were reported by the server at
/// twenty and fifty. Wherever a buff's card id is configured, the server's own remaining time wins.
/// </remarks>
public sealed class BuffResponder :
    IPacketResponder<BfPacket>,
    IPacketResponder<RevivePacket>
{
    private readonly BuffTracker _tracker;
    private readonly ProtocolStateManager _state;
    private readonly ILogger<BuffResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuffResponder"/> class.
    /// </summary>
    /// <param name="tracker">The buff tracker.</param>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public BuffResponder(BuffTracker tracker, ProtocolStateManager state, ILogger<BuffResponder> logger)
    {
        _tracker = tracker;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<BfPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (packet.EntityId != _state.OwnCharacterId || packet.SubPacket is not { } sub)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        if (_tracker.ApplyServerTiming(sub.CardId, sub.Time))
        {
            _logger.LogDebug("Buff card {CardId} refreshed for {Seconds}s.", sub.CardId, sub.Time);
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<RevivePacket> packetArgs, CancellationToken ct = default)
    {
        if (packetArgs.Packet.EntityId != _state.OwnCharacterId)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        // Dying strips buffs; assuming they survived would leave the loop fighting without them.
        _tracker.Reset();
        _logger.LogInformation("Revived - buffs cleared, they will be reapplied.");

        return Task.FromResult(Result.FromSuccess());
    }
}
