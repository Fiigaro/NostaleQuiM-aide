using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Commands;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using Remora.Results;

namespace PacketHandlerDemo;

/// <summary>
/// Un client NosTale minimal qui rejoue des paquets depuis un fichier texte.
/// </summary>
/// <remarks>
/// Aucun jeu n'est requis : c'est la maniere la plus simple d'etudier la chaine
/// de deserialisation. Le seul contrat a respecter est de passer chaque ligne a
/// <see cref="IPacketHandler.HandlePacketAsync"/> avec sa
/// <see cref="PacketSource"/> ; tout le reste (deserialisation, ouverture du
/// scope DI, appel des responders) est fait par NosSmooth.
/// <para>
/// Format du fichier : <c>&lt; paquet</c> pour un paquet serveur,
/// <c>&gt; paquet</c> pour un paquet client, <c>#</c> pour un commentaire.
/// </para>
/// </remarks>
public class StreamNostaleClient : BaseNostaleClient
{
    private readonly Stream _stream;
    private readonly IPacketHandler _packetHandler;
    private readonly ILogger<StreamNostaleClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamNostaleClient"/> class.
    /// </summary>
    /// <param name="stream">Le flux contenant les paquets.</param>
    /// <param name="packetHandler">Le handler de paquets fourni par NosSmooth.</param>
    /// <param name="commandProcessor">Le processeur de commandes.</param>
    /// <param name="logger">Le logger.</param>
    public StreamNostaleClient
    (
        Stream stream,
        IPacketHandler packetHandler,
        CommandProcessor commandProcessor,
        ILogger<StreamNostaleClient> logger
    )
        : base(commandProcessor)
    {
        _stream = stream;
        _packetHandler = packetHandler;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<Result> RunAsync(CancellationToken stopRequested = default)
    {
        using var reader = new StreamReader(_stream);

        while (await reader.ReadLineAsync(stopRequested) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var source = trimmed[0] switch
            {
                '<' => PacketSource.Server,
                '>' => PacketSource.Client,
                _ => (PacketSource?)null
            };

            if (source is null)
            {
                _logger.LogError("Ligne ignoree, prefixe '<' ou '>' attendu : {Line}", line);
                continue;
            }

            var packetString = trimmed[1..].Trim();
            var result = await _packetHandler.HandlePacketAsync(this, source.Value, packetString, stopRequested);
            if (!result.IsSuccess)
            {
                _logger.LogResultError(result);
            }
        }

        return Result.FromSuccess();
    }

    /// <inheritdoc />
    public override Task<Result> SendPacketAsync(string packetString, CancellationToken ct = default)
        => _packetHandler.HandlePacketAsync(this, PacketSource.Client, packetString, ct);

    /// <inheritdoc />
    public override Task<Result> ReceivePacketAsync(string packetString, CancellationToken ct = default)
        => _packetHandler.HandlePacketAsync(this, PacketSource.Server, packetString, ct);
}
