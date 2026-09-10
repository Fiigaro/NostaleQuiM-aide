using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.LocalBinding.Extensions;
using NosSmooth.LocalClient.Extensions;
using NosSmooth.Packets;
using NosSmooth.PacketSerializer.Extensions;
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
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBotEngine(this IServiceCollection services, RunMode mode)
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
        services.AddSingleton<BotOptions>();
        services.AddSingleton<ProtocolStateManager>();
        services.AddSingleton<SkillRotation>();
        services.AddSingleton<BotController>();
        services.AddSingleton<PacketDispatcher>();
        services.AddSingleton<LogBuffer>();

        // 5. Responders.
        services.AddPacketResponder<PlayerStatsResponder>();
        services.AddPacketResponder<EntitySpawnResponder>();
        services.AddPacketResponder<TargetHpResponder>();
        services.AddPacketResponder<PositionTrackingResponder>();
        services.AddPacketResponder<SkillResponder>();
        services.AddPacketResponder<QuiMStatResponder>();

        // 6. Transport, and the movement strategy that matches it.
        if (mode == RunMode.Attach)
        {
            services.AddNostaleBindings();
            services.AddLocalClient();

            // Attached, walking goes through the game's own routine, so no checksum is synthesised.
            services.AddSingleton<IMovementStrategy, CommandWalkStrategy>();
        }
        else
        {
            services.AddSingleton<SimulatedNostaleClient>();
            services.AddSingleton<INostaleClient>(sp => sp.GetRequiredService<SimulatedNostaleClient>());
            services.AddSingleton<IMovementStrategy, PacketWalkStrategy>();
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
