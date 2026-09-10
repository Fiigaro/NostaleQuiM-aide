using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// Constructs the dashboard under Avalonia's headless platform and asserts it came up.
/// </summary>
/// <remarks>
/// This exists because the window is authored on a machine with no display. Compiling proves the
/// syntax; this proves the visual tree actually builds, the controls resolve from the container and
/// a refresh against live engine state does not throw.
/// </remarks>
public static class SelfTest
{
    /// <summary>
    /// Runs the headless construction pass.
    /// </summary>
    /// <param name="services">The engine services.</param>
    /// <param name="mode">The transport mode.</param>
    /// <returns>0 when every check passed, 3 otherwise.</returns>
    public static int Run(IServiceProvider services, RunMode mode)
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();

        var window = App.CreateMainWindow(services, mode);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The engine has been running for a few seconds by now; assert against what it actually
        // produced rather than against values injected here.
        var state = services.GetRequiredService<ProtocolStateManager>();
        var options = services.GetRequiredService<BotOptions>();
        var logs = services.GetRequiredService<LogBuffer>();
        var lines = logs.Snapshot();

        window.Refresh();
        Dispatcher.UIThread.RunJobs();

        var visuals = window.GetVisualDescendants().ToList();
        var texts = visuals.OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        var checks = new (string Name, bool Passed)[]
        {
            ("fenêtre construite", window.IsVisible),
            ("arbre visuel peuplé", visuals.Count > 40),
            ("barres de vitaux", visuals.OfType<ProgressBar>().Count() >= 2),
            ("bouton pause/reprise", visuals.OfType<Button>().Any()),
            ("lignes de rotation", visuals.OfType<Border>().Count() >= options.Skills.Count),

            // Les suivantes prouvent que le MOTEUR tourne sous la GUI, pas seulement que
            // la fenêtre se dessine.
            ("moteur : vitaux reçus du serveur", state.MaxHp > 0 && state.MaxMp > 0),
            ("moteur : PV rendus dans l'UI", texts.Any(t => t.Contains(state.MaxHp.ToString()))),
            ("moteur : position suivie", texts.Any(t => t.Contains($"({state.Position.X}, {state.Position.Y})"))),
            ("moteur : journal alimenté", lines.Count > 20),
            ("moteur : paquets échangés", lines.Any(l => l.Message.Contains("[IN ]")) && lines.Any(l => l.Message.Contains("[OUT]"))),
            ("moteur : rotation active", lines.Any(l => l.Message.Contains("[OUT] u_s"))),
            ("journal rendu dans l'UI", texts.Any(t => t.Contains("[IN ]")))
        };

        var failed = 0;
        foreach (var (name, passed) in checks)
        {
            Console.WriteLine($"  [{(passed ? "OK  " : "ECHEC")}] {name}");
            if (!passed)
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{checks.Length - failed}/{checks.Length} verifications passees, {visuals.Count} controles dans l'arbre visuel.");

        return failed == 0 ? 0 : 3;
    }
}
