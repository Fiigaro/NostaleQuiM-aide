using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Everything the bot can actually do, named by intent rather than by mechanism.
/// </summary>
/// <remarks>
/// The decision loop decides <i>what</i> should happen; an actuator decides <i>how</i>. Keeping that
/// line sharp is what lets the same, tested logic drive a synthesised client through packets and a
/// real one through keystrokes, and what lets a dry run be a substitution rather than a flag checked
/// in a dozen places.
/// </remarks>
public interface IBotActuator
{
    /// <summary>Gets a short description of how actions are delivered.</summary>
    string Description { get; }

    /// <summary>
    /// Gets a value indicating whether this actuator can walk the character cell by cell.
    /// </summary>
    /// <remarks>
    /// Packets can place a step anywhere; a keyboard cannot. The client walks into range on its own
    /// when told to attack, so the loop simply skips the approach rather than pretending.
    /// </remarks>
    bool SupportsApproach { get; }

    /// <summary>
    /// Gets a value indicating whether the game picks the target itself.
    /// </summary>
    /// <remarks>
    /// True for the keyboard, where the attack key selects the nearest monster: the client can see
    /// the whole screen, so asking it is strictly better than scanning our own entity table, which
    /// only knows about monsters whose spawn packet we happened to capture. False when the target
    /// has to be named by id in a packet, where the scan is the only way to know what to name.
    /// </remarks>
    bool SelectsTargetItself { get; }

    /// <summary>
    /// Gets a value indicating whether one movement order walks the whole path.
    /// </summary>
    /// <remarks>
    /// A minimap click is a destination: the client walks there on its own and re-issuing the order
    /// every tick only restarts it, which is what makes a bot stutter in place instead of
    /// travelling. A walk packet is one step, so it has to be repeated. The loop has to know which
    /// of the two it is holding, or it either stutters or never moves.
    /// </remarks>
    bool WalkIsSustained { get; }

    /// <summary>
    /// Tries a different way of moving, after an order that was accepted but changed nothing.
    /// </summary>
    /// <param name="what">What changed, when something did.</param>
    /// <returns>True when there was something left to try.</returns>
    /// <remarks>
    /// Some ways of talking to a client report success for a message the client then ignores. Only
    /// the caller watching the character knows that happened, so the decision to try something else
    /// is made there and carried out here.
    /// </remarks>
    bool TryAnotherWayToMove(out string what);

    /// <summary>
    /// Plays a recorded run of interface clicks, in order.
    /// </summary>
    /// <param name="points">The points to click.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when every point was clicked.</returns>
    /// <remarks>
    /// The one part of a sequence the server never announces. A reward panel is drawn over the
    /// window and mentioned by nothing, so these are played blind and judged by what follows.
    /// </remarks>
    Task<bool> ClickSequenceAsync(IReadOnlyList<UiPoint> points, CancellationToken ct = default);

    /// <summary>Presses one named key, whatever it is bound to elsewhere.</summary>
    /// <param name="key">The key, as written in a recorded sequence.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the key was accepted by the window.</returns>
    /// <remarks>
    /// The quick bar is described by the configuration; a launch sequence is not. Sitting down to
    /// wake an instance up, or answering a portal prompt with Enter, are keys that belong to one
    /// recorded sequence and to nothing else, so they are named by the step rather than bound.
    /// </remarks>
    Task<bool> PressKeyAsync(string key, CancellationToken ct = default);

    /// <summary>Takes one step towards a cell.</summary>
    /// <param name="x">Destination X.</param>
    /// <param name="y">Destination Y.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> ApproachAsync(int x, int y, CancellationToken ct = default);

    /// <summary>Prepares the actuator.</summary>
    /// <param name="error">Why it is not usable, when it is not.</param>
    /// <returns>True when ready.</returns>
    bool TryPrepare(out string error);

    /// <summary>Drinks an HP consumable.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> UseHpPotionAsync(CancellationToken ct = default);

    /// <summary>Drinks an MP consumable.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> UseMpPotionAsync(CancellationToken ct = default);

    /// <summary>Applies a buff.</summary>
    /// <param name="buff">The buff to apply.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> ApplyBuffAsync(BuffDefinition buff, CancellationToken ct = default);

    /// <summary>Selects the nearest monster and begins attacking it.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> TargetNearestAsync(CancellationToken ct = default);

    /// <summary>Casts an attack skill at the current target.</summary>
    /// <param name="skill">The skill to cast, or null for the basic attack.</param>
    /// <param name="targetEntityId">The target, for actuators that address it explicitly.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> CastSkillAsync(SkillDefinition? skill, long targetEntityId, CancellationToken ct = default);

    /// <summary>Picks up nearby loot.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> LootAsync(CancellationToken ct = default);

    /// <summary>Heads for a waypoint.</summary>
    /// <param name="index">The waypoint's index in the configured path.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the action was issued.</returns>
    Task<bool> GoToWaypointAsync(int index, CancellationToken ct = default);
}
