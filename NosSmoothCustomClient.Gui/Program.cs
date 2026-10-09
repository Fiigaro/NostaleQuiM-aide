using Avalonia;
using NosSmoothCustomClient.Configuration;

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

        var selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);

        // No transport asked for - a double-click on the .exe, most of the time. The launcher asks
        // the questions the switches used to: which NosTale, and whether the bot may play. Falling
        // back to the simulator instead would fill the window with invented vitals that look like
        // a bot which stopped seeing the game.
        if (!CommandLine.Parse(args).TransportRequested && !selfTest)
        {
            UseProgramDirectoryWhenLost();
            App.LauncherArgs = args;

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                await Engine.StopAsync(App.Engine).ConfigureAwait(false);
            }

            return 0;
        }

        // The self-test drives the simulator when nothing else was named.
        var engineArgs = selfTest && !CommandLine.Parse(args).TransportRequested
            ? args.Append("--simulate").ToArray()
            : args;

        var started = await Engine.StartAsync(engineArgs).ConfigureAwait(false);

        if (started.Host is not { } host)
        {
            await Console.Error.WriteLineAsync(started.Error).ConfigureAwait(false);

            // Shown rather than only printed: the console this was launched from may be behind
            // another window, or gone. Without the engine there is nothing to start, so the window
            // is the whole application from here.
            if (!selfTest)
            {
                App.StartupError = started.Error;
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }

            return started.ExitCode;
        }

        App.Services = host.Services;
        App.Mode = started.Cli.Mode;

        // A headless pass that lets the engine actually run, then builds the window against the
        // state it produced. Proves the GUI front-end drives the same engine as the console one,
        // on a machine with no display attached.
        if (selfTest)
        {
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            var code = SelfTest.Run(host.Services, started.Cli.Mode);
            await Engine.StopAsync(host).ConfigureAwait(false);
            return code;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            await Engine.StopAsync(host).ConfigureAwait(false);
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

    /// <summary>
    /// Settles on the folder next to the .exe when the current one holds no settings.
    /// </summary>
    /// <remarks>
    /// Settings are read from, and saved to, the current folder. A double-click starts there, but a
    /// shortcut or an elevation prompt can start in C:\Windows\System32 - and an elevated process
    /// would happily write its settings into it. A folder that already holds settings is kept, so
    /// <c>dotnet run</c> from the repository still reads the repository's files.
    /// </remarks>
    private static void UseProgramDirectoryWhenLost()
    {
        var current = Directory.GetCurrentDirectory();

        if (File.Exists(Path.Combine(current, "appsettings.json"))
            || File.Exists(Path.Combine(current, LocalConfigurationWriter.FileName)))
        {
            return;
        }

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    }
}
