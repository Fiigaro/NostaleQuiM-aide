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
    /// Gets or sets the inventory slot of the HP consumable, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Null disables HP consumables outright. A slot number that happens to hold something else
    /// would otherwise be used over and over, which is worse than not healing at all.
    /// </remarks>
    public long? HpPotionSlot { get; set; }

    /// <summary>
    /// Gets or sets the inventory slot of the MP consumable, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Null disables MP consumables outright. Not every character carries them.
    /// </remarks>
    public long? MpPotionSlot { get; set; }

    /// <summary>
    /// Gets or sets the minimum delay between two consumable uses of the same kind.
    /// </summary>
    public TimeSpan PotionCooldown { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets the cast id of the basic attack, used whenever no rotation skill is ready.
    /// </summary>
    public short BasicAttackCastId { get; set; }

    /// <summary>
    /// Gets or sets the attack rotation, in priority order.
    /// </summary>
    /// <remarks>
    /// The loop casts the first entry that is off cooldown and affordable; when none qualifies it
    /// falls back to <see cref="BasicAttackCastId"/>. Cast ids, MP costs and cooldowns are
    /// placeholders - they must be set to the character's actual skill bar.
    /// </remarks>
    public IReadOnlyList<SkillDefinition> Skills { get; set; } = new[]
    {
        new SkillDefinition(3, "Sort lourd",   60, TimeSpan.FromSeconds(12)),
        new SkillDefinition(2, "Sort moyen",   35, TimeSpan.FromSeconds(8)),
        new SkillDefinition(1, "Sort rapide",  20, TimeSpan.FromSeconds(4)),
        new SkillDefinition(4, "Sort d'appoint", 15, TimeSpan.FromSeconds(3))
    };

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
    /// Gets or sets the buffs kept up on the character.
    /// </summary>
    public IReadOnlyList<BuffDefinition> Buffs { get; set; } = Array.Empty<BuffDefinition>();

    /// <summary>
    /// Gets or sets how early a buff may be refreshed when out of combat.
    /// </summary>
    public TimeSpan BuffRefreshMargin { get; set; } = TimeSpan.FromSeconds(60);

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
