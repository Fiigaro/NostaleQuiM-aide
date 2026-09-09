using Microsoft.Extensions.Logging;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmooth.Packets;
using Remora.Results;

namespace PacketHandlerDemo.Responders;

/// <summary>
/// Responder de diagnostic, tres utile quand on met au point un nouveau paquet.
/// </summary>
/// <remarks>
/// Quand <c>ManagedPacketHandler</c> n'arrive pas a produire un objet typé, il
/// fabrique tout de meme un paquet et le dispatche :
/// <list type="bullet">
/// <item><see cref="UnresolvedPacket"/> : aucun type n'est enregistre pour cet
/// en-tete (oubli de <c>AddPacketType</c> / <c>AddPacketTypes</c>).</item>
/// <item><see cref="ParsingFailedPacket"/> : le type existe mais la chaine ne
/// correspond pas a la definition (mauvais index, mauvais type de champ...).</item>
/// </list>
/// </remarks>
public class UnknownPacketResponder : IPacketResponder<UnresolvedPacket>, IPacketResponder<ParsingFailedPacket>
{
    private readonly ILogger<UnknownPacketResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownPacketResponder"/> class.
    /// </summary>
    /// <param name="logger">Le logger.</param>
    public UnknownPacketResponder(ILogger<UnknownPacketResponder> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<UnresolvedPacket> packetArgs, CancellationToken ct = default)
    {
        _logger.LogWarning("En-tete inconnue, paquet non resolu : {Packet}", packetArgs.PacketString);
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<ParsingFailedPacket> packetArgs, CancellationToken ct = default)
    {
        _logger.LogWarning("Echec de deserialisation : {Packet}", packetArgs.PacketString);
        _logger.LogResultError(packetArgs.Packet.SerializerResult);
        return Task.FromResult(Result.FromSuccess());
    }
}
