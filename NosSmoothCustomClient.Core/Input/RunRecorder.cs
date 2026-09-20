using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Packets.Enums.Entities;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Records a run played by hand: every key and click, against what the server said at the time.
/// </summary>
/// <remarks>
/// The point is not to replay the timings. A sequence played back on a stopwatch desynchronises on
/// the first slow load, and from there every later action lands in a world that is not the one it
/// was recorded in. What makes a recording replayable is the other half of each line: the state the
/// server reported. An action can then wait for the consequence it produced the first time -
/// the map actually changed, the monsters actually appeared - instead of for a number of seconds.
///
/// Input is only recorded while the game holds the foreground. That is what separates playing from
/// operating the bot's own window, and it costs nothing: what is being recorded is a run.
/// </remarks>
public sealed class RunRecorder : BackgroundService
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    private static readonly int[] WatchedKeys = BuildWatchedKeys();

    private readonly CaptureTarget _target;
    private readonly BotOptions _options;
    private readonly ProtocolStateManager _state;
    private readonly RunJournal _journal;
    private readonly ILogger<RunRecorder> _logger;

    private readonly List<RunEvent> _events = new();
    private readonly object _sync = new();

    private volatile bool _recording;
    private long _startedAt;

    // What of the offered hotkeys was last seen pressed, whether or not it is the one bound.
    private volatile string? _lastHotKey;
    private long _lastHotKeyAt;

    // The last world the recorder saw, so a change can be told from a repeat.
    private int _lastMap = int.MinValue;
    private long? _lastTarget;
    private int _lastMonsters = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunRecorder"/> class.
    /// </summary>
    /// <param name="target">The captured game process.</param>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public RunRecorder
    (
        CaptureTarget target,
        BotOptions options,
        ProtocolStateManager state,
        RunJournal journal,
        ILogger<RunRecorder> logger
    )
    {
        _target = target;
        _options = options;
        _state = state;
        _journal = journal;
        _logger = logger;
    }

    /// <summary>Raised whenever the recording changes.</summary>
    public event Action? Changed;

    /// <summary>Gets a value indicating whether a game window was found, so recording can work.</summary>
    public bool Available { get; private set; }

    /// <summary>Gets the reason recording is unavailable, when it is.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Gets the key that starts and stops recording, as it reads.</summary>
    public string Key => HotKey.Resolve(_options.RecordRunKey).Label;

    /// <summary>Gets the last offered hotkey seen pressed, and how long ago.</summary>
    /// <remarks>
    /// Every key on the list is watched, not only the bound one. That is the whole point: the
    /// question a dead hotkey raises is whether anything at all gets through, and only a key that
    /// does can answer it.
    /// </remarks>
    public (string Label, TimeSpan Ago)? LastHotKey
    {
        get
        {
            var label = _lastHotKey;
            var at = Interlocked.Read(ref _lastHotKeyAt);

            return label is null || at == 0
                ? null
                : (label, Stopwatch.GetElapsedTime(at, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>Gets a value indicating whether a run is being recorded right now.</summary>
    public bool Recording => _recording;

    /// <summary>Gets the run recorded so far.</summary>
    public IReadOnlyList<RunEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts or stops recording.
    /// </summary>
    /// <returns>True when recording is now running.</returns>
    public bool Toggle()
    {
        if (_recording)
        {
            _recording = false;
            _journal.Enabled = false;
            _logger.LogInformation("Run recording stopped: {Count} event(s).", Events.Count);
            Changed?.Invoke();
            return false;
        }

        lock (_sync)
        {
            _events.Clear();
        }

        _lastMap = int.MinValue;
        _lastTarget = null;
        _lastMonsters = -1;
        _startedAt = Stopwatch.GetTimestamp();
        _journal.Clear();
        _journal.Enabled = true;
        _recording = true;

        _logger.LogInformation
        (
            "Run recording started. Play the run by hand; every key and click you send to the game " +
            "is recorded with what the server reported at that moment. {Key} stops it.",
            Key
        );

        Changed?.Invoke();
        return true;
    }

    /// <summary>Discards the recording.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _events.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Writes the recording next to the configuration.
    /// </summary>
    /// <param name="directory">Where to write, defaulting to the working directory.</param>
    /// <returns>The path written, or null with the reason.</returns>
    public (string? Path, string? Error) Save(string? directory = null)
    {
        var events = Events;

        if (events.Count == 0)
        {
            return (null, "rien à enregistrer");
        }

        var path = Path.Combine
        (
            directory ?? Directory.GetCurrentDirectory(),
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        );

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(events, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));

            _logger.LogInformation("Run saved to {Path}: {Count} event(s).", path, events.Count);
            return (path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, ex.Message);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Unavailable("recording a run reads the mouse and keyboard, which is Windows only");
            return;
        }

        if (_target.Process is not { } process)
        {
            Unavailable("no game process is bound; start in --pcap mode");
            return;
        }

        var (window, className) = GameWindowFinder.Find(process.Id, process.MainWindowHandle);

        if (window == IntPtr.Zero)
        {
            Unavailable($"process {process.ProcessName} (pid {process.Id}) exposes no usable window");
            return;
        }

        Available = true;
        Changed?.Invoke();

        _logger.LogInformation
        (
            "Run recorder ready on window 0x{Handle:X} (\"{Class}\"). Press {Key} to start and stop.",
            window.ToInt64(),
            className,
            Key
        );

        var down = new Dictionary<int, bool>();
        var hotKeys = new Dictionary<int, bool>();
        var toggleWas = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Every offered key, not only the bound one. A key that never arrives and a key
                // whose action is broken look the same from outside, and this is what tells them
                // apart without a debugger on the operator's machine.
                foreach (var choice in HotKey.Choices)
                {
                    var pressed = IsDown(choice.VirtualKey);
                    hotKeys.TryGetValue(choice.VirtualKey, out var wasPressed);
                    hotKeys[choice.VirtualKey] = pressed;

                    if (pressed && !wasPressed)
                    {
                        _lastHotKey = choice.Label;
                        Interlocked.Exchange(ref _lastHotKeyAt, Stopwatch.GetTimestamp());
                        _logger.LogInformation("Hotkey seen: {Key}.", choice.Label);
                        Changed?.Invoke();
                    }
                }

                // Read every pass rather than once at startup: the key is a setting, and one
                // changed because it collided with the game has to take effect there and then.
                var toggle = IsDown(HotKey.Resolve(_options.RecordRunKey).VirtualKey);
                if (toggle && !toggleWas)
                {
                    Toggle();
                }

                toggleWas = toggle;

                if (_recording)
                {
                    DrainJournal();
                    SampleState();
                    SampleInput(window, down);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SampleInput(IntPtr window, Dictionary<int, bool> down)
    {
        // Only what was aimed at the game. Keys typed into the bot's own window are not part of the
        // run, and neither are clicks on it.
        var foreground = GetForegroundWindow();
        if (foreground != window && GetAncestor(foreground, GaRoot) != window)
        {
            return;
        }

        foreach (var key in WatchedKeys)
        {
            var isDown = IsDown(key);
            down.TryGetValue(key, out var was);
            down[key] = isDown;

            if (!isDown || was)
            {
                continue;
            }

            Record(RunEventKind.Key, Describe(key), null, null);
        }

        foreach (var button in new[] { VkLButton, VkRButton })
        {
            var isDown = IsDown(button);
            down.TryGetValue(button, out var was);
            down[button] = isDown;

            if (!isDown || was || !GetCursorPos(out var cursor) || !ScreenToClient(window, ref cursor))
            {
                continue;
            }

            if (!GetClientRect(window, out var rect)
                || cursor.X < 0 || cursor.Y < 0 || cursor.X > rect.Right || cursor.Y > rect.Bottom)
            {
                continue;
            }

            Record(RunEventKind.Click, button == VkLButton ? "clic gauche" : "clic droit", cursor.X, cursor.Y);
        }
    }

    private void DrainJournal()
    {
        foreach (var note in _journal.Drain())
        {
            Record(RunEventKind.State, note, null, null);
        }
    }

    private void SampleState()
    {
        var map = _state.CurrentMapId;
        var target = _state.Target?.EntityId;
        var monsters = CountMonsters();

        if (map != _lastMap)
        {
            Record(RunEventKind.State, _lastMap == int.MinValue ? $"carte {map}" : $"changement de carte -> {map}", null, null);
            _lastMap = map;
        }

        if (target != _lastTarget)
        {
            Record(RunEventKind.State, target is { } id ? $"cible #{id}" : "plus de cible", null, null);
            _lastTarget = target;
        }

        // Only worth a line when the count actually moves; a fight changes it constantly by one.
        if (_lastMonsters < 0 || Math.Abs(monsters - _lastMonsters) >= 1)
        {
            if (monsters != _lastMonsters)
            {
                Record(RunEventKind.State, $"{monsters} monstre(s) annoncés", null, null);
                _lastMonsters = monsters;
            }
        }
    }

    private int CountMonsters()
        => _state.KnownEntities.Count(e => e.EntityType == EntityType.Monster && e.HpPercentage > 0);

    private void Record(RunEventKind kind, string what, int? clickX, int? clickY)
    {
        var position = _state.HasPosition ? _state.Position : (Configuration.Waypoint?)null;

        var entry = new RunEvent
        (
            (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds,
            kind,
            what,
            clickX,
            clickY,
            _state.CurrentMapId,
            position?.X,
            position?.Y,
            _state.Target?.EntityId,
            CountMonsters()
        );

        lock (_sync)
        {
            _events.Add(entry);
        }

        _logger.LogDebug("RUN | {Summary}", entry.Summary);
        Changed?.Invoke();
    }

    private void Unavailable(string reason)
    {
        UnavailableReason = reason;
        Available = false;
        _logger.LogWarning("Run recording is unavailable: {Reason}. The bot keeps running.", reason);
        Changed?.Invoke();
    }

    private static string Describe(int virtualKey)
        => virtualKey switch
        {
            0x0D => "Entrée",
            0x1B => "Échap",
            0x20 => "espace",
            _ => ((char)virtualKey).ToString()
        };

    private static int[] BuildWatchedKeys()
    {
        var keys = new List<int> { 0x0D, 0x1B, 0x20 };

        for (var c = '0'; c <= '9'; c++)
        {
            keys.Add(c);
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add(c);
        }

        return keys.ToArray();
    }

    private static bool IsDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
