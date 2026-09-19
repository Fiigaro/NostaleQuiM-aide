using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Records a patrol route by watching where the player points on the minimap.
/// </summary>
/// <remarks>
/// A waypoint needs two unrelated things: where it is on the map, so the loop can tell when the
/// character has arrived, and where to click to go there. The first comes from the packet stream and
/// is exact; the second can only come from the player, because nothing in the protocol says where
/// the minimap is drawn.
///
/// Both are captured in one gesture. Stand on the spot, point at yourself on the minimap, press F9.
/// The click point is stored relative to the game window, so moving or resizing it later does not
/// invalidate the route.
/// </remarks>
public sealed class WaypointRecorder : BackgroundService
{
    private const int VkF9 = 0x78;
    private const int VkF10 = 0x79;

    private readonly CaptureTarget _target;
    private readonly ProtocolStateManager _state;
    private readonly BotOptions _options;
    private readonly ILogger<WaypointRecorder> _logger;

    private readonly List<Waypoint> _recorded = new();
    private readonly object _sync = new();

    private volatile bool _armed;
    private volatile bool _available;
    private DateTimeOffset _nextDisarmedWarning = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaypointRecorder"/> class.
    /// </summary>
    /// <param name="target">The captured game process.</param>
    /// <param name="state">The state manager, for the character's position.</param>
    /// <param name="options">The bot options the route is written into.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="startArmed">Whether recording begins armed.</param>
    public WaypointRecorder
    (
        CaptureTarget target,
        ProtocolStateManager state,
        BotOptions options,
        ILogger<WaypointRecorder> logger,
        StartArmed? startArmed = null
    )
    {
        _armed = startArmed?.Value ?? false;
        _target = target;
        _state = state;
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised whenever the recorded route changes, or the recorder becomes usable.</summary>
    public event Action? Changed;

    /// <summary>Gets a value indicating whether a game window was found, so F9 can do anything.</summary>
    public bool Available
    {
        get => _available;
        private set
        {
            _available = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets the reason recording is unavailable, when it is.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Gets or sets a value indicating whether F9 records a waypoint.</summary>
    /// <remarks>
    /// Off by default: F9 is an ordinary key in the game, and capturing on it unprompted would turn
    /// a normal keypress into a silent edit of the route.
    /// </remarks>
    public bool Armed
    {
        get => _armed;
        set
        {
            if (_armed == value)
            {
                return;
            }

            _armed = value;
            _logger.LogInformation(value
                ? "Waypoint recording ARMED: stand on a spot, point at it on the minimap, press F9."
                : "Waypoint recording disarmed.");
        }
    }

    /// <summary>Gets the route recorded so far.</summary>
    public IReadOnlyList<Waypoint> Recorded
    {
        get
        {
            lock (_sync)
            {
                return _recorded.ToArray();
            }
        }
    }

    /// <summary>Discards the recorded route.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _recorded.Clear();
        }

        _logger.LogInformation("Recorded route cleared.");
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies the recorded route to the live options and writes it to the override file.
    /// </summary>
    /// <returns>The path written, or null with the reason.</returns>
    public (string? Path, string? Error) Save()
    {
        lock (_sync)
        {
            if (_recorded.Count == 0)
            {
                return (null, "no waypoint recorded");
            }

            _options.Waypoints = _recorded.ToList();

            // Stamp the map. The click points are positions on this minimap and mean nothing on
            // another one, so the route carries where it belongs and navigation stays put elsewhere.
            _options.RouteMapId = _state.CurrentMapId >= 0 ? _state.CurrentMapId : null;
        }

        _logger.LogInformation
        (
            "Route saved: {Count} point(s) on map {MapId}.",
            _options.Waypoints.Count,
            _options.RouteMapId?.ToString() ?? "unknown"
        );

        return LocalConfigurationWriter.Save(_options);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recording is an optional convenience, so every way it can fail ends in a warning and a
        // recorder that reports itself unavailable - never in stopping the host. Taking the whole
        // bot down because a route cannot be recorded is a far worse outcome than not recording one.
        if (!OperatingSystem.IsWindows())
        {
            Unavailable("recording a route reads the mouse position, which is Windows only");
            return;
        }

        if (_target.Process is not { } process)
        {
            Unavailable("no game process is bound; start in --pcap mode");
            return;
        }

        // The same window the keystrokes go to, found the same way. MainWindowHandle is a different
        // window on this client, so trusting it would record pixels measured against something the
        // minimap is not even drawn on.
        var (window, className) = GameWindowFinder.Find(process.Id, process.MainWindowHandle);

        if (window == IntPtr.Zero)
        {
            Unavailable($"process {process.ProcessName} (pid {process.Id}) exposes no usable window");
            return;
        }

        Available = true;

        _logger.LogInformation
        (
            "Waypoint recorder ready on window 0x{Handle:X} (class \"{Class}\"). Arm it in the window " +
            "(or start with --record-waypoints), then: stand on a spot, point at it on the minimap, " +
            "press F9. F10 saves the route.",
            window.ToInt64(),
            className
        );

        var f9WasDown = false;
        var f10WasDown = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var f9 = IsDown(VkF9);
                var f10 = IsDown(VkF10);

                // Edge detection: a held key must record one waypoint, not fifty.
                if (f9 && !f9WasDown)
                {
                    if (_armed)
                    {
                        Capture(window);
                    }
                    else
                    {
                        // Silence here reads as a broken feature. F9 is an ordinary game key, so it
                        // stays inert until armed - but saying so once beats saying nothing.
                        WarnDisarmed("F9");
                    }
                }

                if (f10 && !f10WasDown)
                {
                    if (_armed)
                    {
                        Finish();
                    }
                    else
                    {
                        WarnDisarmed("F10");
                    }
                }

                f9WasDown = f9;
                f10WasDown = f10;

                await Task.Delay(TimeSpan.FromMilliseconds(40), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with Ctrl+C: keep whatever was recorded rather than discarding it.
            if (_recorded.Count > 0)
            {
                Finish();
            }
        }
    }

    private void Capture(IntPtr window)
    {
        if (!GetCursorPos(out var cursor))
        {
            _logger.LogWarning("Could not read the mouse position; nothing recorded.");
            return;
        }

        var point = cursor;
        if (!ScreenToClient(window, ref point))
        {
            _logger.LogWarning("Could not translate the mouse into the game window; nothing recorded.");
            return;
        }

        if (!GetClientRect(window, out var rect) || point.X < 0 || point.Y < 0 || point.X > rect.Right || point.Y > rect.Bottom)
        {
            _logger.LogWarning
            (
                "The mouse is outside the game window ({X},{Y}); point at the minimap inside NosTale.",
                point.X,
                point.Y
            );

            return;
        }

        var position = _state.Position;
        var waypoint = new Waypoint(position.X, position.Y, point.X, point.Y);

        lock (_sync)
        {
            _recorded.Add(waypoint);
        }

        Changed?.Invoke();

        _logger.LogInformation
        (
            "Waypoint {Number} recorded: map {Map}, minimap click at ({ClickX},{ClickY}).",
            Recorded.Count,
            waypoint,
            point.X,
            point.Y
        );

        if (position is { X: 0, Y: 0 })
        {
            _logger.LogWarning
            (
                "The character's position is still unknown, so this waypoint cannot be checked for " +
                "arrival. Move about until the log shows a position, then record it again."
            );
        }
    }

    private void Finish()
    {
        var (path, error) = Save();

        if (path is null)
        {
            _logger.LogWarning("Route not saved: {Error}", error);
            return;
        }

        var route = Recorded;
        _logger.LogInformation
        (
            "{Count} waypoint(s) saved to {Path}: {Route}",
            route.Count,
            path,
            string.Join(" -> ", route.Select(w => w.ToString()))
        );
    }

    private void Unavailable(string reason)
    {
        UnavailableReason = reason;
        Available = false;
        _logger.LogWarning("Waypoint recording is unavailable: {Reason}. The bot keeps running.", reason);
    }

    private void WarnDisarmed(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextDisarmedWarning)
        {
            return;
        }

        _nextDisarmedWarning = now.AddSeconds(5);
        _logger.LogWarning
        (
            "{Key} pressed, but waypoint recording is not armed - nothing was recorded. " +
            "Click \"Armer l'enregistrement (F9)\" in the window first.",
            key
        );
    }

    private static bool IsDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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
