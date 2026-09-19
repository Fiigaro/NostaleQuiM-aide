using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.Packets.Server.Maps;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
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
    private readonly RunJournal _journal;
    private readonly ILogger<EntitySpawnResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntitySpawnResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public EntitySpawnResponder
    (
        ProtocolStateManager state,
        BotOptions options,
        RunJournal journal,
        ILogger<EntitySpawnResponder> logger
    )
    {
        _state = state;
        _options = options;
        _journal = journal;
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
            // Everything that is not a monster: the portal that opens when a room is cleared shows
            // up this way, and a recorded run has no other way to point at it.
            _journal.Note($"apparition {packet.EntityType} #{packet.EntityId} VNum {packet.VNum} en ({packet.PositionX},{packet.PositionY})");
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

        // Said plainly because it is constantly mistaken for a death: out is what the server sends
        // when an entity leaves view. A count built from it follows the character, not the fight.
        _journal.Note($"hors de vue #{packet.EntityId}");

        _state.ForgetEntity(packet.EntityId);
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<MapclearPacket> packetArgs, CancellationToken ct = default)
    {
        // The one that empties the table in a single step. A room's monsters never go from dozens
        // to none by being killed, so a count reaching zero means this packet far more often than
        // it means the fight is over - which is exactly the pair a recorded run has to tell apart.
        _journal.Note("mapclear : toutes les entités effacées d'un coup");

        _logger.LogInformation("Map cleared - dropping every tracked entity.");
        _state.ForgetAllEntities();
        return Task.FromResult(Result.FromSuccess());
    }
}
