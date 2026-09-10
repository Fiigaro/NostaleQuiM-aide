using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Packets;

namespace NosSmoothCustomClient;

/// <summary>
/// Console entry point.
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

        builder.Services.AddBotEngine(mode);
        builder.Services.AddHostedService<ConsoleExitService>();

        var host = builder.Build();

        // The type repository is populated after the container exists, because AddPacketTypes and
        // AddDefaultPackets extend the repository itself rather than IServiceCollection. Without
        // this step every packet deserialises to UnresolvedPacket.
        var registration = BotServiceRegistration.RegisterPacketTypes(host.Services);
        if (!registration.IsSuccess)
        {
            host.Services.GetRequiredService<ILogger<PacketTypeRegistrar>>()
                .LogCritical("Packet type registration failed: {Error}", registration.ToFullString());

            return 2;
        }

        LogStartup(host.Services, mode);

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void LogStartup(IServiceProvider services, RunMode mode)
    {
        var logger = services.GetRequiredService<ILogger<object>>();
        var options = services.GetRequiredService<BotOptions>();

        logger.LogInformation("Mode          : {Mode}", mode);
        logger.LogInformation("Packet handler: {Handler}", services.GetRequiredService<IPacketHandler>().GetType().Name);
        logger.LogInformation("Client        : {Client}", services.GetRequiredService<INostaleClient>().GetType().Name);
        logger.LogInformation("Movement      : {Movement}", services.GetRequiredService<IMovementStrategy>().GetType().Name);
        logger.LogInformation
        (
            "Thresholds    : HP <= {Hp:P0}, MP <= {Mp:P0}, attack every {Attack}ms, tick {Tick}ms",
            options.HpPotionThreshold,
            options.MpPotionThreshold,
            options.AttackInterval.TotalMilliseconds,
            options.TickInterval.TotalMilliseconds
        );
        logger.LogInformation
        (
            "Rotation      : {Rotation}",
            string.Join(" > ", options.Skills.Select(s => $"{s.Name}(cast {s.CastId}, {s.MpCost}mp, {s.EffectiveCooldown.TotalSeconds:0}s)"))
        );
    }
}
