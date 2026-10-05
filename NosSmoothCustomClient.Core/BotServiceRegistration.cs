using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.Packets;
using NosSmoothCustomClient.Responders;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient;

/// <summary>Marks that the run should begin with keystrokes reaching the game.</summary>
/// <param name="Value">Whether to start live.</param>
public sealed record StartLive(bool Value);

/// <summary>Marks that the run should begin with waypoint recording armed.</summary>
/// <param name="Value">Whether to start armed.</param>
public sealed record StartArmed(bool Value);

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
    /// <param name="play">Whether actions actually reach the game, rather than only being logged.</param>
    /// <param name="recordWaypoints">Whether to record a patrol route instead of acting.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBotEngine
    (
        this IServiceCollection services,
        RunMode mode,
        bool trace = false,
        IConfiguration? configuration = null,
        bool play = false,
        bool recordWaypoints = false
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
        services.AddSingleton<BuffTracker>();
        services.AddSingleton<BotController>();
        services.AddSingleton<PacketDispatcher>();
        services.AddSingleton<LogBuffer>();
        services.AddSingleton<PacketCounter>();
        services.AddSingleton<PacketLog>();

        // The filter writes through the stored settings, so what the window narrows is what the
        // next save records. Tracing to the log stays opt-in outside capture, where the transport
        // already prints its own frames and a second copy would double every line.
        services.AddSingleton(sp => new PacketFilter(sp.GetRequiredService<BotOptions>().PacketTrace)
        {
            TraceToLog = trace || mode == RunMode.Pcap
        });

        // 5. Responders.
        services.AddPacketResponder<PlayerStatsResponder>();
        services.AddPacketResponder<EntitySpawnResponder>();
        services.AddPacketResponder<TargetHpResponder>();
        services.AddPacketResponder<PositionTrackingResponder>();
        services.AddPacketResponder<MapResponder>();

        // Always registered, recording or not: responders write into it unconditionally and it is
        // inert until a run is being recorded.
        services.TryAddSingleton<RunJournal>();
        services.AddPacketResponder<SkillResponder>();
        services.AddPacketResponder<BuffResponder>();
        services.AddPacketResponder<QuiMStatResponder>();

        // Registered whatever the mode: it is what fills the ring the packet view reads back, and
        // the filter decides on its own whether anything also reaches the log. Making the
        // registration conditional instead would mean the view is empty in the very modes where a
        // frame is worth looking up before repeating it.
        services.AddPacketResponder<PacketTraceResponder>();

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

                // Capture can read this server faithfully but cannot forge its outbound traffic, so
                // acting goes through the keyboard instead. Live or dry run is a runtime switch
                // rather than a launch flag, which is what makes it usable as a kill switch.
                services.AddSingleton<SwitchableGameInput>();
                services.AddSingleton<IGameInput>(sp => sp.GetRequiredService<SwitchableGameInput>());
                services.AddSingleton<IBotActuator, InputActuator>();

                // Always available so the window can arm it; the command line flag only decides
                // whether it starts armed.
                services.AddSingleton<WaypointRecorder>();
                services.AddHostedService(sp => sp.GetRequiredService<WaypointRecorder>());

                // Records a run played by hand, so a sequence can be derived from what was actually
                // done rather than from a description of it.
                services.AddSingleton<RunRecorder>();
                services.AddHostedService(sp => sp.GetRequiredService<RunRecorder>());

                if (play && OperatingSystem.IsWindows())
                {
                    services.AddSingleton(new StartLive(true));
                }

                if (recordWaypoints)
                {
                    services.AddSingleton(new StartArmed(true));
                }

                break;

            default:
                services.AddSingleton<SimulatedNostaleClient>();
                services.AddSingleton<INostaleClient>(sp => sp.GetRequiredService<SimulatedNostaleClient>());
                services.AddSingleton<IMovementStrategy, PacketWalkStrategy>();
                break;
        }

        // The packet path stays the reference for everything the simulator drives.
        services.TryAddSingleton<IBotActuator, PacketActuator>();

        // Built by hand rather than resolved: the live/dry-run switch only exists under capture,
        // and the transport is what decides whether a packet can be sent at all.
        services.AddSingleton(sp => new ManualSender
        (
            sp.GetRequiredService<INostaleClient>(),
            sp.GetRequiredService<IBotActuator>(),
            sp.GetRequiredService<PacketLog>(),
            sp.GetService<SwitchableGameInput>(),
            mode,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ManualSender>>()
        ));

        // 7. Workers shared by every front-end.
        services.AddHostedService<NostaleClientHostedService>();
        // Registered as itself and forwarded, so the window can read what the loop last decided
        // rather than inferring it from the log.
        services.AddSingleton<InstanceLauncher>();
        services.AddSingleton<OrchestrationBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<OrchestrationBackgroundService>());

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
    /// Applies the trace narrowing asked for on the command line.
    /// </summary>
    /// <param name="provider">The service provider.</param>
    /// <param name="cli">The parsed command line.</param>
    /// <remarks>
    /// Naming headers also turns the trace on. Narrowing a trace that is not being printed would
    /// otherwise produce silence, which reads exactly like a transport that has stopped seeing the
    /// game.
    /// </remarks>
    public static void ApplyTraceFilter(IServiceProvider provider, CommandLine cli)
    {
        if (cli.Only is null && cli.Hide is null)
        {
            return;
        }

        var filter = provider.GetRequiredService<PacketFilter>();

        if (cli.Only is { } only)
        {
            filter.Only = only;
        }

        if (cli.Hide is { } hide)
        {
            filter.Hide = hide;
        }

        filter.TraceToLog = true;
    }

    /// <summary>
    /// Gets the assembly holding the custom packet definitions.
    /// </summary>
    /// <returns>The core assembly.</returns>
    public static Assembly CoreAssembly
        => typeof(BotServiceRegistration).Assembly;
}
