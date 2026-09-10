using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;

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
        var mode = args.Contains("--attach", StringComparer.OrdinalIgnoreCase)
            ? RunMode.Attach
            : RunMode.Simulate;

        if (mode == RunMode.Attach && !OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync
            (
                "--attach binds to a running NosTale process and is Windows x86 only. " +
                "Run without --attach to drive the in-process simulator."
            );

            return 1;
        }

        using var host = BuildHost(mode, args);

        var registration = BotServiceRegistration.RegisterPacketTypes(host.Services);
        if (!registration.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"Packet type registration failed: {registration.Error?.Message}");
            return 2;
        }

        App.Services = host.Services;
        App.Mode = mode;

        await host.StartAsync().ConfigureAwait(false);

        // A headless pass that lets the engine actually run, then builds the window against the
        // state it produced. Proves the GUI front-end drives the same engine as the console one,
        // on a machine with no display attached.
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            var code = SelfTest.Run(host.Services, mode);
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

    private static IHost BuildHost(RunMode mode, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        builder.Services.AddBotEngine(mode);

        // The window reads the same stream the console front-end prints.
        builder.Services.AddSingleton<ILoggerProvider>(sp => new LogBufferProvider(sp.GetRequiredService<LogBuffer>()));

        return builder.Build();
    }
}
