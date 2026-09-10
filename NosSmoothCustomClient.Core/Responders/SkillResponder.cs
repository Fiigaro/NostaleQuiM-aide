using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
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
/// skill bar, logged once so the configured cast ids can be checked against what the character
/// actually has.
/// </remarks>
public sealed class SkillResponder :
    IPacketResponder<SrPacket>,
    IPacketResponder<SkiPacket>
{
    private readonly SkillRotation _rotation;
    private readonly ILogger<SkillResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillResponder"/> class.
    /// </summary>
    /// <param name="rotation">The rotation.</param>
    /// <param name="logger">The logger.</param>
    public SkillResponder(SkillRotation rotation, ILogger<SkillResponder> logger)
    {
        _rotation = rotation;
        _logger = logger;
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
