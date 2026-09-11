using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.LocalBinding.Extensions;
using NosSmooth.LocalClient.Extensions;
using NosSmooth.Packets;
using NosSmooth.PacketSerializer.Extensions;
using NosSmooth.Pcap;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.Orchestration;
using NosSmoothCustomClient.Packets;
using NosSmoothCustomClient.Responders;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient;

/// <summary>
/// The single wiring point shared by every front-end.
/// </summary>
/// <remarks>
/// The console and the GUI must run byte-for-byte the same engine; putting the registration here
/// rather than in each entry point is what guarantees that a behaviour verified in one is the
/// behaviour you get in the other.
/// </remarks>
public static class BotServiceRegistration
{
    /// <summary>
    /// Registers the whole bot engine.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="mode">Which transport to bind.</param>
    /// <param name="trace">Whether to log every raw packet in both directions.</param>
    /// <param name="configuration">Configuration to read the "Bot" section from, when available.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBotEngine
    (
        this IServiceCollection services,
        RunMode mode,
        bool trace = false,
        IConfiguration? configuration = null
    )
    {
        var coreAssembly = typeof(BotServiceRegistration).Assembly;
        var stockPacketsAssembly = typeof(IPacket).Assembly;

        // 1. Managed packet handling: deserialises the inbound frame and fans it out to the
        //    typed IPacketResponder<T> implementations.
        services.AddManagedNostaleCore();

        // 2. Serialization core: type repository, converter repository, primitive converters.
        services.AddPacketSerialization();

        // 3. Source-generated converters. Both assemblies matter - the stock NosTale packets live
        //    in NosSmooth.Packets, the custom ones in this library. Miss either and those packets
        //    degrade to UnresolvedPacket.
        services.AddGeneratedSerializers(stockPacketsAssembly);
        services.AddGeneratedSerializers(coreAssembly);
        services.AddSingleton<PacketTypeRegistrar>();

        // 4. Cross-packet state. Responders are resolved per packet, so these must be singletons.
        // Read appsettings.json when there is one. Calibration means changing waypoints, cast ids
        // and slots repeatedly, and needing a rebuild for each of those would make the loop
        // unusable.
        var (options, configurationSummary) = configuration is null
            ? (new BotOptions(), "no configuration provided, using built-in defaults")
            : BotConfigurationFile.Apply(configuration);

        services.AddSingleton(options);
        services.AddSingleton(new BotConfigurationSummary(configurationSummary));
        services.AddSingleton<ProtocolStateManager>();
        services.AddSingleton<SkillRotation>();
        services.AddSingleton<SkillBarMap>();
        services.AddSingleton<BotController>();
        services.AddSingleton<PacketDispatcher>();
        services.AddSingleton<LogBuffer>();
        services.AddSingleton<PacketCounter>();

        // 5. Responders.
        services.AddPacketResponder<PlayerStatsResponder>();
        services.AddPacketResponder<EntitySpawnResponder>();
        services.AddPacketResponder<TargetHpResponder>();
        services.AddPacketResponder<PositionTrackingResponder>();
        services.AddPacketResponder<SkillResponder>();
        services.AddPacketResponder<QuiMStatResponder>();

        // Capture exists to be watched, so tracing is on by default there; elsewhere it is opt-in.
        if (trace || mode == RunMode.Pcap)
        {
            services.AddPacketResponder<PacketTraceResponder>();
        }

        // Capture is the calibration transport, so the deduction runs there by default.
        if (mode == RunMode.Pcap)
        {
            services.AddPacketResponder<CalibrationResponder>();
        }

        // 6. Transport, and the movement strategy that matches it.
        switch (mode)
        {
            case RunMode.Attach:
                services.AddNostaleBindings();
                services.AddLocalClient();

                // Attached, walking goes through the game's own routine, so no checksum is synthesised.
                services.AddSingleton<IMovementStrategy, CommandWalkStrategy>();
                break;

            case RunMode.Pcap:
                services.AddSingleton<PcapOptions>();
                services.AddSingleton<CaptureTarget>();
                services.AddHostedService<CaptureDiagnosticsService>();
                services.AddOptions<PcapNostaleOptions>();
                services.AddSingleton<PcapNostaleManager>();
                services.AddSingleton<ProcessTcpManager>();
                services.AddSingleton<INostaleClient>(PcapClientFactory.Create);
                services.AddSingleton<IMovementStrategy, PacketWalkStrategy>();
                break;

            default:
                services.AddSingleton<SimulatedNostaleClient>();
                services.AddSingleton<INostaleClient>(sp => sp.GetRequiredService<SimulatedNostaleClient>());
                services.AddSingleton<IMovementStrategy, PacketWalkStrategy>();
                break;
        }

        // 7. Workers shared by every front-end.
        services.AddHostedService<NostaleClientHostedService>();
        services.AddHostedService<OrchestrationBackgroundService>();

        return services;
    }

    /// <summary>
    /// Populates the packet type repository. Must run once, after the container is built.
    /// </summary>
    /// <param name="provider">The service provider.</param>
    /// <returns>A result describing whether the stock packets could be registered.</returns>
    public static Remora.Results.Result RegisterPacketTypes(IServiceProvider provider)
        => provider.GetRequiredService<PacketTypeRegistrar>().Register();

    /// <summary>
    /// Gets the assembly holding the custom packet definitions.
    /// </summary>
    /// <returns>The core assembly.</returns>
    public static Assembly CoreAssembly
        => typeof(BotServiceRegistration).Assembly;
}
