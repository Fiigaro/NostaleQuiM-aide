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
