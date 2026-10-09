using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// What starting the engine produced: a running host, or the reason there is none.
/// </summary>
/// <param name="Host">The started host, when it started.</param>
/// <param name="Cli">The command line it was started from.</param>
/// <param name="Error">Why it did not start, when it did not.</param>
/// <param name="ExitCode">The process exit code that failure maps to.</param>
public sealed record EngineStart(IHost? Host, CommandLine Cli, string? Error, int ExitCode);

/// <summary>
/// Builds, binds and starts the engine from a command line.
/// </summary>
/// <remarks>
/// Shared by the two ways the window starts: from switches typed after <c>dotnet run</c>, and from
/// the launcher, which writes the same switches from what was picked in it. One path means a choice
/// made by clicking binds exactly the way the same choice typed would.
/// </remarks>
public static class Engine
{
    /// <summary>
    /// Starts the engine. Never throws for a setup problem: the reason comes back as text.
    /// </summary>
    /// <param name="args">The switches, as typed or as written by the launcher.</param>
    /// <returns>The outcome.</returns>
    public static async Task<EngineStart> StartAsync(string[] args)
    {
        var cli = CommandLine.Parse(args);

        if (!cli.IsSupportedHere(out var reason))
        {
            return new EngineStart(null, cli, reason, 1);
        }

        var host = BuildHost(cli, args);

        var registration = BotServiceRegistration.RegisterPacketTypes(host.Services);
        if (!registration.IsSuccess)
        {
            host.Dispose();
            return new EngineStart(null, cli, $"Packet type registration failed: {registration.Error?.Message}", 2);
        }

        BotServiceRegistration.ApplyTraceFilter(host.Services, cli);

        if (!TransportBinder.TryBind(host.Services, cli.Mode, out var transportError))
        {
            host.Dispose();
            return new EngineStart(null, cli, transportError, 3);
        }

        // After the bind, never before: --play asks for the live keyboard, which is refused while
        // no game window is bound.
        ModeStartupPolicy.Apply(host.Services, cli.Mode, cli.Paused, cli.Play, cli.RecordWaypoints);

        await host.StartAsync().ConfigureAwait(false);
        return new EngineStart(host, cli, null, 0);
    }

    /// <summary>
    /// Stops and disposes a started host.
    /// </summary>
    /// <param name="host">The host, if any.</param>
    /// <returns>A task.</returns>
    public static async Task StopAsync(IHost? host)
    {
        if (host is null)
        {
            return;
        }

        await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        host.Dispose();
    }

    private static IHost BuildHost(CommandLine cli, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Loaded after appsettings.json so anything tuned in the window wins over the file.
        builder.Configuration.AddJsonFile(LocalConfigurationWriter.FileName, optional: true, reloadOnChange: false);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        builder.Services.AddBotEngine(cli.Mode, cli.Trace, builder.Configuration, cli.Play, cli.RecordWaypoints);

        if (cli.ProcessId is { } pid)
        {
            builder.Services.AddSingleton(new PcapOptions { ProcessId = pid });
        }

        // The engine still logs into the buffer: the self-test reads it to prove the engine ran.
        builder.Services.AddSingleton<ILoggerProvider>(sp => new LogBufferProvider(sp.GetRequiredService<LogBuffer>()));

        return builder.Build();
    }
}
