using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Battle;
using NosSmooth.Packets.Server.Buffs;
using NosSmooth.Packets.Server.Character;
using NosSmooth.Packets.Server.Inventory;
using NosSmooth.Packets.Server.Skills;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Turns a live session into the values <c>appsettings.json</c> needs.
/// </summary>
/// <remarks>
/// Everything here is derived from the inbound stream alone. That is deliberate: on this server the
/// client-to-server direction is encrypted with a scheme NosSmooth does not implement, so our own
/// packets cannot be read back. They are not needed. The server echoes every fact we want - the
/// skill bar arrives in <c>ski</c>, a cast comes back as <c>su</c> naming the skill's VNum, and
/// using an item produces an <c>ivn</c> naming the slot it came from.
///
/// Buffs take a different route: a self-cast buff need not produce <c>su</c> at all, and shows up
/// as <c>bf</c> instead, carrying the buff card and its duration. <c>sr</c> is reported too because
/// it names a skill coming off cooldown, which is a second, independent way to pin a cast id.
/// </remarks>
public sealed class CalibrationResponder :
    IPacketResponder<CInfoPacket>,
    IPacketResponder<SkiPacket>,
    IPacketResponder<SuPacket>,
    IPacketResponder<SrPacket>,
    IPacketResponder<BfPacket>,
    IPacketResponder<IvnPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly SkillBarMap _skillBar;
    private readonly ILogger<CalibrationResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="skillBar">The skill bar, held across packets.</param>
    /// <param name="logger">The logger.</param>
    public CalibrationResponder(ProtocolStateManager state, SkillBarMap skillBar, ILogger<CalibrationResponder> logger)
    {
        _state = state;
        _skillBar = skillBar;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<CInfoPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // Authoritative, unlike inferring it from whichever `at` arrives first.
        _state.SetOwnCharacterId(packet.CharacterId);

        _logger.LogInformation
        (
            "CALIBRATION | Character {Name}, id {CharacterId}, class {Class}, level icon {Icon}",
            packet.Name,
            packet.CharacterId,
            packet.Class,
            packet.Icon
        );

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SkiPacket> packetArgs, CancellationToken ct = default)
    {
        var skills = packetArgs.Packet.SkillSubPackets;
        if (skills is null || skills.Count == 0)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        // SkillResponder owns the bar; calibration only reports it, so the mapping is maintained in
        // every mode and not just while calibrating.
        var vnums = skills.Select(s => s.SkillVNum).ToArray();

        var lines = new List<string>(vnums.Length);
        for (var castId = 0; castId < vnums.Length; castId++)
        {
            lines.Add($"cast {castId} -> VNum {vnums[castId]}");
        }

        _logger.LogInformation
        (
            "CALIBRATION | Skill bar changed, {Count} skill(s). Cast a skill to find out which is which:{NewLine}  {Table}",
            skills.Count,
            Environment.NewLine,
            string.Join(Environment.NewLine + "  ", lines)
        );

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SuPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // Only our own casts identify our own bar.
        if (packet.CasterEntityId != _state.OwnCharacterId || packet.SkillVNum is not { } vnum)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        if (_skillBar.TryGetCastId(vnum, out var castId))
        {
            _logger.LogInformation
            (
                "CALIBRATION | You cast VNum {VNum}  ->  add to appsettings.json:  {{ \"CastId\": {CastId}, \"Name\": \"...\", \"MpCost\": 0, \"CooldownSeconds\": 5 }}",
                vnum,
                castId
            );
        }
        else
        {
            _logger.LogInformation
            (
                "CALIBRATION | You cast VNum {VNum}, which is not in the current skill bar (a mate or item skill?).",
                vnum
            );
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SrPacket> packetArgs, CancellationToken ct = default)
    {
        var castId = packetArgs.Packet.SkillId;
        var vnums = _skillBar.VNums;

        // sr carries a skill identifier as the server numbers it. When it lands inside the bar it
        // is almost certainly the cast id, which is exactly what u_s wants.
        var hint = castId >= 0 && castId < vnums.Count
            ? $"that is cast {castId} on the current bar (VNum {vnums[castId]})"
            : "outside the current bar, so it numbers skills differently here";

        _logger.LogInformation("CALIBRATION | Skill {SkillId} came off cooldown - {Hint}.", castId, hint);
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<BfPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        if (packet.EntityId != _state.OwnCharacterId)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        _logger.LogInformation
        (
            "CALIBRATION | Buff applied to you: card {CardId}, lasting {Seconds:0.#}s (raw {Raw}, caster level {Level}). " +
            "A buff cast does not always produce su, which is why it shows up here instead.",
            packet.SubPacket?.CardId,
            (packet.SubPacket?.Time ?? 0) / 10d,
            packet.SubPacket?.Time,
            packet.CasterLevel
        );

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<IvnPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;
        var item = packet.InvSubPacket;

        _logger.LogInformation
        (
            "CALIBRATION | You used an item: bag {Bag}, slot {Slot}, VNum {VNum}, {Amount} left. " +
            "If that was a healing potion:  \"PotionBag\": \"{Bag}\", \"HpPotionSlot\": {Slot}. " +
            "If it was a buff item, put the same bag and slot in the Buffs section instead.",
            packet.Bag,
            item.Slot,
            item.VNum,
            item.RareOrAmount,
            packet.Bag,
            item.Slot
        );

        return Task.FromResult(Result.FromSuccess());
    }
}
