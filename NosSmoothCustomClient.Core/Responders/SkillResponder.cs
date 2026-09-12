using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Battle;
using NosSmooth.Packets.Server.Skills;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Keeps the rotation's cooldown view in sync with the server.
/// </summary>
/// <remarks>
/// <c>sr</c> is the server telling the client a skill is off cooldown - the authoritative signal,
/// and the reason the rotation does not have to trust its own clock. <c>ski</c> is the character's
/// skill bar, which is also what turns the VNum in <c>su</c> back into a cast id. <c>su</c> is the
/// server saying a skill actually went off, which is the only proof a key press became a cast.
/// </remarks>
public sealed class SkillResponder :
    IPacketResponder<SrPacket>,
    IPacketResponder<SkiPacket>,
    IPacketResponder<SuPacket>
{
    private readonly SkillRotation _rotation;
    private readonly SkillBarMap _bar;
    private readonly ProtocolStateManager _state;
    private readonly ILogger<SkillResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillResponder"/> class.
    /// </summary>
    /// <param name="rotation">The rotation.</param>
    /// <param name="bar">The skill bar.</param>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public SkillResponder(SkillRotation rotation, SkillBarMap bar, ProtocolStateManager state, ILogger<SkillResponder> logger)
    {
        _rotation = rotation;
        _bar = bar;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SuPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // Someone else's attack says nothing about our cooldowns.
        if (packet.CasterEntityId != _state.OwnCharacterId)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        // su omits the VNum for a plain hit; only a named skill can confirm a rotation entry.
        if (packet.SkillVNum is not { } vnum)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        if (_bar.TryGetCastId(vnum, out var castId))
        {
            if (_rotation.ConfirmCast((short)castId))
            {
                _logger.LogDebug
                (
                    "su -> we cast VNum {VNum} (slot {CastId}); server cooldown field reads {Cooldown}.",
                    vnum,
                    castId,
                    packet.SkillCooldown
                );
            }

            return Task.FromResult(Result.FromSuccess());
        }

        // The bar is unknown, which is the normal case for a bot started while already in game -
        // ski is only sent at login and on a Specialist change. The cast can still be identified:
        // a cooldown of its own is what separates a real skill from the plain swing the attack key
        // produces, and the loop holds every other key while one press is outstanding, so that
        // press is the only thing this can be.
        if (packet.SkillCooldown > 0 && _rotation.TryConfirmPending(out var pending))
        {
            if (_bar.Learn(vnum, pending))
            {
                _logger.LogInformation
                (
                    "Learned that quick bar slot {CastId} casts VNum {VNum} (server cooldown field {Cooldown}). " +
                    "No ski was seen this session, so the bar is being read from your own casts.",
                    pending,
                    vnum,
                    packet.SkillCooldown
                );
            }

            return Task.FromResult(Result.FromSuccess());
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SrPacket> packetArgs, CancellationToken ct = default)
    {
        var castId = packetArgs.Packet.SkillId;

        if (!_rotation.MarkServerReady(castId))
        {
            // Not one of ours: either a skill outside the rotation, or sr numbers its skills
            // differently than the cast ids. Either way the local timer still covers us.
            _logger.LogDebug("sr -> skill {CastId} is not part of the rotation, ignoring.", castId);
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SkiPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // The bar is what lets su's VNum be read back as a cast id, so it is maintained here rather
        // than in the calibration responder, which only runs while capturing.
        if (packet.SkillSubPackets is { Count: > 0 } skills)
        {
            _bar.Replace(skills.Select(s => s.SkillVNum).ToArray());
        }

        _logger.LogInformation
        (
            "ski -> skill bar: primary VNum {Primary}, secondary VNum {Secondary}, {Count} skill(s): {VNums}",
            packet.PrimarySkillVNum,
            packet.SecondarySkillVNum,
            packet.SkillSubPackets?.Count ?? 0,
            string.Join(", ", packet.SkillSubPackets?.Select(s => s.SkillVNum) ?? Enumerable.Empty<int>())
        );

        return Task.FromResult(Result.FromSuccess());
    }
}
