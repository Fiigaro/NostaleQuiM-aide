using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Entities;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Pipes the character's vitals out of the inbound <c>stat</c> packet and into the state manager.
/// </summary>
/// <remarks>
/// Recording only. It used to drink here as well, one packet earlier than the orchestration loop
/// would - and that shaved 300ms off the reaction at the cost of two places deciding the same
/// thing through one cooldown gate. On a server whose outbound traffic cannot be forged the cost
/// came due: this responder took the gate, sent a use-item packet that went nowhere, and the loop's
/// key press was refused for the next three seconds. The potion was never drunk and nothing said
/// so. Consumables belong to the loop, which reaches them through the actuator and therefore works
/// whichever way the bot is driving the client.
/// </remarks>
public sealed class PlayerStatsResponder : IPacketResponder<StatPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly ILogger<PlayerStatsResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerStatsResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public PlayerStatsResponder(ProtocolStateManager state, ILogger<PlayerStatsResponder> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<StatPacket> packetArgs, CancellationToken ct = default)
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

        return Task.FromResult(Result.FromSuccess());
    }
}
