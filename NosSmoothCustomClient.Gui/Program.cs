using Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// GUI entry point.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the dashboard on top of the shared engine.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        var cli = CommandLine.Parse(args);

        if (!cli.IsSupportedHere(out var reason))
        {
            await Console.Error.WriteLineAsync(reason).ConfigureAwait(false);
            return 1;
        }

        using var host = BuildHost(cli, args);

        var registration = BotServiceRegistration.RegisterPacketTypes(host.Services);
        if (!registration.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"Packet type registration failed: {registration.Error?.Message}").ConfigureAwait(false);
            return 2;
        }

        App.Services = host.Services;
        App.Mode = cli.Mode;

        ModeStartupPolicy.Apply(host.Services, cli.Mode, cli.Paused, cli.Play);

        if (!TransportBinder.TryBind(host.Services, cli.Mode, out var transportError))
        {
            await Console.Error.WriteLineAsync(transportError).ConfigureAwait(false);
            return 3;
        }

        await host.StartAsync().ConfigureAwait(false);

        // A headless pass that lets the engine actually run, then builds the window against the
        // state it produced. Proves the GUI front-end drives the same engine as the console one,
        // on a machine with no display attached.
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            var code = SelfTest.Run(host.Services, cli.Mode);
            await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return code;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Builds the Avalonia application. Named by convention so the Avalonia tooling finds it.
    /// </summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static IHost BuildHost(CommandLine cli, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Loaded after appsettings.json so anything tuned in the window wins over the file.
        builder.Configuration.AddJsonFile(LocalConfigurationWriter.FileName, optional: true, reloadOnChange: false);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        builder.Services.AddBotEngine(cli.Mode, cli.Trace, builder.Configuration, cli.Play);

        if (cli.ProcessId is { } pid)
        {
            builder.Services.AddSingleton(new PcapOptions { ProcessId = pid });
        }

        // The window reads the same stream the console front-end prints.
        builder.Services.AddSingleton<ILoggerProvider>(sp => new LogBufferProvider(sp.GetRequiredService<LogBuffer>()));

        return builder.Build();
    }
}
