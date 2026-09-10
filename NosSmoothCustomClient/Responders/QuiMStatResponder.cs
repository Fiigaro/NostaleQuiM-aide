using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmoothCustomClient.Packets;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Consumes the modified server's <c>quim</c> broadcast.
/// </summary>
/// <remarks>
/// This responder is what proves the custom half of the pipeline is live: the frame only reaches
/// here as a typed <see cref="QuiMStatPacket"/> if the source generator emitted its converter, the
/// converter was registered by AddGeneratedSerializers, and the type was added to the repository. A
/// missing link anywhere in that chain would surface the packet as an UnresolvedPacket instead and
/// this responder would simply never fire.
/// </remarks>
public sealed class QuiMStatResponder : IPacketResponder<QuiMStatPacket>
{
    private readonly ILogger<QuiMStatResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuiMStatResponder"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public QuiMStatResponder(ILogger<QuiMStatResponder> logger)
        => _logger = logger;

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<QuiMStatPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        _logger.LogInformation
        (
            "quim -> player #{PlayerId} is rank {Rank} with {Score} points in '{QuizName}' (custom converter OK).",
            packet.PlayerId,
            packet.CurrentRank,
            packet.Score,
            packet.QuizName
        );

        return Task.FromResult(Result.FromSuccess());
    }
}
