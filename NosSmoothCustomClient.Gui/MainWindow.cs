using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.Orchestration;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// The control panel: live vitals, target, rotation state and the log stream.
/// </summary>
/// <remarks>
/// Built in code rather than XAML on purpose. The whole window is then verified by the compiler and
/// can be constructed under the headless platform in a self-test, which matters because this window
/// is authored on a machine that has no display to check it against.
///
/// It never mutates engine state directly: it reads snapshots on a timer and writes only through
/// <see cref="BotController"/>. That keeps the UI thread off the packet threads entirely.
/// </remarks>
public sealed class MainWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA0A6"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1E1F22"));
    private static readonly IBrush Ready = new SolidColorBrush(Color.Parse("#5BD46B"));
    private static readonly IBrush Cooling = new SolidColorBrush(Color.Parse("#D4A65B"));
    private static readonly IBrush Blocked = new SolidColorBrush(Color.Parse("#D45B5B"));

    private readonly ProtocolStateManager _state;
    private readonly SkillRotation _rotation;
    private readonly BuffTracker _buffs;
    private readonly BotController _controller;
    private readonly BotOptions _options;
    private readonly LogBuffer _logs;
    private readonly RunMode _mode;

    private readonly TextBlock _status = Label("", 13, FontWeight.SemiBold);
    private readonly Button _toggle = new() { Width = 130, Height = 32 };
    private readonly ProgressBar _hpBar = Bar("#D45B5B");
    private readonly ProgressBar _mpBar = Bar("#5B8FD4");
    private readonly TextBlock _hpText = Mono();
    private readonly TextBlock _mpText = Mono();
    private readonly TextBlock _position = Mono();
    private readonly TextBlock _waypoint = Mono();
    private readonly TextBlock _target = Mono();
    private readonly TextBlock _entities = Mono();
    private readonly TextBlock _map = Mono();
    private readonly TextBlock _decision = new()
    {
        Foreground = Muted,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly StackPanel _skills = new() { Spacing = 4 };
    private readonly StackPanel _buffPanel = new() { Spacing = 4 };
    private readonly Button _save = new() { Content = "Enregistrer les réglages", Height = 30 };
    private readonly Button _resetSkills = new() { Content = "Remettre tous les sorts à zéro", Height = 30 };
    private readonly Button _resetBuffs = new() { Content = "Remettre tous les buffs à zéro", Height = 30 };
    private readonly TextBlock _saveStatus = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<SkillRow> _skillRows = new();
    private readonly List<BuffRow> _buffRows = new();

    private readonly SwitchableGameInput? _input;
    private readonly WaypointRecorder? _recorder;
    private readonly OrchestrationBackgroundService? _loop;
    private readonly RunRecorder? _runs;

    private readonly Button _live = new() { Width = 150, Height = 32 };
    private readonly Button _arm = new() { Width = 190, Height = 28 };
    private readonly Button _clearRoute = new() { Content = "Effacer", Width = 90, Height = 28 };
    private readonly Button _testClick = new() { Content = "Tester le clic (point 1)", Width = 190, Height = 28 };
    private readonly Button _probeClick = new() { Content = "Sonder les fenêtres", Width = 170, Height = 28 };
    private readonly Button _useProbed = new() { Content = "Garder celle-ci", Width = 140, Height = 28, IsEnabled = false };
    private int _probeIndex;
    private (IntPtr Handle, string ClassName)? _lastProbed;

    private readonly Button _adoptMap = new() { Width = 260, Height = 28 };
    private readonly Button _clearReward = new() { Content = "Effacer les clics", Width = 150, Height = 28 };
    private readonly Button _playReward = new() { Name = "playReward", Content = "Jouer les clics maintenant", Width = 220, Height = 28 };
    private readonly TextBlock _rewardStatus = new() { Name = "rewardStatus", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _rewardList = new() { Spacing = 2 };

    private readonly Button _buildLaunch = new() { Name = "buildLaunch", Content = "Faire de ce run la séquence", Width = 230, Height = 28 };
    private readonly Button _launchNow = new() { Name = "launchNow", Content = "Lancer maintenant", Width = 170, Height = 28 };
    private readonly Button _clearLaunch = new() { Content = "Effacer", Width = 90, Height = 28 };
    private readonly TextBlock _launchStatus = new() { Name = "launchStatus", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _launchList = new() { Spacing = 2 };
    private readonly TextBlock _launchHowTo = new()
    {
        Foreground = Ink,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _keyWatch = new()
    {
        Name = "keyWatch",
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly ComboBox _recordKey = new()
    {
        Name = "recordKey",
        Width = 150,
        Height = 32,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly CheckBox _autoLaunch = new()
    {
        Content = "Lancer l'espace-temps au démarrage du bot",
        Foreground = Ink,
        FontSize = 12.5,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly NumericUpDown _rewardDelay = new()
    {
        Name = "rewardDelay",
        Minimum = 0,
        Maximum = 30,
        Increment = 0.5m,
        FormatString = "0.#",
        Width = 90,
        Height = 34,
        FontSize = 15,
        Padding = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private readonly CheckBox _instanceMode = new()
    {
        Content = "Mode instance (espace-temps)",
        Foreground = Ink,
        FontSize = 12.5,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly NumericUpDown _exitWaypoint = new()
    {
        Minimum = 1,
        Maximum = 99,
        Increment = 1,
        Width = 90,
        Height = 34,
        FontSize = 15,
        Padding = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private readonly NumericUpDown _repositionAfter = new()
    {
        Minimum = 0.5m,
        Maximum = 30,
        Increment = 0.5m,
        FormatString = "0.#",
        Width = 90,
        Height = 34,
        FontSize = 15,
        Padding = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private readonly NumericUpDown _arrivalRadius = new()
    {
        Minimum = 1,
        Maximum = 40,
        Increment = 1,
        Width = 90,
        Height = 34,
        FontSize = 15,
        Padding = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };
    private readonly Button _saveRoute = new() { Content = "Enregistrer la route", Width = 170, Height = 28 };
    private readonly StackPanel _routeList = new() { Spacing = 3 };
    private readonly StackPanel _readiness = new() { Spacing = 3 };
    private readonly Button _runStart = new() { Name = "runStart", Content = "Commencer l'enregistrement", Width = 230, Height = 30 };
    private readonly Button _runStop = new() { Name = "runStop", Content = "Finir l'enregistrement", Width = 200, Height = 30, IsEnabled = false };
    private readonly Button _launchRecStart = new() { Name = "launchRecStart", Content = "Commencer l'enregistrement", Width = 230, Height = 30 };
    private readonly Button _launchRecStop = new() { Name = "launchRecStop", Content = "Finir l'enregistrement", Width = 200, Height = 30, IsEnabled = false };
    private readonly Button _runSave = new() { Content = "Enregistrer le fichier", Width = 180, Height = 28 };
    private readonly Button _runClear = new() { Content = "Effacer", Width = 90, Height = 28 };
    private readonly TextBlock _runStatus = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _runList = new() { Spacing = 2 };
    private readonly ScrollViewer _runScroll;
    private readonly TextBlock _routeStatus = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _keyAttack = KeyBox();
    private readonly TextBox _keyLoot = KeyBox();
    private readonly TextBox _keyHp = KeyBox();
    private readonly TextBox _keyMp = KeyBox();
    private readonly NumericUpDown _pressDelay = new()
    {
        Minimum = 0,
        Maximum = 2000,
        Increment = 10,
        Width = 132,
        Height = 34,
        FontSize = 15,
        Padding = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };
    private readonly SelectableTextBlock _log = new()
    {
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
        FontSize = 11,
        Foreground = Ink,
        TextWrapping = TextWrapping.NoWrap
    };

    private readonly ScrollViewer _logScroll;
    private DispatcherTimer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="rotation">The attack rotation.</param>
    /// <param name="buffs">The buff tracker.</param>
    /// <param name="controller">The run/pause switch.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logs">The log buffer.</param>
    /// <param name="mode">The transport mode, shown in the header.</param>
    /// <param name="input">The switchable input, when the transport has one.</param>
    /// <param name="recorder">The waypoint recorder, when the transport has one.</param>
    public MainWindow
    (
        ProtocolStateManager state,
        SkillRotation rotation,
        BuffTracker buffs,
        BotController controller,
        BotOptions options,
        LogBuffer logs,
        RunMode mode,
        SwitchableGameInput? input = null,
        WaypointRecorder? recorder = null,
        OrchestrationBackgroundService? loop = null,
        RunRecorder? runs = null
    )
    {
        _input = input;
        _recorder = recorder;
        _loop = loop;
        _runs = runs;
        _state = state;
        _rotation = rotation;
        _buffs = buffs;
        _controller = controller;
        _options = options;
        _logs = logs;
        _mode = mode;

        Title = "NosSmoothCustomClient";
        Width = 920;
        Height = 800;
        MinWidth = 640;
        MinHeight = 460;
        Background = new SolidColorBrush(Color.Parse("#141517"));

        _runScroll = new ScrollViewer
        {
            Content = _runList,
            Height = 150,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        _logScroll = new ScrollViewer
        {
            Content = _log,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        _toggle.Click += (_, _) =>
        {
            _controller.Toggle();
            Refresh();
        };

        _logScroll.Height = 190;

        _save.Click += (_, _) => SaveSettings();
        _resetSkills.Click += (_, _) => { _rotation.ResetAll(); Refresh(); };
        _resetBuffs.Click += (_, _) => { _buffs.Reset(); Refresh(); };
        _live.Click += (_, _) => ToggleLive();
        _arm.Click += (_, _) => ToggleArm();
        _clearRoute.Click += (_, _) => ClearRoute();
        _testClick.Click += (_, _) => TestClick();
        _probeClick.Click += (_, _) => ProbeClick();
        _runStart.Click += (_, _) => StartRecording();
        _runStop.Click += (_, _) => StopRecording();
        _launchRecStart.Click += (_, _) => StartRecording();
        _launchRecStop.Click += (_, _) => StopRecording();
        _runClear.Click += (_, _) => { _runs?.Clear(); RefreshRun(); };
        _runSave.Click += (_, _) => SaveRun();

        if (_runs is not null)
        {
            _runs.Changed += () => Dispatcher.UIThread.Post(RefreshRun);
        }
        _useProbed.Click += (_, _) => UseProbedWindow();

        _adoptMap.Click += (_, _) => AdoptCurrentMap();
        _clearReward.Click += (_, _) => { _recorder?.ClearUiPoints(); RefreshRoute(); };
        _playReward.Click += (_, _) => PlayRewardNow();
        _buildLaunch.Click += (_, _) => BuildLaunchFromRun();
        _launchNow.Click += (_, _) => LaunchNow();
        _clearLaunch.Click += (_, _) => { _options.StartupSequence.Clear(); MarkDirty(); RefreshLaunch(); };

        foreach (var choice in HotKey.Choices)
        {
            _recordKey.Items.Add(choice.Label);
        }

        _recordKey.SelectedItem = HotKey.Resolve(_options.RecordRunKey).Label;
        _recordKey.SelectionChanged += (_, _) =>
        {
            if (_recordKey.SelectedItem is string label && HotKey.TryParse(label, out var picked))
            {
                _options.RecordRunKey = picked.Label;
                MarkDirty();
                RefreshRun();
                RefreshLaunch();
            }
        };

        _autoLaunch.IsChecked = _options.AutoLaunchInstance;
        _autoLaunch.IsCheckedChanged += (_, _) =>
        {
            _options.AutoLaunchInstance = _autoLaunch.IsChecked == true;
            MarkDirty();
        };

        _rewardDelay.Value = (decimal)_options.RewardDelay.TotalSeconds;
        _rewardDelay.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                _options.RewardDelay = TimeSpan.FromSeconds((double)value);
                MarkDirty();
            }
        };

        _instanceMode.IsChecked = _options.InstanceMode;
        _instanceMode.IsCheckedChanged += (_, _) =>
        {
            _options.InstanceMode = _instanceMode.IsChecked == true;
            MarkDirty();
        };

        _exitWaypoint.Value = _options.ResolveExitWaypoint() + 1;
        _exitWaypoint.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                _options.ExitWaypoint = (int)value;
                MarkDirty();
            }
        };

        _repositionAfter.Value = (decimal)_options.RepositionAfter.TotalSeconds;
        _repositionAfter.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                _options.RepositionAfter = TimeSpan.FromSeconds((double)value);
                MarkDirty();
            }
        };

        _arrivalRadius.Value = _options.WaypointArrivalRadius;
        _arrivalRadius.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                _options.WaypointArrivalRadius = (int)value;
                MarkDirty();
            }
        };
        _saveRoute.Click += (_, _) => SaveRoute();

        if (_recorder is not null)
        {
            _recorder.Changed += () => Dispatcher.UIThread.Post(RefreshRoute);
        }

        BuildKeyFields();

        BuildSkillRows();
        BuildBuffRows();
        Content = BuildLayout();
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 250 ms is under the engine's 300 ms tick, so no decision goes unseen, and it is far
        // cheaper than subscribing the UI thread to every packet.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// Pulls a fresh snapshot of the engine into the controls.
    /// </summary>
    public void Refresh()
    {
        var running = _controller.IsRunning;
        _status.Text = running ? "EN COURS" : "EN PAUSE";
        _status.Foreground = running ? Ready : Cooling;
        _toggle.Content = running ? "Mettre en pause" : "Reprendre";

        var maxHp = _state.MaxHp;
        var maxMp = _state.MaxMp;

        _hpBar.Value = _state.HpRatio * 100;
        _mpBar.Value = _state.MpRatio * 100;
        _hpText.Text = $"{_state.CurrentHp} / {maxHp}   ({_state.HpRatio:P0})";
        _mpText.Text = $"{_state.CurrentMp} / {maxMp}   ({_state.MpRatio:P0})";
        _hpText.Foreground = _state.IsHpCritical ? Blocked : Ink;
        _mpText.Foreground = _state.IsMpCritical ? Blocked : Ink;

        var position = _state.Position;
        _position.Text = $"({position.X}, {position.Y})";
        _waypoint.Text = $"{_state.CurrentWaypoint}   [{_state.WaypointIndex % Math.Max(1, _options.Waypoints.Count) + 1}/{_options.Waypoints.Count}]";

        _target.Text = _state.Target is { } target
            ? $"#{target.EntityId}   {target.HpPercentage}% PV"
            : "aucune";
        _target.Foreground = _state.HasLiveTarget ? Ready : Muted;

        _entities.Text = _state.KnownEntities.Count.ToString();
        _decision.Text = _loop?.LastDecision ?? "moteur non disponible";
        RefreshMap();

        if (_input is null)
        {
            _live.Content = "Mode simulateur";
            _live.IsEnabled = false;
            ToolTip.SetTip(_live, "Lancé en --simulate : rien n'est envoyé au jeu. Relance en --pcap.");
        }
        else if (!_input.CanGoLive && !_input.IsLive)
        {
            // Say why the button cannot be pressed. A disabled button with no reason is what made
            // the bot look like it did nothing at all.
            _live.Content = "Jeu non lié";
            _live.IsEnabled = false;
            _live.Foreground = Blocked;
            ToolTip.SetTip
            (
                _live,
                "Aucune fenêtre de jeu n'est liée, les touches ne peuvent pas partir. "
                + "Vérifie que NosTale tourne, puis relance (au besoin avec --pid)."
            );
        }
        else
        {
            _live.Content = _input.IsLive ? "JOUE — clic pour arrêter" : "Simulation (n'agit pas)";
            _live.IsEnabled = true;
            _live.Foreground = _input.IsLive ? Blocked : Ink;
            ToolTip.SetTip
            (
                _live,
                _input.IsLive
                    ? "Les touches partent vers le jeu. Clic pour reprendre la main."
                    : "Clic pour laisser le bot appuyer sur les touches du jeu."
            );
        }

        RefreshSkills();
        RefreshBuffs();
        RefreshRoute();
        RefreshLaunch();
        RefreshReadiness();
        RefreshRun();
        RefreshLog();
    }

    private void RefreshMap()
    {
        var mapId = _state.CurrentMapId;

        if (mapId < 0)
        {
            _map.Text = "inconnue";
            _map.Foreground = Muted;
            return;
        }

        // The route's map is the thing that decides whether navigation will run here, so it belongs
        // next to the map rather than buried in the log.
        if (_options.RouteMapId is not { } routeMap)
        {
            _map.Text = mapId.ToString();
            _map.Foreground = Ink;
            return;
        }

        _map.Text = routeMap == mapId ? $"{mapId}   (route)" : $"{mapId}   (route sur {routeMap})";
        _map.Foreground = routeMap == mapId ? Ready : Blocked;
    }

    private void RefreshSkills()
    {
        var statuses = _rotation.Snapshot(_state.CurrentMp);

        foreach (var row in _skillRows)
        {
            if (row.Index >= statuses.Count)
            {
                continue;
            }

            var status = statuses[row.Index];

            if (!status.Enabled)
            {
                Set(row.Status, row.Dot, "désactivé", Muted);
                continue;
            }

            if (!status.AffordableNow)
            {
                Set(row.Status, row.Dot, "PM insuffisants", Blocked);
                continue;
            }

            // Worth distinguishing: a reserved skill is one whose key was pressed and which the
            // server has not confirmed. If it stays there, the client is refusing the cast.
            if (status.AwaitingConfirmation)
            {
                Set(row.Status, row.Dot, "envoyé…", Cooling);
                continue;
            }

            if (status.Remaining > TimeSpan.Zero)
            {
                Set(row.Status, row.Dot, $"{status.Remaining.TotalSeconds:0.0}s", Cooling);
                continue;
            }

            Set(row.Status, row.Dot, "prêt", Ready);
        }
    }

    private void RefreshBuffs()
    {
        var statuses = _buffs.Snapshot();

        foreach (var row in _buffRows)
        {
            if (row.Index >= statuses.Count)
            {
                continue;
            }

            var status = statuses[row.Index];

            if (!status.Buff.Enabled)
            {
                Set(row.Status, row.Dot, "désactivé", Muted);
                continue;
            }

            if (string.IsNullOrWhiteSpace(status.Buff.Key))
            {
                Set(row.Status, row.Dot, "pas de touche", Blocked);
                continue;
            }

            if (!status.IsActive)
            {
                Set(row.Status, row.Dot, status.OnCooldown ? "à relancer" : "tombé", Blocked);
                continue;
            }

            Set(row.Status, row.Dot, $"{status.Remaining.TotalSeconds:0}s", Ready);
        }
    }

    private static void Set(TextBlock status, Border dot, string text, IBrush brush)
    {
        status.Text = text;
        status.Foreground = brush;
        dot.Background = brush;
    }

    private void RefreshLog()
    {
        var lines = _logs.Snapshot();
        var builder = new StringBuilder(lines.Count * 80);

        foreach (var line in lines)
        {
            builder.Append(line.Timestamp.ToString("HH:mm:ss.fff"))
                   .Append("  ")
                   .Append(Abbreviate(line.Level))
                   .Append("  ")
                   .Append(line.Message)
                   .Append('\n');
        }

        var text = builder.ToString();
        if (_log.Text == text)
        {
            return;
        }

        _log.Text = text;
        _logScroll.ScrollToEnd();
    }

    /// <summary>
    /// Lays the window out: what is always true on top, everything else behind a tab.
    /// </summary>
    /// <remarks>
    /// One column of ten sections meant scrolling past a room's settings to reach a farming one,
    /// and reading both to find out which was in force. The two jobs are separate, so they are
    /// separate here. What stays on top is only what is true whichever of them is running - what
    /// the character is worth, and what the loop just decided - because those are the two things
    /// worth glancing at without looking for them.
    /// </remarks>
    private Control BuildLayout()
    {
        var root = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };

        root.Children.Add(Place(BuildHeader(), 0));
        root.Children.Add(Place(new StackPanel
        {
            Spacing = 0,
            Children = { BuildVitals(), BuildDecision() }
        }, 1));

        var tabs = new TabControl { Margin = new Thickness(0, 6, 0, 0) };

        tabs.Items.Add(Tab("Vue d'ensemble", new StackPanel
        {
            Spacing = 0,
            Children =
            {
                BuildInfo(),
                Section("Ce que le bot peut faire", BuildReadinessSection())
            }
        }));

        tabs.Items.Add(Tab("Combat", new StackPanel
        {
            Spacing = 0,
            Children =
            {
                Section("Touches", BuildKeySection()),
                Section("Rotation", BuildSkillSection()),
                Section("Buffs", BuildBuffSection())
            }
        }));

        tabs.Items.Add(Tab("Farm", new StackPanel
        {
            Spacing = 0,
            Children =
            {
                Section("Route de patrouille", BuildRouteSection()),
                Section("Clics", BuildClickSection())
            }
        }));

        tabs.Items.Add(Tab("Espace-temps", Section("Salle d'instance", BuildInstanceSection())));

        tabs.Items.Add(Tab("Journal", new StackPanel
        {
            Spacing = 0,
            Children =
            {
                Section("Enregistrer une run", BuildRunSection()),
                Section("Journal", _logScroll)
            }
        }));

        Grid.SetRow(tabs, 2);
        root.Children.Add(tabs);

        return root;
    }

    private static TabItem Tab(string header, Control content)
        => new()
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = content,
                Padding = new Thickness(0, 10, 8, 0),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };

    private Control BuildHeader()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };

        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(Label("NosSmoothCustomClient", 17, FontWeight.Bold));

        // Naming the mode correctly matters more than it looks: this line used to call capture mode
        // "simulator" too, so a bot reading the real game told its operator none of it was real.
        var (modeText, modeColour) = _mode switch
        {
            RunMode.Pcap => ("Mode : capture — lecture du vrai client", Ready),
            RunMode.Attach => ("Mode : client attaché", Ready),
            _ => ("MODE SIMULATEUR — faux serveur, rien ici n'est ton personnage", Blocked)
        };

        title.Children.Add(new TextBlock
        {
            Text = modeText,
            Foreground = modeColour,
            FontSize = 11.5,
            FontWeight = _mode == RunMode.Simulate ? FontWeight.SemiBold : FontWeight.Normal
        });

        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 12, 0);

        _live.Margin = new Thickness(0, 0, 8, 0);
        _save.Margin = new Thickness(0, 0, 8, 0);
        _save.Height = 32;

        // Saving applies to every tab, so it belongs with the things that are always true rather
        // than at the bottom of whichever section happened to hold it.
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_saveStatus, 1);
        Grid.SetColumn(_save, 2);
        Grid.SetColumn(_status, 3);
        Grid.SetColumn(_live, 4);
        Grid.SetColumn(_toggle, 5);
        row.Children.Add(title);
        row.Children.Add(_saveStatus);
        row.Children.Add(_save);
        row.Children.Add(_status);
        row.Children.Add(_live);
        row.Children.Add(_toggle);

        return new Border { Child = row, Margin = new Thickness(0, 0, 0, 12) };
    }

    private Control BuildDecision()
    {
        // What the loop just decided, on top and never behind a tab. Every "the bot does nothing"
        // in this project turned out to be the loop deciding, correctly and quietly, not to act -
        // and this is the one line that says so without being looked for.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var label = new TextBlock
        {
            Text = "EN TRAIN DE",
            Foreground = Muted,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(_decision, 1);
        row.Children.Add(label);
        row.Children.Add(_decision);

        return new Border
        {
            Child = row,
            Background = Panel,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9),
            Margin = new Thickness(0, 0, 0, 4)
        };
    }

    private Control BuildVitals()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(VitalRow("PV", _hpBar, _hpText));
        panel.Children.Add(VitalRow("PM", _mpBar, _mpText));
        return Section("Vitaux", panel);
    }

    private Control BuildInfo()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(0, 2, 0, 0)
        };

        AddCell(grid, 0, 0, "Carte", _map);
        AddCell(grid, 0, 2, "Position", _position);
        AddCell(grid, 1, 0, "Cible", _target);
        AddCell(grid, 1, 2, "Entités", _entities);
        AddCell(grid, 2, 0, "Waypoint", _waypoint);

        return Section("État", grid);
    }

    private void BuildSkillRows()
    {
        for (var i = 0; i < _options.Skills.Count; i++)
        {
            var index = i;
            var skill = _options.Skills[index];
            var row = new SkillRow { Index = index };

            row.Enabled = new CheckBox
            {
                IsChecked = skill.Enabled,
                Content = skill.Name,
                Foreground = Ink,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Enabled.IsCheckedChanged += (_, _) =>
            {
                var current = _options.Skills[index];
                _options.Skills[index] = current with { Enabled = row.Enabled.IsChecked == true };
                MarkDirty();
            };

            row.Seconds = Seconds(skill.EffectiveCooldown);
            row.Seconds.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not { } value)
                {
                    return;
                }

                var current = _options.Skills[index];
                _options.Skills[index] = current with { Cooldown = TimeSpan.FromSeconds((double)value) };
                MarkDirty();
            };

            row.Reset = ResetButton($"Remettre {skill.Name} à zéro : le sort redevient disponible tout de suite.");
            row.Reset.Click += (_, _) =>
            {
                _rotation.Reset(_options.Skills[index].CastId);
                Refresh();
            };

            row.Status = new TextBlock { Text = "-", Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            row.Dot = Dot();

            _skillRows.Add(row);
            _skills.Children.Add(BuildRow(row.Enabled, skill.Key, row.Seconds, row.Reset, row.Status, row.Dot));
        }
    }

    private void BuildBuffRows()
    {
        for (var i = 0; i < _options.Buffs.Count; i++)
        {
            var index = i;
            var buff = _options.Buffs[index];
            var row = new BuffRow { Index = index };

            row.Enabled = new CheckBox
            {
                IsChecked = buff.Enabled,
                Content = buff.Name,
                Foreground = Ink,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Enabled.IsCheckedChanged += (_, _) =>
            {
                var current = _options.Buffs[index];
                _options.Buffs[index] = current with { Enabled = row.Enabled.IsChecked == true };
                MarkDirty();
            };

            // For a buff the number that matters is how long it lasts: that is what decides when it
            // has to go back up, and the server corrects it whenever the card id is known.
            row.Seconds = Seconds(buff.EffectiveDuration);
            row.Seconds.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not { } value)
                {
                    return;
                }

                var current = _options.Buffs[index];
                _options.Buffs[index] = current with { Duration = TimeSpan.FromSeconds((double)value) };
                MarkDirty();
            };

            row.Reset = ResetButton($"Considérer {buff.Name} comme tombé : il sera relancé au prochain tour.");
            row.Reset.Click += (_, _) =>
            {
                _buffs.Reset(_options.Buffs[index]);
                Refresh();
            };

            row.Status = new TextBlock { Text = "-", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            row.Dot = Dot();

            _buffRows.Add(row);
            _buffPanel.Children.Add(BuildRow(row.Enabled, buff.Key, row.Seconds, row.Reset, row.Status, row.Dot));
        }
    }

    private Control BuildSkillSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_skills);

        // The countdown shown here is the bot's own, started from whatever the cooldown field said
        // at the time of the cast. Correcting that field cannot shorten a wait already under way,
        // so there has to be a way to end it by hand - otherwise tuning a number means sitting
        // through the old one.
        panel.Children.Add(Note
        (
            "Le ↻ d'une ligne rend ce sort disponible immédiatement. À utiliser après avoir corrigé "
            + "un temps de recharge : le décompte en cours a démarré avec l'ancienne valeur."
        ));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(_resetSkills);
        panel.Children.Add(actions);

        return panel;
    }

    private Control BuildBuffSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_buffPanel);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(_resetBuffs);
        panel.Children.Add(actions);

        return panel;
    }

    private static Border BuildRow(CheckBox enabled, string? key, NumericUpDown seconds, Button reset, TextBlock status, Border dot)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,60,150,44,96,18") };

        var keyLabel = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(key) ? "-" : "touche " + key,
            Foreground = string.IsNullOrWhiteSpace(key) ? Blocked : Ink,
            FontSize = 13,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(keyLabel, 1);
        Grid.SetColumn(seconds, 2);
        Grid.SetColumn(reset, 3);
        Grid.SetColumn(status, 4);
        Grid.SetColumn(dot, 5);
        grid.Children.Add(enabled);
        grid.Children.Add(keyLabel);
        grid.Children.Add(seconds);
        grid.Children.Add(reset);
        grid.Children.Add(status);
        grid.Children.Add(dot);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#26282C"))
        };
    }

    private static NumericUpDown Seconds(TimeSpan value)
        => new()
        {
            Value = (decimal)Math.Round(value.TotalSeconds, 1),
            Minimum = 0,
            Maximum = 3600,
            Increment = 1,
            FormatString = "0.#",
            Width = 132,
            Height = 34,
            FontSize = 15,
            Padding = new Thickness(6, 0),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

    private static Control Field(string label, Control editor, string? hint = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 11.5,
            Width = 230,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(editor);

        if (hint is not null)
        {
            row.Children.Add(Note(hint));
        }

        return row;
    }

    private static TextBlock Heading(string text)
        => new()
        {
            Text = text.ToUpperInvariant(),
            Foreground = Muted,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 6, 0, 0)
        };

    private static TextBlock Note(string text, IBrush? foreground = null)
        => new()
        {
            Text = text,
            Foreground = foreground ?? Muted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 700
        };

    private static Button ResetButton(string tip)
        => new()
        {
            Content = "\u21BB",
            Width = 32,
            Height = 32,
            FontSize = 15,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            [ToolTip.TipProperty] = tip
        };

    private static Border Dot()
        => new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Muted, VerticalAlignment = VerticalAlignment.Center };

    private void MarkDirty()
    {
        _saveStatus.Text = "modifications non enregistrées";
        _saveStatus.Foreground = Cooling;
    }

    private void SaveSettings()
    {
        var (path, error) = LocalConfigurationWriter.Save(_options);

        if (path is null)
        {
            _saveStatus.Text = "échec : " + error;
            _saveStatus.Foreground = Blocked;
            return;
        }

        _saveStatus.Text = "enregistré dans " + Path.GetFileName(path);
        _saveStatus.Foreground = Ready;
    }

    private sealed class SkillRow
    {
        public int Index { get; init; }

        public CheckBox Enabled { get; set; } = null!;

        public NumericUpDown Seconds { get; set; } = null!;

        public Button Reset { get; set; } = null!;

        public TextBlock Status { get; set; } = null!;

        public Border Dot { get; set; } = null!;
    }

    private sealed class BuffRow
    {
        public int Index { get; init; }

        public CheckBox Enabled { get; set; } = null!;

        public NumericUpDown Seconds { get; set; } = null!;

        public Button Reset { get; set; } = null!;

        public TextBlock Status { get; set; } = null!;

        public Border Dot { get; set; } = null!;
    }

    private static TextBox KeyBox()
        => new()
        {
            Width = 78,
            Height = 34,
            FontSize = 15,
            MaxLength = 5,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

    private void BuildKeyFields()
    {
        var keys = _options.Keys;

        _keyAttack.Text = keys.TargetAndAttack;
        _keyLoot.Text = keys.Loot;
        _keyHp.Text = keys.HpPotion;
        _keyMp.Text = keys.MpPotion;
        _pressDelay.Value = (decimal)keys.PressDelay.TotalMilliseconds;

        // Empty means "no key for this action", which is different from a bad key: the first
        // disables the action deliberately, the second is a typo worth showing.
        Bind(_keyAttack, v => _options.Keys.TargetAndAttack = v ?? "space");
        Bind(_keyLoot, v => _options.Keys.Loot = v);
        Bind(_keyHp, v => _options.Keys.HpPotion = v);
        Bind(_keyMp, v => _options.Keys.MpPotion = v);

        _pressDelay.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                _options.Keys.PressDelay = TimeSpan.FromMilliseconds((double)value);
                MarkDirty();
            }
        };
    }

    private void Bind(TextBox box, Action<string?> assign)
        => box.TextChanged += (_, _) =>
        {
            var text = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            var valid = text is null || GameKey.TryParse(text, out _);

            box.Foreground = valid ? Ink : Blocked;
            if (valid)
            {
                assign(text);
                MarkDirty();
            }
        };

    private Control BuildRunSection()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(Field("Touche d'enregistrement", _recordKey,
            "pressée dans le jeu ; elle doit être une touche dont le client ne fait rien"));

        panel.Children.Add(_keyWatch);

        panel.Children.Add(Note
        (
            "Toutes les touches de la liste sont surveillées, pas seulement celle choisie : appuie "
            + "sur l'une d'elles dans le jeu et la ligne ci-dessus le dit. Si rien ne s'affiche, la "
            + "touche est interceptée avant d'arriver ici — prends-en une autre, ou ignore-la : "
            + "les deux boutons ci-dessous font le même travail et marchent toujours. Clique sur "
            + "« Commencer », retourne dans le jeu, joue, reviens cliquer sur « Finir »."
        ));

        panel.Children.Add(Note
        (
            "Joue la séquence à la main : chaque touche et chaque clic envoyés au jeu sont notés "
            + "avec ce que le serveur annonçait au même instant. C'est cette seconde moitié qui "
            + "rend la run rejouable — on attend la conséquence, pas un chrono."
        ));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_runStart);
        actions.Children.Add(_runStop);
        actions.Children.Add(_runSave);
        actions.Children.Add(_runClear);
        actions.Children.Add(_runStatus);
        panel.Children.Add(actions);

        panel.Children.Add(_runScroll);
        return panel;
    }

    private void RefreshRun()
    {
        if (_runs is null)
        {
            SetRecordingButtons(false, false, "Commencer l'enregistrement");
            _runSave.IsEnabled = false;
            _runClear.IsEnabled = false;
            _runStatus.Text = "disponible en mode capture (--pcap)";
            _keyWatch.Text = "surveillance des touches disponible en mode capture (--pcap)";
            _keyWatch.Foreground = Muted;
            return;
        }

        var (watchText, watchGood) = HotKeyWatch.Describe
        (
            _runs.LastHotKey?.Label,
            _runs.LastHotKey?.Ago,
            _runs.Key
        );

        _keyWatch.Text = watchText;
        _keyWatch.Foreground = watchGood ? Ready : Cooling;

        if (!_runs.Available)
        {
            SetRecordingButtons(false, false, "Enregistrement hors service");
            _runStatus.Text = _runs.UnavailableReason ?? "fenêtre de jeu introuvable";
            _runStatus.Foreground = Blocked;
            return;
        }

        var events = _runs.Events;

        SetRecordingButtons
        (
            !_runs.Recording,
            _runs.Recording,
            _runs.Recording
                ? $"ENREGISTRE — {events.Count} évènement(s)"
                : $"Commencer l'enregistrement (ou {_runs.Key} dans le jeu)"
        );

        _runSave.IsEnabled = events.Count > 0;
        _runClear.IsEnabled = events.Count > 0 && !_runs.Recording;

        if (_runStatus.Foreground != Ready)
        {
            _runStatus.Text = events.Count == 0 ? "rien d'enregistré" : $"{events.Count} évènement(s)";
            _runStatus.Foreground = Muted;
        }

        _runList.Children.Clear();

        // The tail, not the whole run: the last few lines are what tells you it is recording what
        // you are doing, and a thousand-line list would only make the window slow.
        foreach (var entry in events.Skip(Math.Max(0, events.Count - 40)))
        {
            _runList.Children.Add(new TextBlock
            {
                Text = entry.Summary,
                Foreground = entry.Kind == RunEventKind.State ? Muted : Ink,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
            });
        }
    }

    /// <summary>
    /// Starts recording, from either of the two places it is offered.
    /// </summary>
    /// <remarks>
    /// Two buttons rather than one that changes meaning. A toggle labelled by its own state is a
    /// button you have to read before you dare press it, and the one thing worse than that is
    /// pressing it by mistake and losing a run that took a full instance to play.
    /// </remarks>
    private void StartRecording()
    {
        if (_runs is null)
        {
            _runStatus.Text = "disponible en mode capture (--pcap)";
            _runStatus.Foreground = Blocked;
            return;
        }

        if (!_runs.Recording)
        {
            _runs.Toggle();
        }

        RefreshRun();
    }

    private void StopRecording()
    {
        if (_runs is null)
        {
            _runStatus.Text = "disponible en mode capture (--pcap)";
            _runStatus.Foreground = Blocked;
            return;
        }

        if (_runs.Recording)
        {
            _runs.Toggle();
        }

        RefreshRun();
    }

    private void SetRecordingButtons(bool canStart, bool canStop, string startLabel)
    {
        _runStart.IsEnabled = canStart;
        _launchRecStart.IsEnabled = canStart;
        _runStop.IsEnabled = canStop;
        _launchRecStop.IsEnabled = canStop;

        _runStart.Content = startLabel;
        _launchRecStart.Content = startLabel;

        _runStart.Foreground = canStop ? Blocked : Ink;
        _launchRecStart.Foreground = canStop ? Blocked : Ink;
    }

    private void SaveRun()
    {
        if (_runs is null)
        {
            return;
        }

        var (path, error) = _runs.Save();
        _runStatus.Text = path is null ? "échec : " + error : "écrit dans " + Path.GetFileName(path);
        _runStatus.Foreground = path is null ? Blocked : Ready;
    }

    private Control BuildReadinessSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Note
        (
            "Chaque ligne rouge est une chose que le bot ne fera pas, avec la raison. "
            + "Un bot qui ne bouge pas se lit ici, pas dans le journal."
        ));

        panel.Children.Add(_readiness);
        return panel;
    }

    private void RefreshReadiness()
    {
        var items = BotReadiness.Describe(_options, _state, _input);

        // Rebuilt rather than patched: the list is short, and every row's text can change with the
        // state it describes.
        _readiness.Children.Clear();

        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,200,*") };

            var dot = new TextBlock
            {
                Text = item.Ready ? "●" : "●",
                Foreground = item.Ready ? Ready : Blocked,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };

            var name = new TextBlock
            {
                Text = item.Name,
                Foreground = Ink,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };

            var detail = new TextBlock
            {
                Text = item.Detail,
                Foreground = item.Ready ? Muted : Blocked,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(detail, 2);
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(detail);

            _readiness.Children.Add(row);
        }
    }

    private Control BuildKeySection()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };

        AddKeyCell(grid, 0, 0, "Cibler / attaquer", _keyAttack);
        AddKeyCell(grid, 0, 2, "Ramasser", _keyLoot);
        AddKeyCell(grid, 1, 0, "Potion de vie", _keyHp);
        AddKeyCell(grid, 1, 2, "Potion de mana", _keyMp);
        AddKeyCell(grid, 2, 0, "Délai entre touches (ms)", _pressDelay);

        return grid;
    }

    private static void AddKeyCell(Grid grid, int row, int column, string label, Control field)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center
        };

        field.Margin = new Thickness(0, 4, 24, 4);

        Grid.SetRow(text, row);
        Grid.SetColumn(text, column);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, column + 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
    }

    private Control BuildRouteSection()
    {
        var panel = new StackPanel { Spacing = 10 };

        // A waypoint carries two coordinate systems for reasons that are not obvious from a list of
        // numbers, so the panel says which does what.
        panel.Children.Add(Note
        (
            "Le trajet parcouru quand il n'y a plus rien à taper. Le bot clique sur la minimap pour "
            + "aller au point suivant, et sait qu'il est arrivé grâce à sa position réelle."
        ));

        panel.Children.Add(Note
        (
            "Ajouter un point : place ton personnage à l'endroit voulu → arme F9 → vise ce même "
            + "endroit sur la minimap dans NosTale → presse F9. F10 enregistre.",
            Ink
        ));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_arm);
        actions.Children.Add(_saveRoute);
        actions.Children.Add(_clearRoute);
        actions.Children.Add(_routeStatus);
        panel.Children.Add(actions);

        panel.Children.Add(_adoptMap);
        panel.Children.Add(Field("Rayon d'arrivée (cases)", _arrivalRadius,
            "un clic minimap est imprécis ; trop petit, le bot n'arrive jamais"));

        panel.Children.Add(_routeList);
        return panel;
    }

    private Control BuildClickSection()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(Note
        (
            "Comment les clics atteignent le jeu. Les messages postés laissent la souris libre et "
            + "fonctionnent fenêtre en arrière-plan ; le vrai curseur ne peut pas être ignoré mais "
            + "prend la main sur la machine. Le bot bascule seul si les premiers ne font rien."
        ));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_testClick);
        actions.Children.Add(_probeClick);
        actions.Children.Add(_useProbed);
        panel.Children.Add(actions);

        panel.Children.Add(Note
        (
            "« Sonder » essaie une fenêtre du client par pression : regarde le personnage, et garde "
            + "celle qui le fait partir. Une fenêtre qui accepte les clics postés rend le bot "
            + "utilisable en arrière-plan, et plusieurs comptes menables en parallèle.",
            Ink
        ));

        return panel;
    }

    private Control BuildInstanceSection()
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(Note
        (
            "Une salle d'instance ne se patrouille pas, elle se balaie. Le bot tient chaque point "
            + "tant que des monstres y tombent, puis passe au suivant dans l'ordre. La sortie ne "
            + "fait pas partie de la tournée : elle est prise quand le serveur annonce la salle "
            + "terminée, ce qu'aucun compte de monstres ne peut dire à sa place."
        ));

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        toggles.Children.Add(_instanceMode);
        panel.Children.Add(toggles);

        panel.Children.Add(Field("Waypoint de sortie (n°)", _exitWaypoint, "le portail de fin, enregistré en dernier"));
        panel.Children.Add(Field("Passer au point suivant après (s)", _repositionAfter, "sans rien qui tombe"));

        panel.Children.Add(Heading("Récompense de fin"));
        panel.Children.Add(Note
        (
            "Le panneau de récompense n'est annoncé par aucun paquet : ses clics s'enregistrent à "
            + "la main et sont rejoués dans l'ordre, une fois la sortie atteinte."
        ));

        panel.Children.Add(Note
        (
            "Arme F9, puis dans le jeu : F7 sur la case à tirer (double-clic), F8 sur Confirm "
            + "(clic simple). F10 enregistre.",
            Ink
        ));

        panel.Children.Add(Field("Attendre avant les clics (s)", _rewardDelay,
            "le panneau met quelques secondes à s'afficher ; le délai repart du chargement de carte"));

        var rewardActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        rewardActions.Children.Add(_playReward);
        rewardActions.Children.Add(_clearReward);
        rewardActions.Children.Add(_rewardStatus);
        panel.Children.Add(rewardActions);
        panel.Children.Add(_rewardList);

        panel.Children.Add(Note
        (
            "« Jouer les clics maintenant » rejoue la séquence à l'instant, panneau à l'écran : "
            + "c'est ce qui distingue des coordonnées fausses d'une séquence jamais déclenchée."
        ));

        panel.Children.Add(Heading("Lancement de l'espace-temps"));
        panel.Children.Add(Note
        (
            "Tout ce qui précède le premier monstre est de l'interface : s'asseoir et se relever "
            + "pour réveiller l'entrée, la fenêtre MISSION, START, la marche jusqu'au portail, la "
            + "question « première salle ? ». Aucun paquet ne l'annonce, donc ça ne se devine pas : "
            + "ça s'enregistre une fois et ça se rejoue."
        ));

        panel.Children.Add(_launchHowTo);

        panel.Children.Add(_autoLaunch);

        var recording = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        recording.Children.Add(_launchRecStart);
        recording.Children.Add(_launchRecStop);
        panel.Children.Add(recording);

        var launchActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        launchActions.Children.Add(_buildLaunch);
        launchActions.Children.Add(_launchNow);
        launchActions.Children.Add(_clearLaunch);
        launchActions.Children.Add(_launchStatus);
        panel.Children.Add(launchActions);
        panel.Children.Add(_launchList);

        return panel;
    }

    /// <summary>
    /// Turns the run just recorded into the sequence that opens the instance.
    /// </summary>
    private void BuildLaunchFromRun()
    {
        if (_runs is null)
        {
            _launchStatus.Text = "disponible en mode capture (--pcap)";
            _launchStatus.Foreground = Blocked;
            return;
        }

        if (_runs.Recording)
        {
            _launchStatus.Text = $"arrête l'enregistrement d'abord ({_runs.Key})";
            _launchStatus.Foreground = Blocked;
            return;
        }

        var steps = StartupSequenceBuilder.FromRun(_runs.Events);

        if (steps.Count == 0)
        {
            _launchStatus.Text = "ce run ne contient aucune touche ni clic gauche";
            _launchStatus.Foreground = Blocked;
            RefreshLaunch();
            return;
        }

        _options.StartupSequence = steps.ToList();
        MarkDirty();

        _launchStatus.Text = $"{steps.Count} étape(s) reprises du run";
        _launchStatus.Foreground = Ready;
        RefreshLaunch();
    }

    /// <summary>
    /// Plays the launch sequence now, without waiting for the bot to be started.
    /// </summary>
    private void LaunchNow()
    {
        if (_loop is null)
        {
            _launchStatus.Text = "disponible en mode capture (--pcap)";
            _launchStatus.Foreground = Blocked;
            return;
        }

        if (_options.StartupSequence.Count == 0)
        {
            _launchStatus.Text = $"aucune séquence : enregistre un run ({RecordKey}) puis reprends-le";
            _launchStatus.Foreground = Blocked;
            return;
        }

        if (!_controller.IsRunning)
        {
            _launchStatus.Text = "le bot est en pause : la séquence ne partirait pas";
            _launchStatus.Foreground = Blocked;
            return;
        }

        _loop.LaunchInstance();
        _launchStatus.Text = "lancement démarré";
        _launchStatus.Foreground = Ready;
        RefreshLaunch();
    }

    /// <summary>Gets the recording key as it currently reads, for every line that names it.</summary>
    private string RecordKey => HotKey.Resolve(_options.RecordRunKey).Label;

    private void RefreshLaunch()
    {
        // Named rather than spelled out: the key is a setting now, and a panel still saying F11
        // after it was changed is worse than one that never named it.
        _launchHowTo.Text =
            "« Commencer l'enregistrement », puis fais le lancement à la main dans le jeu "
            + "(C, C, START, marche, Entrée), reviens sur « Finir l'enregistrement », et enfin "
            + $"« Faire de ce run la séquence ». La touche {RecordKey} fait la même chose depuis le "
            + "jeu si elle passe. Chaque étape retient la carte ou l'endroit où tu étais : la "
            + "relecture attend le chargement, elle ne compte pas les secondes.";

        _clearLaunch.IsEnabled = _options.StartupSequence.Count > 0;
        _launchList.Children.Clear();

        if (_loop?.Launcher is { State: LaunchState.Running } running)
        {
            _launchStatus.Text = running.Status;
            _launchStatus.Foreground = Cooling;
        }

        if (_options.StartupSequence.Count == 0)
        {
            _launchList.Children.Add(new TextBlock
            {
                Text = $"  aucune séquence de lancement enregistrée ({RecordKey} puis « Faire de ce run la séquence »)",
                Foreground = Blocked,
                FontSize = 11
            });

            return;
        }

        var current = _loop?.Launcher is { State: LaunchState.Running } live ? live.Step : 0;

        for (var i = 0; i < _options.StartupSequence.Count; i++)
        {
            _launchList.Children.Add(new TextBlock
            {
                Text = $"  {i + 1}.  {_options.StartupSequence[i]}",
                Foreground = i + 1 == current ? Ready : Ink,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
            });
        }
    }

    private void ToggleLive()
    {
        if (_input is null)
        {
            return;
        }

        _input.SetLive(!_input.IsLive);
        Refresh();
    }

    private void ToggleArm()
    {
        if (_recorder is null)
        {
            return;
        }

        _recorder.Armed = !_recorder.Armed;
        RefreshRoute();
    }

    /// <summary>
    /// Declares that the route belongs to the map the character is standing on.
    /// </summary>
    /// <remarks>
    /// The points themselves are right far more often than the label on them - a route recorded in
    /// a room and saved somewhere else came out belonging to the wrong map - and re-walking a whole
    /// room to correct one number is a poor trade. The map is read off the character, the same
    /// source the recorder uses, so this cannot write a value the bot would not have written itself.
    /// </remarks>
    private void AdoptCurrentMap()
    {
        var map = _state.CurrentMapId;

        if (map < 0)
        {
            _routeStatus.Text = "carte encore inconnue : traverse une carte pour qu'elle soit annoncée";
            _routeStatus.Foreground = Blocked;
            return;
        }

        _options.RouteMapId = map;
        MarkDirty();
        RefreshRoute();

        _routeStatus.Text = $"route déclarée sur la carte {map} — pense à enregistrer les réglages";
        _routeStatus.Foreground = Ready;
    }

    private void ClearRoute()
    {
        // Deliberately not gated on the recorder: wiping the route the bot walks has nothing to do
        // with being able to record a new one, and a button that needs --pcap to delete four lines
        // of configuration is a button that looks broken.
        if (_recorder is not null)
        {
            _recorder.Clear();
        }
        else
        {
            _options.Waypoints = new List<Waypoint>();
            _options.RouteMapId = null;
        }

        RefreshRoute();

        _routeStatus.Text = "route effacée (le fichier n'est pas touché : relancer la restaure)";
        _routeStatus.Foreground = Muted;
    }

    /// <summary>
    /// Clicks the first waypoint's minimap point, right now.
    /// </summary>
    /// <remarks>
    /// Waiting for the loop to try, stall, and escalate takes the better part of ten seconds and
    /// buries the answer in the log. One click on demand settles in a second whether the point is
    /// right and whether the client reacts to clicks at all.
    /// </remarks>
    /// <summary>
    /// Plays the recorded reward clicks on demand, panel on screen.
    /// </summary>
    /// <remarks>
    /// The one part of a run nothing confirms: the panel is drawn over the window and mentioned by
    /// no packet, so a sequence that did nothing at the end of an instance leaves two explanations
    /// and no way to choose between them - the coordinates were wrong, or they were never played.
    /// Pressing this while the panel is up answers that in one click.
    /// </remarks>
    private async void PlayRewardNow()
    {
        if (_loop is null)
        {
            _rewardStatus.Text = "disponible en mode capture (--pcap)";
            _rewardStatus.Foreground = Blocked;
            return;
        }

        if (_options.RewardSequence.Count == 0)
        {
            _rewardStatus.Text = "aucun clic enregistré : F9 puis F7/F8, F10 pour garder";
            _rewardStatus.Foreground = Blocked;
            return;
        }

        if (_input is { IsLive: false })
        {
            _rewardStatus.Text = "passe en JOUE d'abord, sinon les clics ne sont que simulés";
            _rewardStatus.Foreground = Blocked;
            return;
        }

        _playReward.IsEnabled = false;
        _rewardStatus.Text = $"{_options.RewardSequence.Count} clic(s) en cours...";
        _rewardStatus.Foreground = Muted;

        try
        {
            var sent = await _loop.PlayRewardAsync().ConfigureAwait(true);
            _rewardStatus.Text = sent ? "séquence jouée - regarde le panneau" : "la fenêtre a refusé un clic";
            _rewardStatus.Foreground = sent ? Ready : Blocked;
        }
        catch (Exception ex)
        {
            _rewardStatus.Text = ex.Message;
            _rewardStatus.Foreground = Blocked;
        }
        finally
        {
            _playReward.IsEnabled = true;
        }
    }

    private void TestClick()
    {
        if (_input is null)
        {
            _routeStatus.Text = "disponible en mode capture (--pcap)";
            _routeStatus.Foreground = Blocked;
            return;
        }

        var route = _recorder?.Recorded is { Count: > 0 } recorded ? recorded : _options.Waypoints.ToArray();

        if (route.Count == 0 || route[0] is not { ClickX: { } x, ClickY: { } y })
        {
            _routeStatus.Text = "le point 1 n'a pas de clic minimap : enregistre la route (F9/F10)";
            _routeStatus.Foreground = Blocked;
            return;
        }

        if (!_input.IsLive)
        {
            _routeStatus.Text = "passe en JOUE d'abord, sinon le clic n'est que simulé";
            _routeStatus.Foreground = Blocked;
            return;
        }

        var sent = _input.ClickAt(x, y);
        _routeStatus.Text = sent
            ? $"clic envoyé en ({x},{y}) par « {_input.ClickMode} » — regarde si le personnage part"
            : $"le clic en ({x},{y}) a été refusé par la fenêtre";

        _routeStatus.Foreground = sent ? Ready : Blocked;
    }

    /// <summary>
    /// Posts a click to one window of the client per press, to find one that listens.
    /// </summary>
    /// <remarks>
    /// One per press rather than all of them in a burst: the answer is whether the character moved,
    /// which only a person watching the game can give, and they can only attribute it if a single
    /// window was tried. A Delphi client renders into a child window, and it is often that child
    /// rather than the form the bot binds to that handles mouse input - if one of them does, clicks
    /// no longer need the real cursor, and several clients can be driven at once.
    /// </remarks>
    private void ProbeClick()
    {
        if (_input is null)
        {
            _routeStatus.Text = "disponible en mode capture (--pcap)";
            _routeStatus.Foreground = Blocked;
            return;
        }

        var route = _recorder?.Recorded is { Count: > 0 } recorded ? recorded : _options.Waypoints.ToArray();

        if (route.Count == 0 || route[0] is not { ClickX: { } x, ClickY: { } y })
        {
            _routeStatus.Text = "il faut un point 1 avec un clic minimap pour sonder";
            _routeStatus.Foreground = Blocked;
            return;
        }

        var candidates = _input.ClickCandidates();

        if (candidates.Count == 0)
        {
            _routeStatus.Text = "aucune fenêtre à sonder : le jeu n'est pas lié";
            _routeStatus.Foreground = Blocked;
            return;
        }

        var index = _probeIndex % candidates.Count;
        var (handle, className, depth) = candidates[index];
        _probeIndex++;

        var sent = _input.ProbeClick(handle, x, y);
        _lastProbed = sent ? (handle, className) : null;
        _useProbed.IsEnabled = sent;

        _routeStatus.Text = sent
            ? $"essai {index + 1}/{candidates.Count} : « {className} » (niveau {depth}) — le personnage part ?"
            : $"essai {index + 1}/{candidates.Count} : « {className} » a refusé le message";

        _routeStatus.Foreground = sent ? Ink : Blocked;

        _logs.Add(new LogLine
        (
            DateTimeOffset.Now,
            LogLevel.Information,
            "Sonde",
            sent
                ? $"Clic posté à 0x{handle.ToInt64():X} (« {className} », niveau {depth}), essai {index + 1}/{candidates.Count}."
                : $"Le message a été refusé par 0x{handle.ToInt64():X} (« {className} »)."
        ));
    }

    /// <summary>
    /// Keeps the window the last probe used as the one every click goes to.
    /// </summary>
    /// <remarks>
    /// Only the operator can tell which probe moved the character - PostMessage reports queued, not
    /// acted on - so the choice is theirs to confirm, one press after the one that worked.
    /// </remarks>
    private void UseProbedWindow()
    {
        if (_input is null || _lastProbed is not { } probed)
        {
            return;
        }

        _input.UseClickWindow(probed.Handle, probed.ClassName);

        _routeStatus.Text = $"clics envoyés à « {probed.ClassName} » en messages postés — "
                            + "le jeu peut rester en arrière-plan";

        _routeStatus.Foreground = Ready;
    }

    private void SaveRoute()
    {
        if (_recorder is null)
        {
            return;
        }

        var (path, error) = _recorder.Save();
        _routeStatus.Text = path is null ? "échec : " + error : "route enregistrée";
        _routeStatus.Foreground = path is null ? Blocked : Ready;
    }

    private void RefreshRoute()
    {
        // The list is worth showing whether or not a recorder exists: without one the configured
        // route is still what the bot will walk, and an empty panel would read as "no route".
        if (_recorder is null)
        {
            _arm.Content = "Enregistrement indisponible";
        }
        else if (!_recorder.Available)
        {
            // The one state that used to be completely silent: F9 pressed, nothing recorded, no
            // reason given anywhere. Now the button says so and names the cause.
            _arm.Content = "F9 hors service";
            _arm.IsEnabled = false;
            _routeStatus.Text = _recorder.UnavailableReason ?? "fenêtre de jeu introuvable";
            _routeStatus.Foreground = Blocked;
        }
        else
        {
            _arm.Content = _recorder.Armed ? "F9 armé — cliquer pour désarmer" : "Armer l'enregistrement (F9)";
            _arm.IsEnabled = true;
            _arm.Foreground = _recorder.Armed ? Ready : Ink;


        }

        // The one setting that silently stops the bot walking, and the one the recorder used to get
        // wrong, so it is stated and fixable here rather than only in the log.
        var map = _state.CurrentMapId;
        _adoptMap.IsEnabled = map >= 0 && _options.RouteMapId != map;
        _adoptMap.Content = _options.RouteMapId == map && map >= 0
            ? $"Route déclarée sur cette carte ({map})"
            : $"Déclarer la route sur la carte courante ({(map < 0 ? "?" : map.ToString())})";

        _rewardList.Children.Clear();
        _clearReward.IsEnabled = _recorder is not null && _options.RewardSequence.Count > 0;

        if (_options.RewardSequence.Count == 0)
        {
            _rewardList.Children.Add(new TextBlock
            {
                Text = "  aucun clic de récompense enregistré (F7 / F8)",
                Foreground = Blocked,
                FontSize = 11
            });
        }
        else
        {
            for (var i = 0; i < _options.RewardSequence.Count; i++)
            {
                _rewardList.Children.Add(new TextBlock
                {
                    Text = $"  {i + 1}.  {_options.RewardSequence[i]}",
                    Foreground = Ink,
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
                });
            }
        }

        var route = _recorder?.Recorded ?? Array.Empty<Waypoint>();
        var shown = route.Count > 0 ? route : _options.Waypoints.ToArray();

        _routeList.Children.Clear();

        if (shown.Count == 0)
        {
            _routeList.Children.Add(new TextBlock { Text = "aucun point enregistré", Foreground = Muted, FontSize = 11 });
            return;
        }

        for (var i = 0; i < shown.Count; i++)
        {
            var waypoint = shown[i];
            _routeList.Children.Add(new TextBlock
            {
                Text = $"  {i + 1}.  carte {waypoint}   " +
                       (waypoint.IsClickable ? $"clic ({waypoint.ClickX},{waypoint.ClickY})" : "pas de point de clic"),
                Foreground = waypoint.IsClickable ? Ink : Blocked,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
            });
        }
    }

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
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private static Control VitalRow(string label, ProgressBar bar, TextBlock text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,170") };

        var name = new TextBlock { Text = label, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        bar.Margin = new Thickness(0, 0, 12, 0);
        text.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(name, 0);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(name);
        grid.Children.Add(bar);
        grid.Children.Add(text);

        return grid;
    }

    private static void AddCell(Grid grid, int row, int column, string label, TextBlock value)
    {
        var name = new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center
        };

        value.Margin = new Thickness(0, 3, 24, 3);

        Grid.SetRow(name, row);
        Grid.SetColumn(name, column);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, column + 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
    }

    private static Control Place(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static TextBlock Label(string text, double size, FontWeight weight)
        => new() { Text = text, FontSize = size, FontWeight = weight, Foreground = Ink };

    private static TextBlock Mono()
        => new()
        {
            Text = "-",
            Foreground = Ink,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
        };

    private static ProgressBar Bar(string colour)
        => new()
        {
            Minimum = 0,
            Maximum = 100,
            Height = 10,
            Foreground = new SolidColorBrush(Color.Parse(colour)),
            VerticalAlignment = VerticalAlignment.Center
        };

    private static string Abbreviate(LogLevel level)
        => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "----"
        };
}
