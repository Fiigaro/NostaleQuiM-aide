using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.Packets;
using NosSmoothCustomClient.State;

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
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        var cli = CommandLine.Parse(args);

        if (cli.ListProcesses)
        {
            Console.WriteLine(ProcessScanReport.Render());
            return 0;
        }

        if (cli.TestInput)
        {
            using var testLoggers = LoggerFactory.Create(b => b
                .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
                .SetMinimumLevel(LogLevel.Information));

            return await InputTest.RunAsync(cli.ProcessId, testLoggers, cli.TestKey).ConfigureAwait(false);
        }

        if (!cli.IsSupportedHere(out var reason))
        {
            await Console.Error.WriteLineAsync(reason).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Run without a transport switch to drive the in-process simulator.").ConfigureAwait(false);
            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
        });
        builder.Logging.SetMinimumLevel(cli.Verbose ? LogLevel.Debug : LogLevel.Information);

        builder.Services.AddBotEngine(cli.Mode, cli.Trace, builder.Configuration, cli.Play);
        builder.Services.AddHostedService<ConsoleExitService>();

        if (cli.ProcessId is { } pid)
        {
            builder.Services.AddSingleton(new PcapOptions { ProcessId = pid });
        }

        var host = builder.Build();

        var registration = BotServiceRegistration.RegisterPacketTypes(host.Services);
        if (!registration.IsSuccess)
        {
            host.Services.GetRequiredService<ILogger<PacketTypeRegistrar>>()
                .LogCritical("Packet type registration failed: {Error}", registration.ToFullString());

            return 2;
        }

        ModeStartupPolicy.Apply(host.Services, cli.Mode, cli.Paused, cli.Play);

        // Bind the transport before the loop starts: this is the step that reaches outside the
        // process, so its failures belong here as messages, not later as stack traces.
        if (!TransportBinder.TryBind(host.Services, cli.Mode, out var transportError))
        {
            await Console.Error.WriteLineAsync(transportError).ConfigureAwait(false);
            return 3;
        }

        LogStartup(host.Services, cli.Mode);

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void LogStartup(IServiceProvider services, RunMode mode)
    {
        var logger = services.GetRequiredService<ILogger<object>>();
        var options = services.GetRequiredService<BotOptions>();

        logger.LogInformation("Configuration : {Summary}", services.GetRequiredService<BotConfigurationSummary>().Description);
        logger.LogInformation("Mode          : {Mode}", mode);
        logger.LogInformation("Loop          : {State}", services.GetRequiredService<BotController>().IsRunning ? "running" : "PAUSED (read-only)");
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
