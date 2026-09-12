using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
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
    private readonly StackPanel _skills = new() { Spacing = 4 };
    private readonly StackPanel _buffPanel = new() { Spacing = 4 };
    private readonly Button _save = new() { Content = "Enregistrer les réglages", Height = 30 };
    private readonly TextBlock _saveStatus = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<SkillRow> _skillRows = new();
    private readonly List<BuffRow> _buffRows = new();
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
    public MainWindow
    (
        ProtocolStateManager state,
        SkillRotation rotation,
        BuffTracker buffs,
        BotController controller,
        BotOptions options,
        LogBuffer logs,
        RunMode mode
    )
    {
        _state = state;
        _rotation = rotation;
        _buffs = buffs;
        _controller = controller;
        _options = options;
        _logs = logs;
        _mode = mode;

        Title = "NosSmoothCustomClient";
        Width = 760;
        Height = 680;
        MinWidth = 520;
        MinHeight = 460;
        Background = new SolidColorBrush(Color.Parse("#141517"));

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

        RefreshSkills();
        RefreshBuffs();
        RefreshLog();
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

    private Control BuildLayout()
    {
        var grid = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto")
        };

        grid.Children.Add(Place(BuildHeader(), 0));
        grid.Children.Add(Place(BuildVitals(), 1));
        grid.Children.Add(Place(BuildInfo(), 2));
        grid.Children.Add(Place(Section("Rotation", _skills), 3));
        grid.Children.Add(Place(Section("Buffs", BuildBuffSection()), 4));
        grid.Children.Add(Place(Section("Journal", _logScroll), 5));

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private Control BuildHeader()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(Label("NosSmoothCustomClient", 17, FontWeight.Bold));
        title.Children.Add(new TextBlock
        {
            Text = _mode == RunMode.Attach ? "Mode : client attaché" : "Mode : simulateur (aucun jeu attaché)",
            Foreground = Muted,
            FontSize = 11
        });

        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 12, 0);

        Grid.SetColumn(title, 0);
        Grid.SetColumn(_status, 1);
        Grid.SetColumn(_toggle, 2);
        row.Children.Add(title);
        row.Children.Add(_status);
        row.Children.Add(_toggle);

        return new Border { Child = row, Margin = new Thickness(0, 0, 0, 12) };
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
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 2, 0, 0)
        };

        AddCell(grid, 0, 0, "Position", _position);
        AddCell(grid, 0, 2, "Cible", _target);
        AddCell(grid, 1, 0, "Waypoint", _waypoint);
        AddCell(grid, 1, 2, "Entités", _entities);

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
                FontSize = 12,
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

            row.Status = new TextBlock { Text = "-", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            row.Dot = Dot();

            _skillRows.Add(row);
            _skills.Children.Add(BuildRow(row.Enabled, skill.Key, row.Seconds, row.Status, row.Dot));
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
                FontSize = 12,
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

            row.Status = new TextBlock { Text = "-", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            row.Dot = Dot();

            _buffRows.Add(row);
            _buffPanel.Children.Add(BuildRow(row.Enabled, buff.Key, row.Seconds, row.Status, row.Dot));
        }
    }

    private Control BuildBuffSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_buffPanel);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(_save);
        actions.Children.Add(_saveStatus);
        panel.Children.Add(actions);

        return panel;
    }

    private static Border BuildRow(CheckBox enabled, string? key, NumericUpDown seconds, TextBlock status, Border dot)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44,96,74,14") };

        var keyLabel = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(key) ? "-" : key,
            Foreground = string.IsNullOrWhiteSpace(key) ? Blocked : Muted,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(keyLabel, 1);
        Grid.SetColumn(seconds, 2);
        Grid.SetColumn(status, 3);
        Grid.SetColumn(dot, 4);
        grid.Children.Add(enabled);
        grid.Children.Add(keyLabel);
        grid.Children.Add(seconds);
        grid.Children.Add(status);
        grid.Children.Add(dot);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(8, 5),
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
            Width = 88,
            Height = 26,
            FontSize = 11,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
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

        public TextBlock Status { get; set; } = null!;

        public Border Dot { get; set; } = null!;
    }

    private sealed class BuffRow
    {
        public int Index { get; init; }

        public CheckBox Enabled { get; set; } = null!;

        public NumericUpDown Seconds { get; set; } = null!;

        public TextBlock Status { get; set; } = null!;

        public Border Dot { get; set; } = null!;
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
