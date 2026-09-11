using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Client.Inventory;
using NosSmooth.Packets.Server.Entities;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Pipes the character's vitals out of the inbound <c>stat</c> packet and into the state manager,
/// and injects a consumable frame the moment either pool crosses its threshold.
/// </summary>
/// <remarks>
/// Reacting here rather than waiting for the next orchestration tick keeps the survival latency at
/// one packet instead of up to a full tick. The cooldown gate lives in
/// <see cref="ProtocolStateManager"/>, so this responder and the orchestration loop cannot both fire
/// the same consumable.
/// </remarks>
public sealed class PlayerStatsResponder : IPacketResponder<StatPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly PacketDispatcher _dispatcher;
    private readonly BotOptions _options;
    private readonly ILogger<PlayerStatsResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerStatsResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="dispatcher">The outbound dispatcher.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public PlayerStatsResponder
    (
        ProtocolStateManager state,
        PacketDispatcher dispatcher,
        BotOptions options,
        ILogger<PlayerStatsResponder> logger
    )
    {
        _state = state;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> Respond(PacketEventArgs<StatPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;
        _state.UpdateVitals(packet.Hp, packet.HpMaximum, packet.Mp, packet.MpMaximum);

        _logger.LogDebug
        (
            "stat -> hp {Hp}/{MaxHp} ({HpRatio:P0}) mp {Mp}/{MaxMp} ({MpRatio:P0})",
            packet.Hp,
            packet.HpMaximum,
            _state.HpRatio,
            packet.Mp,
            packet.MpMaximum,
            _state.MpRatio
        );

        var results = new List<Result>(2);

        if (_options.HpPotionSlot is { } hpSlot
            && _state.IsHpCritical
            && _state.TryTakeHpPotionGate(_options.PotionCooldown))
        {
            _logger.LogInformation
            (
                "HP at {Ratio:P0} (threshold {Threshold:P0}) - using HP consumable from {Bag} slot {Slot}.",
                _state.HpRatio,
                _options.HpPotionThreshold,
                _options.PotionBag,
                hpSlot
            );

            results.Add(await _dispatcher.SendAsync(new UseItemPacket(_options.PotionBag, hpSlot), ct));
        }

        if (_options.MpPotionSlot is { } mpSlot
            && _state.IsMpCritical
            && _state.TryTakeMpPotionGate(_options.PotionCooldown))
        {
            _logger.LogInformation
            (
                "MP at {Ratio:P0} (threshold {Threshold:P0}) - using MP consumable from {Bag} slot {Slot}.",
                _state.MpRatio,
                _options.MpPotionThreshold,
                _options.PotionBag,
                mpSlot
            );

            results.Add(await _dispatcher.SendAsync(new UseItemPacket(_options.PotionBag, mpSlot), ct));
        }

        return ResultAggregate.Combine(results);
    }
}
