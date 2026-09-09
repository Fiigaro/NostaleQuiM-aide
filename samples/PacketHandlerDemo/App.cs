using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.PacketSerializer.Extensions;
using NosSmooth.PacketSerializer.Packets;
using PacketHandlerDemo.Packets;
using PacketHandlerDemo.Services;

namespace PacketHandlerDemo;

/// <summary>
/// Service hote : enregistre les types de paquets puis lance le client.
/// </summary>
public class App : BackgroundService
{
    private readonly INostaleClient _client;
    private readonly IPacketTypesRepository _packetTypesRepository;
    private readonly ScoreboardService _scoreboard;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<App> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class.
    /// </summary>
    /// <param name="client">Le client NosTale.</param>
    /// <param name="packetTypesRepository">Le registre des types de paquets.</param>
    /// <param name="scoreboard">Le classement partage.</param>
    /// <param name="lifetime">Le cycle de vie de l'application.</param>
    /// <param name="logger">Le logger.</param>
    public App
    (
        INostaleClient client,
        IPacketTypesRepository packetTypesRepository,
        ScoreboardService scoreboard,
        IHostApplicationLifetime lifetime,
        ILogger<App> logger
    )
    {
        _client = client;
        _packetTypesRepository = packetTypesRepository;
        _scoreboard = scoreboard;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Les paquets NosTale livres avec NosSmooth.Packets.
        //    Le registre n'est PAS peuple automatiquement : sans cet appel,
        //    tout arrive sous forme d'UnresolvedPacket.
        var defaultPackets = _packetTypesRepository.AddDefaultPackets();
        if (!defaultPackets.IsSuccess)
        {
            _logger.LogResultError(defaultPackets);
            _lifetime.StopApplication();
            return;
        }

        // 2. Les paquets personnalises de CET assembly.
        //    Equivalent unitaire : AddPacketType(typeof(QuiMStatPacket)).
        var customPackets = _packetTypesRepository.AddPacketTypes(typeof(QuiMStatPacket).Assembly);
        if (!customPackets.IsSuccess)
        {
            _logger.LogResultError(customPackets);
            _lifetime.StopApplication();
            return;
        }

        var runResult = await _client.RunAsync(stoppingToken);
        if (!runResult.IsSuccess)
        {
            _logger.LogResultError(runResult);
        }

        foreach (var (playerId, total) in await _scoreboard.GetRankingAsync(stoppingToken))
        {
            _logger.LogInformation("Classement : joueur {PlayerId} = {Total} points", playerId, total);
        }

        _lifetime.StopApplication();
    }
}
