using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.Packets.Server.Maps;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Keeps the map's entity table in sync and hands the combat cycle its next target.
/// </summary>
/// <remarks>
/// "Nearest" is only meaningful against a populated table, so every <c>in</c> spawn is recorded with
/// its coordinates first; the lock-on then picks the closest live monster rather than whichever
/// packet happened to arrive last. <c>out</c> and <c>mapclear</c> retire entries so a dead or
/// despawned entity can never be selected.
/// </remarks>
public sealed class EntitySpawnResponder :
    IPacketResponder<InPacket>,
    IPacketResponder<OutPacket>,
    IPacketResponder<MapclearPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly BotOptions _options;
    private readonly ILogger<EntitySpawnResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntitySpawnResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public EntitySpawnResponder(ProtocolStateManager state, BotOptions options, ILogger<EntitySpawnResponder> logger)
    {
        _state = state;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<InPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        var hpPercentage = packet.NonPlayerSubPacket?.HpPercentage
                           ?? (byte)(packet.PlayerSubPacket?.HpPercentage ?? 100);

        _state.TrackEntity
        (
            new TrackedEntity(packet.EntityId, packet.EntityType, packet.PositionX, packet.PositionY, hpPercentage)
        );

        if (packet.EntityType != EntityType.Monster || hpPercentage == 0)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        _logger.LogDebug
        (
            "in -> monster #{EntityId} '{Name}' at ({X},{Y}) hp {Hp}%",
            packet.EntityId,
            packet.Name?.Name ?? "?",
            packet.PositionX,
            packet.PositionY,
            hpPercentage
        );

        // Only pick a new target while nothing is being fought, so a spawn behind us cannot
        // pull the attack cycle off a monster that is already engaged.
        if (_state.HasLiveTarget)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        var nearest = _state.FindNearestMonster();
        if (nearest is not { } monster)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        if (_state.AcquireTarget(monster.EntityId, monster.EntityType, monster.HpPercentage))
        {
            _logger.LogInformation
            (
                "Locked on to monster #{EntityId} at ({X},{Y}), {Distance} cells away - entering combat cycle.",
                monster.EntityId,
                monster.X,
                monster.Y,
                ProtocolStateManager.Distance(_state.CurrentX, _state.CurrentY, monster.X, monster.Y)
            );
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<OutPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;
        if (_state.TargetEntityId == packet.EntityId)
        {
            _logger.LogInformation("Target #{EntityId} left the map - resuming scanning.", packet.EntityId);
        }

        _state.ForgetEntity(packet.EntityId);
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<MapclearPacket> packetArgs, CancellationToken ct = default)
    {
        _logger.LogInformation("Map cleared - dropping every tracked entity.");
        _state.ForgetAllEntities();
        return Task.FromResult(Result.FromSuccess());
    }
}
