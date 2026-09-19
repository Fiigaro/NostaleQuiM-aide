using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Diagnostics;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Routes input either to the game or to the log, switchable while running.
/// </summary>
/// <remarks>
/// Making this a runtime switch rather than a launch flag is what turns it into a kill switch: if
/// the bot starts doing something unwanted, one click stops it reaching the client without tearing
/// the session down or losing the packet stream. It also means the same run can be watched in dry
/// run and then handed the keyboard, with no restart in between.
/// </remarks>
public sealed class SwitchableGameInput : IGameInput
{
    private readonly WindowsGameInput _live;
    private readonly DryRunGameInput _dryRun;
    private readonly ILogger<SwitchableGameInput> _logger;

    private volatile bool _liveMode;
    private bool _liveAttached;
    private bool _clickModePinned;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwitchableGameInput"/> class.
    /// </summary>
    /// <param name="target">The captured game process.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="logger">The logger.</param>
    public SwitchableGameInput(CaptureTarget target, ILoggerFactory loggerFactory, ILogger<SwitchableGameInput> logger)
    {
        _live = new WindowsGameInput(target, loggerFactory.CreateLogger<WindowsGameInput>());
        _dryRun = new DryRunGameInput(loggerFactory.CreateLogger<DryRunGameInput>());
        _logger = logger;
    }

    /// <summary>Raised when the mode changes, with true meaning keystrokes reach the game.</summary>
    public event Action<bool>? ModeChanged;

    /// <summary>Gets a value indicating whether the live path is usable at all.</summary>
    public bool CanGoLive => _liveAttached;

    /// <summary>Gets a value indicating whether keystrokes currently reach the game.</summary>
    public bool IsLive => _liveMode;

    /// <summary>Gets how minimap clicks are currently delivered.</summary>
    public MinimapClickMode ClickMode => _live.ClickMode;

    /// <summary>
    /// Sends every later click to this window, and stops using the real cursor.
    /// </summary>
    /// <param name="target">The window found to act on posted clicks.</param>
    /// <param name="className">Its class, for the log.</param>
    /// <remarks>
    /// Finding a window that listens is what makes posted clicks usable, and posted clicks are what
    /// let several clients be driven at once - the real pointer is one, and three bots sharing it
    /// have to take turns with the game in front.
    /// </remarks>
    public void UseClickWindow(IntPtr target, string className)
    {
        _live.ClickWindow = target;
        _live.ClickMode = MinimapClickMode.Posted;
        _clickModePinned = true;

        _logger.LogInformation
        (
            "Minimap clicks now go to window 0x{Handle:X} (\"{Class}\") as posted messages. " +
            "The real cursor is no longer needed, so the game can stay in the background.",
            target.ToInt64(),
            className
        );
    }

    /// <summary>Lists the windows a posted click could go to.</summary>
    public IReadOnlyList<(IntPtr Handle, string ClassName, int Depth)> ClickCandidates()
        => _live.ClickCandidates();

    /// <summary>
    /// Posts a click to one candidate window, bypassing the current click mode.
    /// </summary>
    /// <param name="target">The window to try.</param>
    /// <param name="x">X in the bound window's client area.</param>
    /// <param name="y">Y in the bound window's client area.</param>
    /// <returns>True when the messages were queued.</returns>
    /// <remarks>
    /// Deliberately not routed through the mode: the whole point is to find out whether some window
    /// of this client does accept posted clicks, which the real cursor would hide by working
    /// regardless. Queued is not accepted, so only watching the character answers it.
    /// </remarks>
    public bool ProbeClick(IntPtr target, int x, int y)
        => _live.PostClickTo(target, x, y);

    /// <summary>
    /// Fixes how clicks are sent, so the bot will not change it on its own.
    /// </summary>
    /// <param name="mode">The mode to use.</param>
    public void PinClickMode(MinimapClickMode mode)
    {
        _clickModePinned = true;
        _live.ClickMode = mode;
        _logger.LogInformation("Minimap clicks pinned to {Mode} by configuration.", mode);
    }

    /// <summary>
    /// Switches to a way of clicking the client cannot ignore.
    /// </summary>
    /// <returns>A description of what changed, or null when there is nothing left to try.</returns>
    /// <remarks>
    /// Posted mouse messages report success as soon as they are queued, so a client that ignores
    /// them looks exactly like one that is obeying. The only evidence either way is whether the
    /// character moved, which the decision loop has and this layer does not - so escalation is
    /// driven from there rather than guessed at here.
    /// </remarks>
    public string? EscalateClickMode()
    {
        if (_clickModePinned)
        {
            _logger.LogWarning
            (
                "Minimap clicks are not moving the character, but MinimapClickMode is pinned to " +
                "{Mode} in appsettings.json, so nothing else will be tried.",
                _live.ClickMode
            );

            return null;
        }

        if (_live.ClickMode == MinimapClickMode.RealCursor)
        {
            return null;
        }

        _live.ClickMode = MinimapClickMode.RealCursor;

        _logger.LogWarning
        (
            "Posted minimap clicks are not moving the character, so they are being sent with the " +
            "real mouse pointer instead. The cursor will jump briefly each time the bot walks, and " +
            "the game window has to be visible at that point. Set MinimapClickMode to \"Posted\" in " +
            "appsettings.json to forbid this."
        );

        return "clic avec le vrai curseur";
    }

    /// <inheritdoc />
    public string Description
        => _liveMode ? _live.Description : _dryRun.Description;

    /// <inheritdoc />
    public bool TryAttach(out string error)
    {
        _dryRun.TryAttach(out _);
        _liveAttached = _live.TryAttach(out var liveError);

        if (!_liveAttached)
        {
            // Not fatal: the run is still useful for watching decisions, it simply cannot act.
            _logger.LogWarning("Live input unavailable, dry run only: {Error}", liveError);
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Switches between acting and only logging.
    /// </summary>
    /// <param name="live">True to let keystrokes reach the game.</param>
    /// <returns>The mode actually in effect.</returns>
    public bool SetLive(bool live)
    {
        if (live && !_liveAttached)
        {
            _logger.LogWarning("Cannot go live: no game window is bound.");
            live = false;
        }

        if (_liveMode == live)
        {
            return _liveMode;
        }

        _liveMode = live;
        _logger.LogWarning(live
            ? "LIVE: keystrokes now reach the game."
            : "DRY RUN: keystrokes are only logged.");

        ModeChanged?.Invoke(live);
        return _liveMode;
    }

    /// <summary>Reads the game window size, when one is bound.</summary>
    /// <param name="width">The client width.</param>
    /// <param name="height">The client height.</param>
    /// <returns>True when the size could be read.</returns>
    public bool TryGetClientSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        return _liveAttached && _live.TryGetClientSize(out width, out height);
    }

    /// <inheritdoc />
    public bool PressKey(GameKey key)
        => Current.PressKey(key);

    /// <inheritdoc />
    public bool ClickAt(int x, int y)
        => Current.ClickAt(x, y);

    private IGameInput Current
        => _liveMode ? _live : _dryRun;
}
