using NosSmooth.Packets;
using NosSmooth.PacketSerializer.Abstractions.Attributes;

namespace NosSmoothCustomClient.Packets;

/// <summary>
/// A quiz/entity broadcast emitted by the modified server infrastructure.
/// </summary>
/// <remarks>
/// Written as a positional record on purpose: NosSmooth.PacketSerializersGenerator reads the
/// <see cref="PacketIndexAttribute"/> off the primary constructor parameters and emits a
/// QuiMStatPacketConverter : BaseStringConverter&lt;QuiMStatPacket&gt; into the
/// NosSmoothCustomClient.Packets.Generated namespace at compile time.
/// </remarks>
/// <param name="PlayerId">The id of the broadcasting player.</param>
/// <param name="CurrentRank">The player's current rank.</param>
/// <param name="Score">The player's current score.</param>
/// <param name="QuizName">The name of the quiz.</param>
[PacketHeader("quim", PacketSource.Server)]
[GenerateSerializer(true)]
public record QuiMStatPacket
(
    [PacketIndex(0)] long PlayerId,
    [PacketIndex(1)] int CurrentRank,
    [PacketIndex(2)] int Score,
    [PacketIndex(3)] string QuizName
) : IPacket;
