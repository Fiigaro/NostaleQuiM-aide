using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Microsoft.Extensions.DependencyInjection;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// The Avalonia application shell.
/// </summary>
/// <remarks>
/// Avalonia constructs this type itself through <c>AppBuilder.Configure&lt;App&gt;()</c>, so the
/// engine's service provider is handed over through static properties set before the app starts
/// rather than through the constructor.
/// </remarks>
public sealed class App : Application
{
    /// <summary>Gets or sets the engine services. Must be set before the app starts.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>Gets or sets the transport mode shown in the window header.</summary>
    public static RunMode Mode { get; set; } = RunMode.Simulate;

    /// <summary>Gets or sets the reason startup failed, when it did. Shown instead of the dashboard.</summary>
    public static string? StartupError { get; set; }

    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A refusal is a result, and it belongs on screen like any other. Exiting before the
            // window exists leaves nothing to read it in.
            desktop.MainWindow = StartupError is { } error
                ? new StartupErrorWindow(error)
                : Services is not null
                    ? CreateMainWindow(Services, Mode)
                    : new StartupErrorWindow("Le moteur n'a pas pu être construit.");
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the dashboard from the engine services.
    /// </summary>
    /// <param name="services">The engine services.</param>
    /// <param name="mode">The transport mode.</param>
    /// <returns>The window.</returns>
    public static MainWindow CreateMainWindow(IServiceProvider services, RunMode mode)
        => new
        (
            services.GetRequiredService<ProtocolStateManager>(),
            services.GetRequiredService<SkillRotation>(),
            services.GetRequiredService<BuffTracker>(),
            services.GetRequiredService<BotController>(),
            services.GetRequiredService<BotOptions>(),
            services.GetRequiredService<LogBuffer>(),
            mode,
            services.GetService<NosSmoothCustomClient.Input.SwitchableGameInput>(),
            services.GetService<NosSmoothCustomClient.Input.WaypointRecorder>(),
            services.GetService<NosSmoothCustomClient.Orchestration.OrchestrationBackgroundService>()
        );
}
