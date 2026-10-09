using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// What the launcher can start.
/// </summary>
public enum LaunchMode
{
    /// <summary>Reads the game and presses keys in it.</summary>
    Play,

    /// <summary>Reads the game and only shows what it would do.</summary>
    Observe,

    /// <summary>A fake server, no game needed.</summary>
    Simulate
}

/// <summary>
/// The first window of a double-clicked .exe: which NosTale, and what the bot may do with it.
/// </summary>
/// <remarks>
/// Everything here used to be a switch typed after <c>dotnet run</c> - <c>--pcap</c>, <c>--play</c>,
/// <c>--pid</c> - and a forgotten <c>--</c> separator was enough to start the simulator instead. The
/// launcher writes those same switches from what was picked, and starts the engine through the same
/// path the command line uses, so the two can never bind differently.
/// </remarks>
public sealed class LauncherWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA0A6"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1E1F22"));
    private static readonly IBrush Ready = new SolidColorBrush(Color.Parse("#5BD46B"));
    private static readonly IBrush Cooling = new SolidColorBrush(Color.Parse("#D4A65B"));
    private static readonly IBrush Blocked = new SolidColorBrush(Color.Parse("#D45B5B"));

    private readonly string[] _extraArgs;
    private readonly List<ClientChoice> _choices = new();

    private readonly ListBox _clients = new()
    {
        Name = "clients",
        Height = 150,
        FontSize = 12.5,
        Background = new SolidColorBrush(Color.Parse("#141517"))
    };

    private readonly TextBlock _scanStatus = new() { Name = "scanStatus", Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Name = "refresh", Content = "Actualiser la liste", Width = 170, Height = 30 };
    private readonly Button _flash = new() { Name = "flash", Content = "Faire clignoter sa fenêtre", Width = 220, Height = 30, IsEnabled = false };

    private readonly RadioButton _play = Mode("Jouer", "le bot lit le jeu et appuie sur les touches. Il démarre en pause : clique « Reprendre » quand ton perso est prêt.");
    private readonly RadioButton _observe = Mode("Observer", "le bot lit le jeu et montre ce qu'il ferait, sans rien envoyer. Pratique pour vérifier tes réglages.");
    private readonly RadioButton _simulate = Mode("Simulateur", "un faux serveur pour découvrir la fenêtre, sans jeu. Rien n'y est ton personnage.");

    private readonly Button _start = new() { Name = "start", Content = "Démarrer", Width = 160, Height = 38, FontSize = 15, FontWeight = FontWeight.SemiBold };

    private readonly SelectableTextBlock _status = new()
    {
        Name = "launchStatus",
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Muted
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="LauncherWindow"/> class.
    /// </summary>
    /// <param name="args">The switches the program was started with; kept for the engine.</param>
    public LauncherWindow(string[] args)
    {
        _extraArgs = args;

        Title = "NosSmoothCustomClient - démarrage";
        Width = 680;
        Height = 660;
        MinWidth = 520;
        MinHeight = 480;
        Background = new SolidColorBrush(Color.Parse("#141517"));

        _play.IsChecked = true;
        _play.IsCheckedChanged += (_, _) => RefreshChoices();
        _observe.IsCheckedChanged += (_, _) => RefreshChoices();
        _simulate.IsCheckedChanged += (_, _) => RefreshChoices();

        _clients.SelectionChanged += (_, _) => RefreshChoices();
        _refresh.Click += async (_, _) => await ScanAsync().ConfigureAwait(true);
        _flash.Click += (_, _) => Flash();
        _start.Click += async (_, _) => await StartAsync().ConfigureAwait(true);

        Content = BuildLayout();
        _ = ScanAsync();
    }

    /// <summary>Gets the mode currently picked.</summary>
    public LaunchMode SelectedMode
        => _simulate.IsChecked == true ? LaunchMode.Simulate
            : _observe.IsChecked == true ? LaunchMode.Observe
            : LaunchMode.Play;

    /// <summary>Gets the dashboard this launcher opened, once the engine has started.</summary>
    public MainWindow? Dashboard { get; private set; }

    /// <summary>Gets a task that completes when the current process scan has filled the list.</summary>
    public Task Scanned { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Writes the switches the engine is started with.
    /// </summary>
    /// <param name="mode">What the bot may do.</param>
    /// <param name="processId">The NosTale picked, or null to let the engine find the only one.</param>
    /// <param name="extra">Other switches the program was given, such as --verbose.</param>
    /// <returns>The switches.</returns>
    public static string[] BuildArgs(LaunchMode mode, int? processId, IEnumerable<string> extra)
    {
        var args = mode switch
        {
            LaunchMode.Play => new List<string> { "--pcap", "--play" },
            LaunchMode.Observe => new List<string> { "--pcap" },
            _ => new List<string> { "--simulate" }
        };

        if (mode != LaunchMode.Simulate && processId is { } pid)
        {
            args.Add("--pid");
            args.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.AddRange(extra);
        return args.ToArray();
    }

    /// <summary>
    /// Selects the mode, as a click on its button would.
    /// </summary>
    /// <param name="mode">The mode.</param>
    public void Select(LaunchMode mode)
    {
        _play.IsChecked = mode == LaunchMode.Play;
        _observe.IsChecked = mode == LaunchMode.Observe;
        _simulate.IsChecked = mode == LaunchMode.Simulate;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        DisposeChoices();
        base.OnClosed(e);
    }

    private Control BuildLayout()
    {
        var panel = new StackPanel { Spacing = 0, Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = "NosSmoothCustomClient",
            Foreground = Ink,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Choisis ton NosTale et ce que le bot a le droit de faire, puis Démarrer.",
            Foreground = Muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var clients = new StackPanel { Spacing = 8 };
        clients.Children.Add(_clients);

        var clientActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        clientActions.Children.Add(_refresh);
        clientActions.Children.Add(_flash);
        clients.Children.Add(clientActions);
        clients.Children.Add(_scanStatus);
        panel.Children.Add(Section("Ton NosTale", clients));

        var modes = new StackPanel { Spacing = 6 };
        modes.Children.Add(_play);
        modes.Children.Add(_observe);
        modes.Children.Add(_simulate);
        panel.Children.Add(Section("Mode", modes));

        var start = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        start.Children.Add(_start);
        start.Children.Add(new TextBlock
        {
            Text = "Il faut Npcap (npcap.com) installé,\net NosTale ouvert avec ton perso connecté.",
            Foreground = Muted,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(start);

        panel.Children.Add(new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Child = _status
        });

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    /// <summary>
    /// Lists the running NosTale clients, off the UI thread: reading every process takes a moment.
    /// </summary>
    private Task ScanAsync()
    {
        _refresh.IsEnabled = false;
        _scanStatus.Text = "recherche des NosTale ouverts…";
        _scanStatus.Foreground = Muted;

        Scanned = ScanCoreAsync();
        return Scanned;
    }

    private async Task ScanCoreAsync()
    {
        IReadOnlyList<ProcessVerdict> verdicts;

        try
        {
            verdicts = await Task.Run(NosTaleProcessScanner.Scan).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            verdicts = Array.Empty<ProcessVerdict>();
            _scanStatus.Text = "la liste des programmes est illisible : " + ex.Message;
            _scanStatus.Foreground = Blocked;
        }

        DisposeChoices();

        // The processes of the clients are kept for flashing their window; every other one is let
        // go at once.
        foreach (var verdict in verdicts.Where(v => !v.IsClient))
        {
            verdict.Process.Dispose();
        }

        var found = verdicts
            .Where(v => v.IsClient)
            .OrderBy(v => v.StartedAt ?? DateTime.MaxValue)
            .ToList();

        _choices.Add(new ClientChoice(null, null, "Automatique — le seul NosTale ouvert"));

        foreach (var verdict in found)
        {
            _choices.Add(new ClientChoice(verdict.Process.Id, verdict.Process, Describe(verdict)));
        }

        _clients.ItemsSource = null;
        _clients.ItemsSource = _choices.ToList();

        // One windowed client is an unambiguous answer: picking it saves a click, and shows which
        // one the engine is about to bind. Anything else is a choice left to the player.
        var windowed = found.Where(v => v.HasWindow).ToList();
        _clients.SelectedIndex = windowed.Count == 1 ? 1 + found.IndexOf(windowed[0]) : 0;

        if (verdicts.Count > 0)
        {
            (_scanStatus.Text, _scanStatus.Foreground) = found.Count switch
            {
                0 => ("Aucun NosTale trouvé. Ouvre le jeu, connecte ton perso, puis « Actualiser la liste ».", (IBrush)Blocked),
                1 => ("1 NosTale trouvé.", Ready),
                _ => ($"{found.Count} NosTale trouvés : choisis le tien. « Faire clignoter » montre lequel est lequel.", Cooling)
            };
        }

        _refresh.IsEnabled = true;
        RefreshChoices();
    }

    private static string Describe(ProcessVerdict verdict)
    {
        var started = verdict.StartedAt is { } at ? $"ouvert à {at:HH:mm}" : "heure inconnue";
        var window = verdict.HasWindow ? $"« {verdict.WindowTitle} »" : "(sans fenêtre)";
        var where = verdict.Bounds is { Length: > 0 } bounds ? $"   {bounds}" : string.Empty;

        return $"{verdict.Process.ProcessName}   n° {verdict.Process.Id}   {started}   {window}{where}";
    }

    private void RefreshChoices()
    {
        var needsGame = SelectedMode != LaunchMode.Simulate;
        var picked = _clients.SelectedItem as ClientChoice;

        _clients.IsEnabled = needsGame;
        _flash.IsEnabled = needsGame && picked?.Process is not null && OperatingSystem.IsWindows();
    }

    private void Flash()
    {
        if (_clients.SelectedItem is not ClientChoice { Process: { } process })
        {
            return;
        }

        var flashed = NosTaleProcessScanner.Flash(process);
        _scanStatus.Text = flashed
            ? $"n° {process.Id} : sa fenêtre clignote dans la barre des tâches."
            : $"n° {process.Id} : sa fenêtre n'a pas pu clignoter (pas de fenêtre visible ?).";
        _scanStatus.Foreground = flashed ? Ready : Blocked;
    }

    private async Task StartAsync()
    {
        var mode = SelectedMode;
        var pid = mode == LaunchMode.Simulate ? null : (_clients.SelectedItem as ClientChoice)?.ProcessId;
        var args = BuildArgs(mode, pid, _extraArgs);

        _start.IsEnabled = false;
        _status.Text = mode == LaunchMode.Simulate
            ? "démarrage du simulateur…"
            : pid is { } chosen ? $"liaison au NosTale n° {chosen}…" : "liaison au NosTale ouvert…";
        _status.Foreground = Cooling;

        EngineStart started;

        try
        {
            started = await Task.Run(() => Engine.StartAsync(args)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            started = new EngineStart(null, CommandLine.Parse(args), ex.Message, 1);
        }

        if (started.Host is not { } host)
        {
            // Stays open on a refusal: picking another client or another mode is the next step,
            // and it is taken from here, not from a new launch.
            _status.Text = mode == LaunchMode.Simulate
                ? "Le simulateur n'a pas pu démarrer.\n\nDétail : " + started.Error
                : "Le bot n'a pas pu se lier à NosTale. Vérifie que le jeu est ouvert, perso connecté, "
                  + "choisis-le dans la liste (« Actualiser la liste » s'il n'y est pas), puis Démarrer."
                  + "\n\nDétail : " + started.Error;
            _status.Foreground = Blocked;
            _start.IsEnabled = true;
            return;
        }

        App.Engine = host;
        App.Services = host.Services;
        App.Mode = started.Cli.Mode;

        Dashboard = App.CreateMainWindow(host.Services, started.Cli.Mode);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Dashboard;
        }

        Dashboard.Show();
        Close();
    }

    private void DisposeChoices()
    {
        foreach (var choice in _choices)
        {
            choice.Process?.Dispose();
        }

        _choices.Clear();
    }

    private static RadioButton Mode(string name, string detail)
        => new()
        {
            GroupName = "mode",
            Content = new TextBlock
            {
                Inlines = new Avalonia.Controls.Documents.InlineCollection
                {
                    new Avalonia.Controls.Documents.Run(name) { FontWeight = FontWeight.SemiBold, Foreground = Ink },
                    new Avalonia.Controls.Documents.Run(" — " + detail) { Foreground = Muted }
                },
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap
            }
        };

    private static Control Section(string heading, Control content)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = heading.ToUpperInvariant(),
            Foreground = Muted,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(content);

        return new Border
        {
            Child = panel,
            Background = Panel,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12)
        };
    }

    /// <summary>One line of the client list.</summary>
    /// <param name="ProcessId">The process to bind, or null for automatic.</param>
    /// <param name="Process">The process, kept to flash its window.</param>
    /// <param name="Text">What the line reads.</param>
    private sealed record ClientChoice(int? ProcessId, System.Diagnostics.Process? Process, string Text)
    {
        public override string ToString() => Text;
    }
}
