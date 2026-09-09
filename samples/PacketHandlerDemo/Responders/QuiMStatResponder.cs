using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Chat;
using PacketHandlerDemo.Packets;
using PacketHandlerDemo.Services;
using Remora.Results;

namespace PacketHandlerDemo.Responders;

/// <summary>
/// Responder asynchrone : reagit au paquet personnalise <see cref="QuiMStatPacket"/>
/// et au paquet standard <see cref="SayPacket"/>.
/// </summary>
/// <remarks>
/// Une meme classe peut implementer plusieurs <c>IPacketResponder&lt;T&gt;</c> :
/// <c>AddPacketResponder&lt;T&gt;()</c> parcourt toutes les interfaces fermees
/// implementees et enregistre le type sous chacune d'elles.
/// <para>
/// Les dependances arrivent par le constructeur, resolues dans le scope du
/// paquet en cours de traitement.
/// </para>
/// </remarks>
public class QuiMStatResponder : IPacketResponder<QuiMStatPacket>, IPacketResponder<SayPacket>
{
    private readonly ScoreboardService _scoreboard;
    private readonly ILogger<QuiMStatResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuiMStatResponder"/> class.
    /// </summary>
    /// <param name="scoreboard">Le classement partage (singleton).</param>
    /// <param name="logger">Le logger.</param>
    public QuiMStatResponder(ScoreboardService scoreboard, ILogger<QuiMStatResponder> logger)
    {
        _scoreboard = scoreboard;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> Respond(PacketEventArgs<QuiMStatPacket> packetArgs, CancellationToken ct = default)
    {
        // packetArgs.Packet      -> l'objet deserialise, fortement type
        // packetArgs.PacketString -> la chaine brute d'origine
        // packetArgs.Source       -> Server ou Client
        var packet = packetArgs.Packet;

        var total = await _scoreboard.AddScoreAsync(packet.PlayerId, packet.Score, ct);

        _logger.LogInformation
        (
            "[{Source}] joueur {PlayerId} : +{Score} en {Category} ({Label}) -> total {Total}",
            packetArgs.Source,
            packet.PlayerId,
            packet.Score,
            packet.Category,
            packet.Label,
            total
        );

        // Renvoyer une erreur n'interrompt PAS les autres responders : le
        // handler collecte tous les resultats et les agrege.
        return Result.FromSuccess();
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<SayPacket> packetArgs, CancellationToken ct = default)
    {
        _logger.LogInformation("Chat : {Message}", packetArgs.Packet.Message);
        return Task.FromResult(Result.FromSuccess());
    }
}
