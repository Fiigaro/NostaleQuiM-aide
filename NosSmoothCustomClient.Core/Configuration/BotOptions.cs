using NosSmooth.Packets.Enums.Inventory;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// A single grid waypoint on the current map.
/// </summary>
/// <param name="X">The X coordinate on the map, as the server reports it.</param>
/// <param name="Y">The Y coordinate on the map.</param>
/// <param name="ClickX">X offset of the matching point on the minimap, inside the game window.</param>
/// <param name="ClickY">Y offset of the matching point on the minimap.</param>
/// <remarks>
/// Two coordinate systems, each doing a job the other cannot. The map coordinates come from the
/// packet stream and are what tells the loop whether the character has arrived. The click point is
/// where to press on the minimap to go there - and the minimap is the one part of the screen that
/// does not scroll with the character, which is what makes a recorded pixel still correct an hour
/// later.
/// </remarks>
public readonly record struct Waypoint(int X, int Y, int? ClickX = null, int? ClickY = null)
{
    /// <summary>Gets a value indicating whether this waypoint can be reached by clicking.</summary>
    public bool IsClickable => ClickX is not null && ClickY is not null;

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
    public IList<SkillDefinition> Skills { get; set; } = new List<SkillDefinition>
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
    /// Gets or sets how often the attack key is pressed while no target is confirmed.
    /// </summary>
    /// <remarks>
    /// Driving the client by keyboard, the attack key is also the question "is there anything to
    /// fight?" - it selects the nearest monster when there is one and does nothing when there is
    /// not. Asking is cheap, so this only needs to be slow enough not to spam the client.
    /// </remarks>
    public TimeSpan SearchInterval { get; set; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Gets or sets how long a skill waits for the server to confirm it actually went off.
    /// </summary>
    /// <remarks>
    /// Pressing a key is not casting. The client refuses a skill with nothing selected, out of
    /// range, or still on the game's own cooldown, and says nothing about it. So a press only
    /// reserves the skill for this long; the real cooldown starts when <c>su</c> confirms the cast.
    /// If no confirmation arrives the skill goes back to ready, because nothing happened.
    /// </remarks>
    public TimeSpan SkillConfirmationWindow { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Gets or sets how long a target may go unmentioned by the server before it is dropped.
    /// </summary>
    /// <remarks>
    /// A fight produces a steady stream of <c>su</c> and <c>st</c>. Silence means the target is
    /// gone - out of range, lost, or killed by someone else - and holding the lock would keep the
    /// bot swinging at nothing instead of looking for the next monster.
    /// </remarks>
    public TimeSpan TargetStaleAfter { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Gets or sets the movement speed written into the outbound walk packet.
    /// </summary>
    public short WalkSpeed { get; set; } = 11;

    /// <summary>
    /// Gets or sets how many cells a single walk packet may cover. NosTale rejects long jumps.
    /// </summary>
    public int MaxStepDistance { get; set; } = 3;

    /// <summary>
    /// Gets or sets the map the patrol route was recorded on, or null when it is not tied to one.
    /// </summary>
    /// <remarks>
    /// A route is a list of minimap click points, and a minimap belongs to a map. Walking one
    /// recorded elsewhere means clicking arbitrary spots - harmless while grinding, much less so in
    /// a raid, where the next room is a different map and the clicks land on whatever is there.
    /// Recording a route stores the map it was recorded on, and navigation then holds off anywhere
    /// else. Clear it to walk the route on any map.
    /// </remarks>
    public int? RouteMapId { get; set; }

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
    /// Gets or sets which key does what on the quick bar.
    /// </summary>
    public KeyBindings Keys { get; set; } = new();

    /// <summary>
    /// Gets or sets the buffs kept up on the character.
    /// </summary>
    public IList<BuffDefinition> Buffs { get; set; } = new List<BuffDefinition>();

    /// <summary>
    /// Gets or sets how early a buff may be refreshed when out of combat.
    /// </summary>
    public TimeSpan BuffRefreshMargin { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the grid path the character loops over while no target is alive.
    /// </summary>
    public IList<Waypoint> Waypoints { get; set; } = new List<Waypoint>
    {
        new Waypoint(50, 50),
        new Waypoint(70, 50),
        new Waypoint(70, 70)
    };
}
