using Microsoft.Extensions.Logging;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Reports what would be pressed without pressing anything.
/// </summary>
/// <remarks>
/// The safe default while the loop is being tuned. Every decision is visible and nothing reaches the
/// game, so the behaviour can be judged during ordinary play before the bot is given the keyboard.
/// </remarks>
public sealed class DryRunGameInput : IGameInput
{
    private readonly ILogger<DryRunGameInput> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DryRunGameInput"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DryRunGameInput(ILogger<DryRunGameInput> logger)
        => _logger = logger;

    /// <inheritdoc />
    public string Description => "dry run (decisions are logged, nothing is sent to the game)";

    /// <inheritdoc />
    public bool TryAttach(out string error)
    {
        error = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public bool PressKey(GameKey key)
    {
        _logger.LogInformation("[WOULD PRESS] {Key}", key.Label);
        return true;
    }

    /// <inheritdoc />
    public bool ClickAt(int x, int y)
    {
        _logger.LogInformation("[WOULD CLICK] ({X},{Y}) in the game window", x, y);
        return true;
    }
}
