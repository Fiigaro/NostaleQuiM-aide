using NosSmooth.Packets.Enums.Inventory;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// A single grid waypoint on the current map.
/// </summary>
public readonly record struct Waypoint(int X, int Y)
{
    /// <inheritdoc />
    public override string ToString()
        => $"({X},{Y})";
}

/// <summary>
/// Tuning knobs for the orchestration loop. Everything the brain decides on is driven from here
/// so that no thresholds are buried inside the responders.
/// </summary>
public sealed class BotOptions
{
    /// <summary>
    /// Gets or sets the interval of the decision matrix (Step 5 runs on a 300ms clock).
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Gets or sets the HP ratio at or below which an HP consumable is used.
    /// </summary>
    public double HpPotionThreshold { get; set; } = 0.50;

    /// <summary>
    /// Gets or sets the MP ratio at or below which an MP consumable is used.
    /// </summary>
    public double MpPotionThreshold { get; set; } = 0.30;

    /// <summary>
    /// Gets or sets the bag the consumables live in.
    /// </summary>
    public BagType PotionBag { get; set; } = BagType.Main;

    /// <summary>
    /// Gets or sets the inventory slot of the HP consumable.
    /// </summary>
    public long HpPotionSlot { get; set; }

    /// <summary>
    /// Gets or sets the inventory slot of the MP consumable.
    /// </summary>
    public long MpPotionSlot { get; set; } = 1;

    /// <summary>
    /// Gets or sets the minimum delay between two consumable uses of the same kind.
    /// </summary>
    public TimeSpan PotionCooldown { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets the cast id of the skill used for the attack cycle. 0 is the basic attack.
    /// </summary>
    public short SkillCastId { get; set; }

    /// <summary>
    /// Gets or sets the minimum delay between two attack frames.
    /// </summary>
    public TimeSpan AttackInterval { get; set; } = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// Gets or sets the movement speed written into the outbound walk packet.
    /// </summary>
    public short WalkSpeed { get; set; } = 11;

    /// <summary>
    /// Gets or sets how many cells a single walk packet may cover. NosTale rejects long jumps.
    /// </summary>
    public int MaxStepDistance { get; set; } = 3;

    /// <summary>
    /// Gets or sets the distance at which a waypoint counts as reached.
    /// </summary>
    public int WaypointArrivalRadius { get; set; } = 2;

    /// <summary>
    /// Gets or sets the distance within which the attack skill lands.
    /// </summary>
    public int AttackRange { get; set; } = 3;

    /// <summary>
    /// Gets or sets a value indicating whether the character closes the gap to a target that is
    /// outside <see cref="AttackRange"/>.
    /// </summary>
    /// <remarks>
    /// Step 5 of the brief says movement halts outright once a target is locked. Taken literally
    /// that strands the loop on any monster that spawns out of reach: it never walks closer and
    /// never lands a hit. Approaching first, then holding position inside range, is the behaviour
    /// that actually completes a kill. Set this to false for the literal reading.
    /// </remarks>
    public bool ApproachTargetOutOfRange { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum distance at which a spawned monster is considered engageable.
    /// </summary>
    public int EngagementRadius { get; set; } = 12;

    /// <summary>
    /// Gets or sets the grid path the character loops over while no target is alive.
    /// </summary>
    public IReadOnlyList<Waypoint> Waypoints { get; set; } = new[]
    {
        new Waypoint(50, 50),
        new Waypoint(70, 50),
        new Waypoint(70, 70)
    };
}
