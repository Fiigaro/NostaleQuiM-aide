using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.Packets.Server.Battle;
using NosSmooth.Packets.Server.Entities;
using NosSmoothCustomClient.Packets;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Tracks the vital signs of the locked entity and drops the lock the instant it dies.
/// </summary>
/// <remarks>
/// Four inbound shapes carry the same fact. <c>su</c> is the damage frame and is authoritative
/// during a fight - and, driving the client by keyboard, the only thing that says what the game
/// selected, since nothing on our side chose it - <c>st</c> is the entity status frame,
/// <c>die</c> is the explicit death, and
/// <see cref="QuiMTargetPacket"/> is the modified server's custom equivalent of <c>st</c>. Whichever
/// arrives first clears the target, so the scan resumes without waiting for the others.
/// Ground loot is deliberately not touched: server side auto-loot is active.
/// </remarks>
public sealed class TargetHpResponder :
    IPacketResponder<SuPacket>,
    IPacketResponder<StPacket>,
    IPacketResponder<DiePacket>,
    IPacketResponder<QuiMTargetPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly RunJournal _journal;
    private readonly ILogger<TargetHpResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TargetHpResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public TargetHpResponder(ProtocolStateManager state, RunJournal journal, ILogger<TargetHpResponder> logger)
    {
        _state = state;
        _journal = journal;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SuPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (!packet.TargetIsAlive || packet.HpPercentage == 0)
        {
            ReportKill(packet.TargetEntityId, $"su reported it dead (damage {packet.Damage})");
            return Task.FromResult(Result.FromSuccess());
        }

        // Driving the client by keyboard, nothing on our side chooses the target: the attack key
        // does, inside the game. This is where we find out what it chose - the server reporting our
        // own character hitting something is proof that something is selected. Without adopting it
        // here the lock stays empty for the whole session, and a rotation with no target never casts
        // a single skill.
        if (packet.CasterEntityId == _state.OwnCharacterId
            && packet.TargetEntityId != _state.OwnCharacterId
            && _state.AcquireTarget(packet.TargetEntityId, packet.TargetEntityType, packet.HpPercentage))
        {
            _logger.LogInformation
            (
                "su -> the game has #{EntityId} selected ({Type}); locking on.",
                packet.TargetEntityId,
                packet.TargetEntityType
            );
        }

        if (_state.UpdateTargetVitals(packet.TargetEntityId, packet.Hp, packet.HpPercentage))
        {
            _logger.LogDebug
            (
                "su -> target #{EntityId} took {Damage} damage, now {Hp}/{MaxHp} ({Percentage}%)",
                packet.TargetEntityId,
                packet.Damage,
                packet.Hp,
                packet.MaxHp,
                packet.HpPercentage
            );
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<StPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (packet.HpPercentage == 0 || packet.Hp == 0)
        {
            ReportKill(packet.EntityId, "st reported zero HP");
            return Task.FromResult(Result.FromSuccess());
        }

        // st describes whichever entity the client is showing vitals for - ours, a mate's, or the
        // selected monster. Only the last of those is a target, so the type is what makes this safe.
        if (packet.EntityType == EntityType.Monster
            && packet.EntityId != _state.OwnCharacterId
            && _state.AcquireTarget(packet.EntityId, packet.EntityType, packet.HpPercentage))
        {
            _logger.LogInformation("st -> monster #{EntityId} is selected; locking on.", packet.EntityId);
        }

        _state.UpdateTargetVitals(packet.EntityId, packet.Hp, packet.HpPercentage);
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<DiePacket> packetArgs, CancellationToken ct = default)
    {
        ReportKill(packetArgs.Packet.TargetEntityId, "die packet");
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<QuiMTargetPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (packet.Hp <= 0 || packet.HpPercentage == 0)
        {
            ReportKill(packet.EntityId, "quimtg reported zero HP");
            return Task.FromResult(Result.FromSuccess());
        }

        // Shorter server builds omit the percentage; derive it from the absolute values instead.
        var percentage = packet.HpPercentage
                         ?? (byte)(packet.MaxHp > 0 ? Math.Clamp(packet.Hp * 100 / packet.MaxHp, 0, 100) : 100);

        _state.UpdateTargetVitals(packet.EntityId, packet.Hp, percentage);
        return Task.FromResult(Result.FromSuccess());
    }

    private void ReportKill(long entityId, string reason)
    {
        // A death, as opposed to something merely walking out of view. Only this one counts towards
        // a room being cleared.
        _journal.Note($"mort #{entityId} ({reason})");

        var wasTarget = _state.ClearTarget(entityId);
        _state.ForgetEntity(entityId);

        if (wasTarget)
        {
            // Look for the next one immediately rather than after the search interval, which the
            // navigation priority would otherwise spend walking away from a spot still worth
            // farming.
            _state.ResetSearchGate();

            _logger.LogInformation
            (
                "Target #{EntityId} is down ({Reason}). Lock cleared, skipping ground loot (server auto-loot), resuming scan.",
                entityId,
                reason
            );
        }
    }
}
