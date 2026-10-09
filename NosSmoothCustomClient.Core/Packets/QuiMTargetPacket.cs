using NosSmooth.Packets;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.PacketSerializer.Abstractions.Attributes;

namespace NosSmoothCustomClient.Packets;

/// <summary>
/// The explicit model for the server's custom lock-on broadcast.
/// </summary>
/// <remarks>
/// This is the "explicit model" half of Step 3.2. Where the stock <c>st</c> packet is not emitted
/// by the modified server, this packet carries the same information and
/// <see cref="Responders.TargetHpResponder"/> consumes either shape.
/// The trailing index is optional so a shorter server build still parses.
/// </remarks>
/// <param name="EntityType">The kind of the locked entity.</param>
/// <param name="EntityId">The id of the locked entity.</param>
/// <param name="Hp">The absolute HP of the locked entity.</param>
/// <param name="MaxHp">The maximum HP of the locked entity.</param>
/// <param name="HpPercentage">The HP percentage of the locked entity, absent on shorter server builds.</param>
[PacketHeader("quimtg", PacketSource.Server)]
[GenerateSerializer(true)]
public record QuiMTargetPacket
(
    [PacketIndex(0)] EntityType EntityType,
    [PacketIndex(1)] long EntityId,
    [PacketIndex(2)] long Hp,
    [PacketIndex(3)] long MaxHp,
    [PacketIndex(4, IsOptional = true)] byte? HpPercentage
) : IPacket;
