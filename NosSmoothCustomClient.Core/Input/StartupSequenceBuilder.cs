using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Turns a run played by hand into the sequence that replays it.
/// </summary>
/// <remarks>
/// The recorder keeps actions and consequences on one clock, which is what makes this possible: for
/// every key and click it also knows the map and the place the character was on when it happened.
/// So the conversion does not have to guess what each step was waiting for - it can read it. A step
/// played on a map the previous one was not on waits for that map; a step played somewhere the
/// character had to walk to waits for that spot. The recorded gap is kept as a floor, and as the
/// timeout after which the step is played regardless.
///
/// Only left clicks survive the conversion: the bot has one mouse button, and a right click that
/// silently became a left one would open the wrong thing at the worst moment.
/// </remarks>
public static class StartupSequenceBuilder
{
    // Two clicks this close together, in time and on screen, were one double click.
    private const long DoubleClickWindowMs = 450;
    private const int DoubleClickSlopPx = 6;

    // A pause is a floor, not the plan: a step that also waits for a map or a place gets a short
    // one, since the condition is what it is really waiting for.
    private const int MaxPauseWithoutCondition = 15000;
    private const int MaxPauseWithCondition = 2500;

    // Far enough that the character was walked there rather than nudged by the client.
    private const int WalkedDistance = 4;

    /// <summary>
    /// Converts a recorded run into launch steps.
    /// </summary>
    /// <param name="events">The recorded run.</param>
    /// <returns>The steps, in order.</returns>
    public static IReadOnlyList<StartupStep> FromRun(IReadOnlyList<RunEvent> events)
    {
        var actions = events
            .Where(e => e.Kind == RunEventKind.Key
                        || (e.Kind == RunEventKind.Click && e.What == "clic gauche" && e.ClickX is not null))
            .ToList();

        var steps = new List<StartupStep>();
        RunEvent? previous = null;

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];

            // A double click arrives as two clicks a few milliseconds apart, and replaying it as two
            // single ones is how a "Draw item" box stays shut.
            var isDouble = action.Kind == RunEventKind.Click
                           && i + 1 < actions.Count
                           && IsSameClick(action, actions[i + 1]);

            var gap = previous is { } before ? Math.Max(0, action.At - before.At) : 0;

            var (untilMap, untilX, untilY) = Condition(previous, action);
            var hasCondition = untilMap is not null || untilX is not null;
            var pause = (int)Math.Min(gap, hasCondition ? MaxPauseWithCondition : MaxPauseWithoutCondition);
            var timeout = (int)Math.Max(15000, Math.Min(gap * 2, 120000));

            steps.Add(action.Kind == RunEventKind.Key
                ? new StartupStep
                (
                    $"touche {action.What}",
                    StartupAction.Key,
                    Key: action.What,
                    WaitBeforeMs: pause,
                    UntilMap: untilMap,
                    UntilX: untilX,
                    UntilY: untilY,
                    TimeoutMs: timeout
                )
                : new StartupStep
                (
                    isDouble ? "double-clic" : "clic",
                    StartupAction.Click,
                    X: action.ClickX ?? 0,
                    Y: action.ClickY ?? 0,
                    DoubleClick: isDouble,
                    WaitBeforeMs: pause,
                    UntilMap: untilMap,
                    UntilX: untilX,
                    UntilY: untilY,
                    TimeoutMs: timeout
                ));

            previous = action;

            if (isDouble)
            {
                // The second half of the double click is consumed, not replayed.
                previous = actions[i + 1];
                i++;
            }
        }

        return steps;
    }

    private static bool IsSameClick(RunEvent first, RunEvent second)
        => second.Kind == RunEventKind.Click
           && second.What == first.What
           && second.At - first.At <= DoubleClickWindowMs
           && Math.Abs((second.ClickX ?? 0) - (first.ClickX ?? 0)) <= DoubleClickSlopPx
           && Math.Abs((second.ClickY ?? 0) - (first.ClickY ?? 0)) <= DoubleClickSlopPx;

    private static (int? Map, int? X, int? Y) Condition(RunEvent? previous, RunEvent action)
    {
        if (previous is not { } before)
        {
            return (null, null, null);
        }

        // The map first: it is the one condition the protocol states outright, and a load is what a
        // fixed delay gets wrong the moment the server is busy.
        if (action.MapId != before.MapId && action.MapId >= 0)
        {
            return (action.MapId, null, null);
        }

        if (before.X is { } fromX && before.Y is { } fromY && action.X is { } toX && action.Y is { } toY)
        {
            var distance = Math.Max(Math.Abs(toX - fromX), Math.Abs(toY - fromY));

            if (distance >= WalkedDistance)
            {
                return (null, toX, toY);
            }
        }

        return (null, null, null);
    }
}
