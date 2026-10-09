using System.Text.Json.Serialization;

namespace NosSmoothCustomClient.Input;

/// <summary>What a recorded event is.</summary>
public enum RunEventKind
{
    /// <summary>A key was pressed.</summary>
    Key,

    /// <summary>A mouse button was pressed inside the game window.</summary>
    Click,

    /// <summary>Something the server reported changed on its own.</summary>
    State
}

/// <summary>
/// One moment of a recorded run: what was done, and what the world looked like when it was.
/// </summary>
/// <param name="At">Milliseconds since recording started.</param>
/// <param name="Kind">Whether this is an action or a consequence.</param>
/// <param name="What">The key, the button, or the change.</param>
/// <param name="ClickX">X inside the game window, for a click.</param>
/// <param name="ClickY">Y inside the game window, for a click.</param>
/// <param name="MapId">The map at that moment, or -1.</param>
/// <param name="X">The character's X, when known.</param>
/// <param name="Y">The character's Y, when known.</param>
/// <param name="TargetId">The locked entity, or null.</param>
/// <param name="Monsters">How many live monsters the server had announced.</param>
/// <remarks>
/// Actions and consequences go in one list on one clock on purpose. A video shows the first and a
/// packet log the second, and neither alone can say which click opened which door. Replaying by
/// timing alone desynchronises on the first slow load; replaying each action once its recorded
/// consequence has actually arrived does not.
/// </remarks>
public readonly record struct RunEvent
(
    long At,
    RunEventKind Kind,
    string What,
    int? ClickX,
    int? ClickY,
    int MapId,
    int? X,
    int? Y,
    long? TargetId,
    int Monsters
)
{
    /// <summary>Gets a one-line rendering, for the window and the log.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var where = ClickX is { } cx && ClickY is { } cy ? $" ({cx},{cy})" : string.Empty;
            var at = X is { } x && Y is { } y ? $"  carte {MapId} ({x},{y})" : $"  carte {MapId}";
            return $"{At / 1000.0,7:0.0}s  {Kind,-5} {What}{where}{at}  mobs={Monsters}";
        }
    }
}
