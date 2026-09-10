using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmooth.LocalBinding.Extensions;
using NosSmooth.LocalClient.Extensions;
using NosSmooth.Packets;
using NosSmooth.PacketSerializer.Extensions;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Orchestration;
using NosSmoothCustomClient.Packets;
using NosSmoothCustomClient.Responders;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient;

/// <summary>
/// How the assembly talks to the game.
/// </summary>
public enum RunMode
{
    /// <summary>
    /// Frames are synthesised in-process. Runs anywhere, attaches to nothing.
    /// </summary>
    Simulate,

    /// <summary>
    /// Binds to a running NosTale process through NosSmooth.LocalClient. Windows x86 only.
    /// </summary>
    Attach
}

/// <summary>
/// Entry point.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the client.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        var mode = args.Contains("--attach", StringComparer.OrdinalIgnoreCase)
            ? RunMode.Attach
            : RunMode.Simulate;

        if (mode == RunMode.Attach && !OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync
            (
                "--attach binds to a running NosTale process through Reloaded.Hooks and is Windows x86 only. " +
                "Run without --attach to exercise the pipeline against the in-process simulator."
            );

            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
        });
        builder.Logging.SetMinimumLevel(args.Contains("--verbose", StringComparer.OrdinalIgnoreCase)
            ? LogLevel.Debug
            : LogLevel.Information);

        ConfigureServices(builder.Services, mode);

        var host = builder.Build();

        // The type repository is populated once, after the container exists, because
        // AddPacketTypes/AddDefaultPackets are extensions on the repository itself rather than on
        // IServiceCollection. Without this step every packet deserialises to UnresolvedPacket.
        var registration = host.Services.GetRequiredService<PacketTypeRegistrar>().Register();
        if (!registration.IsSuccess)
        {
            var logger = host.Services.GetRequiredService<ILogger<PacketTypeRegistrar>>();
            logger.LogCritical("Packet type registration failed: {Error}", registration.ToFullString());
            return 2;
        }

        LogStartup(host.Services, mode);

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ConfigureServices(IServiceCollection services, RunMode mode)
    {
        var customAssembly = typeof(Program).Assembly;
        var stockPacketsAssembly = typeof(IPacket).Assembly;

        // 1. Managed packet handling. This binds ManagedPacketHandler, which deserialises the
        //    inbound frame and fans it out to the typed IPacketResponder<T> implementations.
        services.AddManagedNostaleCore();

        // 2. Serialization core: the type repository, the string converter repository and the
        //    basic converters for primitives and enums.
        services.AddPacketSerialization();

        // 3. The source-generated converters. Both assemblies matter: the stock NosTale packets
        //    live in NosSmooth.Packets, our custom ones in this assembly. Miss either and the
        //    corresponding packets fall back to UnresolvedPacket.
        services.AddGeneratedSerializers(stockPacketsAssembly);
        services.AddGeneratedSerializers(customAssembly);
        services.AddSingleton<PacketTypeRegistrar>();

        // 4. Cross-packet state. Responders are resolved per packet, so this must be a singleton.
        services.AddSingleton<BotOptions>();
        services.AddSingleton<ProtocolStateManager>();
        services.AddSingleton<PacketDispatcher>();

        // 5. The responders.
        services.AddPacketResponder<PlayerStatsResponder>();
        services.AddPacketResponder<EntitySpawnResponder>();
        services.AddPacketResponder<TargetHpResponder>();
        services.AddPacketResponder<PositionTrackingResponder>();
        services.AddPacketResponder<QuiMStatResponder>();

        // 6. The transport, and the movement strategy that matches it.
        if (mode == RunMode.Attach)
        {
            services.AddNostaleBindings();
            services.AddLocalClient();

            // Attached, walking goes through the game's own routine: it builds a valid frame
            // itself, so no checksum has to be synthesised.
            services.AddSingleton<IMovementStrategy, CommandWalkStrategy>();
        }
        else
        {
            services.AddSingleton<SimulatedNostaleClient>();
            services.AddSingleton<INostaleClient>(sp => sp.GetRequiredService<SimulatedNostaleClient>());
            services.AddSingleton<IMovementStrategy, PacketWalkStrategy>();
        }

        // 7. The workers.
        services.AddHostedService<NostaleClientHostedService>();
        services.AddHostedService<OrchestrationBackgroundService>();
        services.AddHostedService<ConsoleExitService>();
    }

    private static void LogStartup(IServiceProvider services, RunMode mode)
    {
        var logger = services.GetRequiredService<ILogger<object>>();
        var handler = services.GetRequiredService<IPacketHandler>();
        var client = services.GetRequiredService<INostaleClient>();
        var movement = services.GetRequiredService<IMovementStrategy>();
        var options = services.GetRequiredService<BotOptions>();

        logger.LogInformation("Mode          : {Mode}", mode);
        logger.LogInformation("Packet handler: {Handler}", handler.GetType().Name);
        logger.LogInformation("Client        : {Client}", client.GetType().Name);
        logger.LogInformation("Movement      : {Movement}", movement.GetType().Name);
        logger.LogInformation
        (
            "Thresholds    : HP <= {Hp:P0}, MP <= {Mp:P0}, attack every {Attack}ms, tick {Tick}ms",
            options.HpPotionThreshold,
            options.MpPotionThreshold,
            options.AttackInterval.TotalMilliseconds,
            options.TickInterval.TotalMilliseconds
        );
    }
}
