using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Commands;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmooth.PacketSerializer.Extensions;
using PacketHandlerDemo.Packets;
using PacketHandlerDemo.Responders;
using PacketHandlerDemo.Services;

namespace PacketHandlerDemo;

/// <summary>
/// Point d'entree : cablage complet de l'injection de dependances.
/// </summary>
public static class Program
{
    /// <summary>
    /// Point d'entree.
    /// </summary>
    /// <param name="args">Chemin facultatif vers un fichier de paquets.</param>
    /// <returns>Une tache representant l'operation asynchrone.</returns>
    public static async Task Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : "packets-sample.txt";
        await using var stream = File.OpenRead(path);

        await Host.CreateDefaultBuilder()
            .ConfigureServices
            (
                services =>
                {
                    services
                        // 1. Le coeur "manage" : remplace le handler brut par
                        //    ManagedPacketHandler (celui qui deserialise et
                        //    dispatche vers IPacketResponder<T>) et appelle
                        //    AddPacketSerialization() au passage.
                        .AddManagedNostaleCore()

                        // 2. AddPacketSerialization() n'enregistre que les
                        //    convertisseurs generes DANS NosSmooth.Packets.
                        //    Pour nos propres paquets, il faut declarer notre
                        //    assembly, sinon aucun IStringConverter<QuiMStatPacket>
                        //    n'est disponible.
                        .AddGeneratedSerializers(typeof(QuiMStatPacket).Assembly)

                        // 3. Les responders. Chaque interface IPacketResponder<T>
                        //    implementee est enregistree en Scoped.
                        .AddPacketResponder<QuiMStatResponder>()
                        .AddPacketResponder<UnknownPacketResponder>()

                        // 4. L'etat partage entre les scopes.
                        .AddSingleton<ScoreboardService>()

                        // 5. Le client : ici un lecteur de fichier, ailleurs un
                        //    client reseau ou local.
                        .AddSingleton<INostaleClient>
                        (
                            p => new StreamNostaleClient
                            (
                                stream,
                                p.GetRequiredService<IPacketHandler>(),
                                p.GetRequiredService<CommandProcessor>(),
                                p.GetRequiredService<ILogger<StreamNostaleClient>>()
                            )
                        )
                        .AddHostedService<App>();
                }
            )
            .UseConsoleLifetime()
            .Build()
            .RunAsync();
    }
}
