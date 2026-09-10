using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Entities;
using NosSmooth.Packets.Server.Maps;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Keeps the character's own coordinates current so the navigation priority has something to
/// steer from, and keeps tracked entities' coordinates current so "nearest" stays honest.
/// </summary>
/// <remarks>
/// <c>at</c> is the authoritative position frame - the server sends it for the controlled character
/// on map entry and on teleport, which is also where the character id comes from. <c>mv</c> is the
/// per-step move frame for every entity, ours included.
/// </remarks>
public sealed class PositionTrackingResponder :
    IPacketResponder<AtPacket>,
    IPacketResponder<MovePacket>,
    IPacketResponder<TpPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly ILogger<PositionTrackingResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PositionTrackingResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public PositionTrackingResponder(ProtocolStateManager state, ILogger<PositionTrackingResponder> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<AtPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // The first at packet of a session is always the controlled character's own arrival.
        if (_state.OwnCharacterId < 0)
        {
            _state.SetOwnCharacterId(packet.CharacterId);
            _logger.LogInformation("Controlled character id resolved as #{CharacterId}.", packet.CharacterId);
        }

        if (_state.OwnCharacterId == packet.CharacterId)
        {
            _state.UpdatePosition(packet.X, packet.Y);
            _logger.LogDebug("at -> own position ({X},{Y}) on map {MapId}", packet.X, packet.Y, packet.MapId);
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<MovePacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (_state.OwnCharacterId == packet.EntityId)
        {
            _state.UpdatePosition(packet.MapX, packet.MapY);
        }
        else
        {
            _state.MoveEntity(packet.EntityId, packet.MapX, packet.MapY);
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<TpPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (_state.OwnCharacterId == packet.EntityId)
        {
            _state.UpdatePosition(packet.PositionX, packet.PositionY);
        }
        else
        {
            _state.MoveEntity(packet.EntityId, packet.PositionX, packet.PositionY);
        }

        return Task.FromResult(Result.FromSuccess());
    }
}
