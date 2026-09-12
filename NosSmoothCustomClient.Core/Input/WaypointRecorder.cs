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
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WaypointRecorder> _logger;

    private readonly List<Waypoint> _recorded = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WaypointRecorder"/> class.
    /// </summary>
    /// <param name="target">The captured game process.</param>
    /// <param name="state">The state manager, for the character's position.</param>
    /// <param name="options">The bot options the route is written into.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public WaypointRecorder
    (
        CaptureTarget target,
        ProtocolStateManager state,
        BotOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<WaypointRecorder> logger
    )
    {
        _target = target;
        _state = state;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogError("Recording a route reads the mouse position and is Windows only.");
            _lifetime.StopApplication();
            return;
        }

        if (_target.Process is not { } process || process.MainWindowHandle == IntPtr.Zero)
        {
            _logger.LogError("No game window to record against.");
            _lifetime.StopApplication();
            return;
        }

        var window = process.MainWindowHandle;

        _logger.LogWarning("ROUTE RECORDING");
        _logger.LogInformation("  1. Walk your character to a spot on your farming route.");
        _logger.LogInformation("  2. Point the mouse at that same spot on the minimap.");
        _logger.LogInformation("  3. Press F9. Repeat for each waypoint, in the order to walk them.");
        _logger.LogInformation("  4. Press F10 when the route is complete.");

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
                    Capture(window);
                }

                if (f10 && !f10WasDown)
                {
                    Finish();
                    _lifetime.StopApplication();
                    return;
                }

                f9WasDown = f9;
                f10WasDown = f10;

                await Task.Delay(TimeSpan.FromMilliseconds(40), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with Ctrl+C: keep whatever was recorded rather than discarding it.
            Finish();
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
        _recorded.Add(waypoint);

        _logger.LogInformation
        (
            "Waypoint {Number} recorded: map {Map}, minimap click at ({ClickX},{ClickY}).",
            _recorded.Count,
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
        if (_recorded.Count == 0)
        {
            _logger.LogWarning("No waypoint was recorded; nothing written.");
            return;
        }

        _options.Waypoints = _recorded.ToList();

        var (path, error) = LocalConfigurationWriter.Save(_options);
        if (path is null)
        {
            _logger.LogError("Could not save the route: {Error}", error);
            return;
        }

        _logger.LogInformation
        (
            "{Count} waypoint(s) saved to {Path}: {Route}",
            _recorded.Count,
            path,
            string.Join(" -> ", _recorded.Select(w => w.ToString()))
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
