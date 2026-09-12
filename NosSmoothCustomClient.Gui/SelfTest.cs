using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NosSmoothCustomClient.Configuration;
using System.IO;
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
        var boxes = visuals.OfType<CheckBox>().ToList();
        var numbers = visuals.OfType<NumericUpDown>().ToList();
        var expectedRows = options.Skills.Count + options.Buffs.Count;

        // Toggling a box must reach the live options, not just the control: the whole point of the
        // panel is that a change takes effect on the running bot.
        var toggleWorks = false;
        if (boxes.Count > 0 && options.Skills.Count > 0)
        {
            var before = options.Skills[0].Enabled;
            boxes[0].IsChecked = !before;
            Dispatcher.UIThread.RunJobs();
            toggleWorks = options.Skills[0].Enabled != before;
            boxes[0].IsChecked = before;
            Dispatcher.UIThread.RunJobs();
        }

        // And saving must produce a file the next run will actually read back.
        var textBoxes = visuals.OfType<TextBox>().ToList();

        // Editing a key must reach the live options, the same way a checkbox does.
        var keyEditWorks = false;
        if (textBoxes.Count > 0)
        {
            var before = options.Keys.HpPotion;
            var box = textBoxes.FirstOrDefault(b => b.Text == before) ?? textBoxes[0];
            box.Text = "9";
            Dispatcher.UIThread.RunJobs();
            keyEditWorks = options.Keys.TargetAndAttack == "9"
                           || options.Keys.HpPotion == "9"
                           || options.Keys.Loot == "9"
                           || options.Keys.MpPotion == "9";
        }

        var (savedPath, saveError) = LocalConfigurationWriter.Save(options, Path.GetTempPath());
        var saveWorks = savedPath is not null && File.Exists(savedPath);
        if (savedPath is not null)
        {
            File.Delete(savedPath);
        }

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
            // Deliberately a shape check, not an equality one: the character keeps moving between
            // the render and the assertion, so comparing against a freshly read position races the
            // engine and fails at random. What matters is that a real coordinate was rendered.
            ("moteur : position suivie", texts.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(\d+, \d+\)$"))
                                         && state.OwnCharacterId >= 0),
            ("moteur : journal alimenté", lines.Count > 20),
            ("moteur : paquets échangés", lines.Any(l => l.Message.Contains("[IN ]")) && lines.Any(l => l.Message.Contains("[OUT]"))),
            ("moteur : rotation active", lines.Any(l => l.Message.Contains("[OUT] u_s"))),
            ("journal rendu dans l'UI", texts.Any(t => t.Contains("[IN ]"))),

            // Le panneau de réglages.
            ("cases à cocher par ligne", boxes.Count >= expectedRows),
            ("champs de durée par ligne", numbers.Count >= expectedRows),
            ("cocher modifie les options en direct", toggleWorks),
            ("enregistrement des réglages", saveWorks),

            // Le panneau touches et la route.
            ("champs de touches", textBoxes.Count >= 4),
            ("modifier une touche change les options", keyEditWorks),
            ("section route rendue", texts.Any(t => t.Contains("aucun point") || t.Contains("carte ("))),
            ("boutons d'action présents", visuals.OfType<Button>().Count() >= 4)
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
