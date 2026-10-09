namespace NosSmoothCustomClient.Configuration;

/// <summary>What a startup step does.</summary>
public enum StartupAction
{
    /// <summary>Presses a key.</summary>
    Key,

    /// <summary>Clicks a point of the game window.</summary>
    Click
}

/// <summary>
/// One step of the sequence that starts an instance, and what has to be true before it is played.
/// </summary>
/// <param name="Name">What the step is, for the window and the log.</param>
/// <param name="Action">Whether it is a key or a click.</param>
/// <param name="Key">The key to press, for a key step.</param>
/// <param name="X">X inside the game window, for a click.</param>
/// <param name="Y">Y inside the game window, for a click.</param>
/// <param name="DoubleClick">Whether the click has to be a double one.</param>
/// <param name="WaitBeforeMs">A pause held before the step, whatever else is true.</param>
/// <param name="UntilMap">A map to be on before the step is played.</param>
/// <param name="UntilX">An X to have reached before the step is played.</param>
/// <param name="UntilY">A Y to have reached before the step is played.</param>
/// <param name="TimeoutMs">How long to wait for that before playing it anyway.</param>
/// <remarks>
/// A launch is a conversation with panels that no packet describes: sit, stand, the mission window,
/// START, a walk across a map, a portal prompt, Enter. Nothing in the protocol announces any of it,
/// so the steps are recorded by hand and played back - but not on a stopwatch. A sequence replayed
/// on timings alone desynchronises on the first slow load and every later step lands in a world it
/// was not recorded in, so each step carries what it was waiting for: the map that has to have
/// loaded, the spot that has to have been reached. The recorded delay is the floor and the fallback,
/// never the whole plan.
/// </remarks>
public readonly record struct StartupStep
(
    string Name,
    StartupAction Action,
    string? Key = null,
    int X = 0,
    int Y = 0,
    bool DoubleClick = false,
    int WaitBeforeMs = 500,
    int? UntilMap = null,
    int? UntilX = null,
    int? UntilY = null,
    int TimeoutMs = 20000
)
{
    /// <summary>Gets what has to be true first, in words, or null when nothing has to be.</summary>
    public string? Condition => UntilMap is { } map
        ? $"carte {map}"
        : UntilX is { } x && UntilY is { } y
            ? $"position ({x},{y})"
            : null;

    /// <inheritdoc />
    public override string ToString()
    {
        var what = Action == StartupAction.Key
            ? $"touche {Key}"
            : DoubleClick ? $"double-clic ({X},{Y})" : $"clic ({X},{Y})";

        var after = Condition is { } condition ? $" après {condition}" : string.Empty;
        var pause = WaitBeforeMs > 0 ? $" (+{WaitBeforeMs / 1000.0:0.#}s)" : string.Empty;

        return $"{Name} : {what}{after}{pause}";
    }
}
