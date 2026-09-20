using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NosSmoothCustomClient.Configuration;
using System.IO;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.Input;
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
    private static IServiceProvider? _services;

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
        _services = services;
        var state = services.GetRequiredService<ProtocolStateManager>();
        var options = services.GetRequiredService<BotOptions>();
        var logs = services.GetRequiredService<LogBuffer>();
        var lines = logs.Snapshot();

        window.Refresh();
        Dispatcher.UIThread.RunJobs();

        // A tab only builds its contents when it is shown, so the window has to be walked tab by
        // tab. That is worth doing rather than working around: every tab being asked to render is
        // the check, not an obstacle to it.
        var visuals = Realise(window);
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

        // A ↻ must actually clear the engine's cooldown, not merely redraw the row. The loop is
        // paused for the duration: it casts on its own, so leaving it running would let it re-arm
        // the cooldown between the click and the assertion and report noise.
        var controller = services.GetRequiredService<BotController>();
        var rotation = services.GetRequiredService<SkillRotation>();
        var buffTracker = services.GetRequiredService<BuffTracker>();
        var resetButtons = visuals.OfType<Button>()
            .Where(b => b.Content as string == "\u21BB")
            .ToList();

        var skillResetWorks = false;
        var buffResetWorks = false;

        if (resetButtons.Count >= options.Skills.Count + options.Buffs.Count
            && options.Skills.Count > 0
            && options.Buffs.Count > 0)
        {
            var wasRunning = controller.IsRunning;
            controller.Pause();

            rotation.MarkCast(options.Skills[0]);
            var skillWasCooling = rotation.Snapshot(long.MaxValue)[0].Remaining > TimeSpan.Zero;
            Click(resetButtons[0]);
            skillResetWorks = skillWasCooling && rotation.Snapshot(long.MaxValue)[0].Remaining == TimeSpan.Zero;

            buffTracker.MarkCast(options.Buffs[0]);
            var buffWasUp = buffTracker.Snapshot()[0].IsActive;
            Click(resetButtons[options.Skills.Count]);
            buffResetWorks = buffWasUp && !buffTracker.Snapshot()[0].IsActive;

            if (wasRunning)
            {
                controller.Start();
            }
        }

        // Cocher le mode instance doit atteindre les options en direct, comme toute autre case.
        var instanceToggles = false;
        var instanceBox = boxes.FirstOrDefault(b => (b.Content as string)?.Contains("instance") == true);

        if (instanceBox is not null)
        {
            var before = options.InstanceMode;
            instanceBox.IsChecked = !before;
            Dispatcher.UIThread.RunJobs();
            instanceToggles = options.InstanceMode != before;
            instanceBox.IsChecked = before;
            Dispatcher.UIThread.RunJobs();
        }

        var clearWorks = false;
        var clearButton = visuals.OfType<Button>().FirstOrDefault(b => b.Content as string == "Effacer");

        if (clearButton is not null)
        {
            var before = options.Waypoints.ToList();
            options.Waypoints = new List<Waypoint> { new(10, 10, 100, 100), new(20, 20, 200, 200) };

            Click(clearButton);

            // Re-walked, because the list lives on a tab and a tab that is not shown has nothing
            // to read.
            clearWorks = options.Waypoints.Count == 0
                         && Realise(window).OfType<TextBlock>()
                             .Any(t => (t.Text ?? string.Empty).Contains("aucun point"));

            options.Waypoints = before;
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
            ("↻ purge le cooldown d'un sort", skillResetWorks),
            ("↻ fait retomber un buff", buffResetWorks),
            ("enregistrement des réglages", saveWorks),

            // Le panneau touches et la route.
            ("champs de touches", textBoxes.Count >= 4),
            ("modifier une touche change les options", keyEditWorks),
            ("section route rendue", texts.Any(t => t.Contains("aucun point") || t.Contains("carte ("))),
            ("boutons d'action présents", visuals.OfType<Button>().Count() >= 4),

            // Le panneau qui dit pourquoi le bot n'agit pas. Il doit nommer la cause, pas seulement
            // exister : une ligne rouge sans raison ne vaut pas mieux que le silence.
            ("diagnostic rendu", texts.Any(t => t.Contains("Cibler et attaquer"))),

            // Les deux métiers sont séparés : chercher un réglage de salle au milieu des réglages
            // de farm, c'est devoir lire les deux pour savoir lequel s'applique.
            ("onglets separes", TabsAreSeparate(window)),

            // Enregistrer s'applique à tous les onglets, donc il ne peut pas vivre au fond de l'un
            // d'eux : un réglage changé dans « Espace-temps » se sauve depuis « Espace-temps ».
            ("enregistrer est toujours accessible", SaveStaysOnTop(window)),

            // Et ce que la boucle vient de décider reste visible quel que soit l'onglet ouvert.
            ("la decision reste visible", DecisionStaysOnTop(window)),

            // Un échec de liaison doit se lire à l'écran : sortir avant que la fenêtre existe ne
            // laisse rien pour l'afficher, et c'est exactement ce qui se lit comme « ça ne marche
            // plus » sans explication.
            ("un echec de liaison s'affiche", StartupErrorShows()),

            // L'en-tête appelait la capture « simulateur » elle aussi, donc un bot qui lisait le
            // vrai jeu annonçait à son opérateur que rien n'était réel.
            ("le mode est nomme correctement", ModeIsNamedCorrectly()),

            // Le panneau d'enregistrement de run, et le format des lignes qu'il affichera.
            ("section run rendue", texts.Any(t => t.Contains("Joue la séquence à la main"))),
            ("une ligne de run se lit", RunEventReadsBack()),

            // Le mode instance doit être réglable depuis la fenêtre, sinon il faut éditer le JSON
            // pour désigner le portail - ce qui est exactement ce qu'on cherche à éviter.
            ("mode instance reglable", texts.Any(t => t.Contains("Waypoint de sortie")) && instanceToggles),

            // Les clics de récompense n'ont aucune trace dans les paquets : s'ils ne sont pas
            // listés dans la fenêtre, rien ne dit qu'ils existent ni ce qu'ils vont faire.
            ("clics de recompense listes", texts.Any(t => t.Contains("F7")) && RewardSequenceReadsBack()),

            // Effacer doit vider la route que le bot utilise vraiment, pas seulement un tampon
            // invisible : sinon le bouton ne se distingue pas d'un bouton mort.
            ("effacer vide la route en cours", clearWorks),
            ("diagnostic explique les blocages", BotReadiness.Describe(options, state, null)
                .Where(i => !i.Ready)
                .All(i => !string.IsNullOrWhiteSpace(i.Detail) && texts.Contains(i.Detail)))
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

    private static bool RewardSequenceReadsBack()
    {
        var options = new BotOptions
        {
            RewardSequence = new List<UiPoint>
            {
                new("tirage", 467, 460, DoubleClick: true),
                new("confirmer", 509, 571)
            }
        };

        // It has to survive the round trip, or a sequence recorded once is gone at the next launch.
        var (path, _) = LocalConfigurationWriter.Save(options, Path.GetTempPath());
        if (path is null)
        {
            return false;
        }

        var json = File.ReadAllText(path);
        File.Delete(path);

        return json.Contains("\"tirage\"") && json.Contains("\"DoubleClick\": true")
               && options.RewardSequence[0].ToString().Contains("double-clic");
    }

    private static bool RunEventReadsBack()
    {
        // A recorded action has to survive the round trip to the file and still say where it was
        // clicked and what the world looked like - that second half is the whole point of recording
        // it rather than filming it.
        var entry = new RunEvent(1500, RunEventKind.Click, "clic gauche", 842, 511, 12, 107, 202, 4242, 7);

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var back = System.Text.Json.JsonSerializer.Deserialize<RunEvent>(json);

        return back == entry
               && entry.Summary.Contains("842,511")
               && entry.Summary.Contains("carte 12")
               && entry.Summary.Contains("mobs=7");
    }

    private static bool ModeIsNamedCorrectly()
    {
        // Shown before reading: an unrealised window has no visual tree, so the assertion would
        // pass against an empty list rather than against what the operator sees.
        var window = App.CreateMainWindow(_services!, RunMode.Pcap);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .ToList();

        window.Close();

        // Capture must never be described as the simulator, and the simulator must say plainly
        // that nothing on screen belongs to the player.
        return texts.Any(t => t.Contains("capture")) && !texts.Any(t => t.Contains("MODE SIMULATEUR"));
    }

    private static bool StartupErrorShows()
    {
        const string message = "Aucun client NosTale trouvé.";
        var window = new StartupErrorWindow(message);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shown = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Any(t => (t.Text ?? string.Empty).Contains(message));

        // And the way out has to be there too, or the message is a dead end.
        var hasHelp = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(t => (t.Text ?? string.Empty).Contains("--identify"));

        window.Close();
        return shown && hasHelp;
    }

    /// <summary>
    /// Shows every tab in turn and collects everything the window can draw.
    /// </summary>
    private static bool SaveStaysOnTop(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is null)
        {
            return false;
        }

        for (var index = 0; index < tabs.ItemCount; index++)
        {
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();

            if (!window.GetVisualDescendants().OfType<Button>()
                    .Any(b => (b.Content as string)?.Contains("Enregistrer les réglages") == true))
            {
                return false;
            }
        }

        tabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    private static bool TabsAreSeparate(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is null)
        {
            return false;
        }

        var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header as string ?? string.Empty).ToList();
        return headers.Contains("Farm") && headers.Contains("Espace-temps") && headers.Contains("Combat");
    }

    private static bool DecisionStaysOnTop(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is null)
        {
            return false;
        }

        // On every tab, not just the one that happens to be open: a line that disappears when you
        // go looking at settings is a line you cannot use while changing them.
        for (var index = 0; index < tabs.ItemCount; index++)
        {
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();

            if (!window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => (t.Text ?? string.Empty).Contains("EN TRAIN DE")))
            {
                return false;
            }
        }

        tabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    private static List<Visual> Realise(Window window)
    {
        var all = new List<Visual>();
        var seen = new HashSet<Visual>();

        void Collect()
        {
            Dispatcher.UIThread.RunJobs();

            foreach (var visual in window.GetVisualDescendants())
            {
                if (seen.Add(visual))
                {
                    all.Add(visual);
                }
            }
        }

        Collect();

        var tabs = all.OfType<TabControl>().FirstOrDefault();
        if (tabs is null)
        {
            return all;
        }

        for (var index = 0; index < tabs.ItemCount; index++)
        {
            tabs.SelectedIndex = index;
            Collect();
        }

        tabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        return all;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
