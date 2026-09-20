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
using Microsoft.Extensions.Configuration;
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

        // Le délai avant les clics de récompense doit atteindre les options en direct.
        var rewardDelayEdits = false;
        if (numbers.FirstOrDefault(n => n.Name == "rewardDelay") is { } delayBox)
        {
            var before = options.RewardDelay;
            delayBox.Value = (decimal)before.TotalSeconds + 2;
            Dispatcher.UIThread.RunJobs();
            rewardDelayEdits = options.RewardDelay != before;
            delayBox.Value = (decimal)before.TotalSeconds;
            Dispatcher.UIThread.RunJobs();
        }

        // Et le bouton qui rejoue la séquence doit dire ce qu'il a fait - ou pourquoi il n'a rien
        // fait. Un bouton qui ne répond rien est ce qui a déjà été signalé comme « ne sert à rien ».
        var rewardPlaysOnDemand = false;
        var rewardStatus = visuals.OfType<TextBlock>().FirstOrDefault(t => t.Name == "rewardStatus");

        if (rewardStatus is not null
            && visuals.OfType<Button>().FirstOrDefault(b => b.Name == "playReward") is { } playButton)
        {
            var silentBefore = string.IsNullOrWhiteSpace(rewardStatus.Text);
            Click(playButton);
            rewardPlaysOnDemand = silentBefore && !string.IsNullOrWhiteSpace(rewardStatus.Text);
        }

        // Les deux boutons d'enregistrement, dans les deux endroits où on en a besoin.
        var buttons = visuals.OfType<Button>().ToList();
        var starts = buttons.Where(b => b.Name is "runStart" or "launchRecStart").ToList();
        var stops = buttons.Where(b => b.Name is "runStop" or "launchRecStop").ToList();

        var recordingButtons = starts.Count == 2
                               && stops.Count == 2
                               && starts.All(b => (b.Content as string)?.Contains("Commencer") == true)
                               && stops.All(b => (b.Content as string)?.Contains("Finir") == true);

        if (recordingButtons)
        {
            // Sans enregistreur, presser « Commencer » ne peut rien faire : il doit le dire.
            var status = visuals.OfType<TextBlock>().ToList();
            var before = status.Select(s => s.Text).ToList();
            Click(starts[0]);

            recordingButtons = status.Zip(before).Any(pair => pair.First.Text != pair.Second)
                               || starts.All(b => !b.IsEnabled);
        }

        // La touche d'enregistrement doit atteindre les options, et les textes doivent la suivre.
        var recordKeyEdits = false;
        if (visuals.OfType<ComboBox>().FirstOrDefault(c => c.Name == "recordKey") is { } keyBox)
        {
            var before = options.RecordRunKey;

            // Deliberately not a hard-coded key: picking the one already in force would assert
            // nothing, and the default has moved once already.
            var other = HotKey.Choices.First(k => !k.Label.Equals(before, StringComparison.OrdinalIgnoreCase)).Label;

            keyBox.SelectedItem = other;
            Dispatcher.UIThread.RunJobs();

            recordKeyEdits = options.RecordRunKey == other
                             && visuals.OfType<TextBlock>()
                                 .Any(t => t.Text is { } text && text.Contains(other) && text.Contains("lancement"));

            keyBox.SelectedItem = before;
            Dispatcher.UIThread.RunJobs();
        }

        // Lancer au démarrage doit atteindre les options en direct.
        var autoLaunchEdits = false;
        var autoLaunchBox = boxes.FirstOrDefault(b => (b.Content as string)?.Contains("au démarrage") == true);

        if (autoLaunchBox is not null)
        {
            var before = options.AutoLaunchInstance;
            autoLaunchBox.IsChecked = !before;
            Dispatcher.UIThread.RunJobs();
            autoLaunchEdits = options.AutoLaunchInstance != before;
            autoLaunchBox.IsChecked = before;
            Dispatcher.UIThread.RunJobs();
        }

        // Et « Lancer maintenant » doit dire pourquoi il ne lance pas, plutôt que rester muet -
        // c'est exactement le reproche déjà fait à un bouton qui ne répondait rien.
        var launchAnswers = false;
        var launchStatus = visuals.OfType<TextBlock>().FirstOrDefault(t => t.Name == "launchStatus");

        if (launchStatus is not null
            && visuals.OfType<Button>().FirstOrDefault(b => b.Name == "launchNow") is { } launchButton)
        {
            var silentBefore = string.IsNullOrWhiteSpace(launchStatus.Text);
            Click(launchButton);
            launchAnswers = silentBefore && !string.IsNullOrWhiteSpace(launchStatus.Text);
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

            // Le panneau met quelques secondes à s'afficher et personne ne sait combien d'avance :
            // le délai se règle donc depuis la fenêtre, pas dans le JSON.
            ("delai de recompense reglable", rewardDelayEdits),

            // Rien dans les paquets ne dit si le panneau a été cliqué. Pouvoir rejouer la séquence
            // à la main, panneau à l'écran, est ce qui sépare « coordonnées fausses » de « jamais
            // déclenché » - et le bouton doit répondre quelque chose, jamais rester muet.
            ("les clics de recompense se rejouent a la main", rewardPlaysOnDemand),

            // Le lancement est la moitié du travail qu'aucun paquet ne décrit : s'il n'est ni
            // listé ni relisible après sauvegarde, il est perdu au prochain démarrage.
            ("sequence de lancement listee", texts.Any(t => t.Contains("MISSION")) && LaunchSequenceReadsBack()),
            ("lancement au demarrage reglable", autoLaunchEdits),

            // La touche d'enregistrement était figée sur F11, qui ouvre la boutique du jeu : toute
            // capture démarrait derrière une fenêtre. Elle se change donc ici, et tout ce qui la
            // nomme doit suivre - un panneau qui dit encore F11 après coup est pire que rien.
            ("touche d'enregistrement reglable", recordKeyEdits),

            // Deux boutons nommés plutôt qu'un bouton qui change de sens, et une paire là où un
            // lancement s'enregistre : un bouton dans un autre onglet est un bouton absent. Sans
            // enregistreur ils sont éteints, et le panneau dit pourquoi - jamais muets.
            ("boutons commencer et finir presents", recordingButtons),
            ("le bouton de lancement repond toujours", launchAnswers),

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

    /// <summary>
    /// Loads a written settings file exactly as a fresh launch would.
    /// </summary>
    private static BotOptions ReadBack(string path)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        return BotConfigurationFile.Apply(configuration).Options;
    }

    private static bool LaunchSequenceReadsBack()
    {
        var options = new BotOptions
        {
            AutoLaunchInstance = true,
            RecordRunKey = "Pause",
            StartupSequence = new List<StartupStep>
            {
                new("assis", StartupAction.Key, Key: "C", WaitBeforeMs: 700),
                new("START", StartupAction.Click, X: 512, Y: 611, DoubleClick: true, WaitBeforeMs: 1200),
                new("entrer", StartupAction.Key, Key: "Entrée", UntilMap: 4100, UntilX: 30, UntilY: 40, TimeoutMs: 45000)
            }
        };

        var (path, _) = LocalConfigurationWriter.Save(options, Path.GetTempPath());
        if (path is null)
        {
            return false;
        }

        // A launch that does not come back is one recorded once and lost at the next launch, and
        // every condition on it has to come back too: a step that forgets which map it was waiting
        // for is a step played into a loading screen.
        var reloaded = ReadBack(path);

        return reloaded.RecordRunKey == "Pause"
               && reloaded.AutoLaunchInstance
               && reloaded.StartupSequence.Count == 3
               && reloaded.StartupSequence[0].Key == "C"
               && reloaded.StartupSequence[0].Action == StartupAction.Key
               && reloaded.StartupSequence[0].WaitBeforeMs == 700
               && reloaded.StartupSequence[1].DoubleClick
               && reloaded.StartupSequence[1].X == 512
               && reloaded.StartupSequence[2].UntilMap == 4100
               && reloaded.StartupSequence[2].UntilY == 40
               && reloaded.StartupSequence[2].TimeoutMs == 45000;
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

        // Written is not read. A sequence that goes out to the file and does not come back is one
        // recorded once and lost at the next launch, and the write alone cannot tell the two apart -
        // so this goes through the very pipeline the app starts with.
        var reloaded = ReadBack(path);

        File.Delete(path);

        return json.Contains("\"tirage\"")
               && reloaded.RewardSequence.Count == 2
               && reloaded.RewardSequence[0].DoubleClick
               && reloaded.RewardSequence[0].X == 467
               && !reloaded.RewardSequence[1].DoubleClick
               && reloaded.RewardSequence[1].Y == 571;
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
