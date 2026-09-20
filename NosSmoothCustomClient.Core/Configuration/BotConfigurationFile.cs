using Microsoft.Extensions.Configuration;
using NosSmooth.Packets.Enums.Inventory;

using NosSmoothCustomClient.Input;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// The shape of the <c>appsettings.json</c> "Bot" section.
/// </summary>
/// <remarks>
/// Deliberately a mutable DTO rather than binding straight onto <see cref="BotOptions"/>: the
/// domain types are positional records and a readonly record struct, which the configuration binder
/// handles inconsistently. Converting explicitly also means a bad file produces a clear error
/// instead of silently leaving defaults in place - and calibration is exactly the phase where a
/// setting that quietly did not apply costs the most time.
/// </remarks>
public sealed class BotConfigurationFile
{
    /// <summary>Gets or sets the HP ratio at or below which a consumable is used.</summary>
    public double? HpPotionThreshold { get; set; }

    /// <summary>Gets or sets the MP ratio at or below which a consumable is used.</summary>
    public double? MpPotionThreshold { get; set; }

    /// <summary>Gets or sets the bag holding the consumables.</summary>
    public string? PotionBag { get; set; }

    /// <summary>Gets or sets the inventory slot of the HP consumable.</summary>
    public long? HpPotionSlot { get; set; }

    /// <summary>Gets or sets the inventory slot of the MP consumable.</summary>
    public long? MpPotionSlot { get; set; }

    /// <summary>Gets or sets the seconds between two uses of the same consumable.</summary>
    public double? PotionCooldownSeconds { get; set; }

    /// <summary>Gets or sets the cast id used when no rotation skill is ready.</summary>
    public short? BasicAttackCastId { get; set; }

    /// <summary>Gets or sets the milliseconds between two attack frames.</summary>
    public double? AttackIntervalMs { get; set; }

    /// <summary>Gets or sets the milliseconds between two decisions.</summary>
    public double? TickIntervalMs { get; set; }

    /// <summary>Gets or sets the distance within which the attack lands.</summary>
    public int? AttackRange { get; set; }

    /// <summary>Gets or sets whether to close the gap to an out-of-range target.</summary>
    public bool? ApproachTargetOutOfRange { get; set; }

    /// <summary>Gets or sets the radius within which a monster is engaged.</summary>
    public int? EngagementRadius { get; set; }

    /// <summary>Gets or sets how many cells one movement frame may cover.</summary>
    public int? MaxStepDistance { get; set; }

    /// <summary>Gets or sets the distance at which a waypoint counts as reached.</summary>
    public int? WaypointArrivalRadius { get; set; }

    /// <summary>Gets or sets the speed written into outbound movement frames.</summary>
    public short? WalkSpeed { get; set; }

    /// <summary>Gets or sets the patrol path.</summary>
    public List<WaypointEntry>? Waypoints { get; set; }

    /// <summary>Gets or sets the map the patrol route was recorded on.</summary>
    public int? RouteMapId { get; set; }

    /// <summary>Gets or sets how minimap clicks are delivered ("Auto", "Posted" or "RealCursor").</summary>
    public string? MinimapClickMode { get; set; }

    /// <summary>Gets or sets whether the bot runs an instance room rather than a patrol.</summary>
    public bool? InstanceMode { get; set; }

    /// <summary>Gets or sets which waypoint is the way out of the room.</summary>
    public int? ExitWaypoint { get; set; }

    /// <summary>Gets or sets the clicks that collect the reward, in order.</summary>
    public List<UiPointEntry>? RewardSequence { get; set; }

    /// <summary>Gets or sets how long to wait for the reward panel to appear.</summary>
    public double? RewardDelaySeconds { get; set; }

    /// <summary>Gets or sets the recorded steps that open the instance.</summary>
    public List<StartupStepEntry>? StartupSequence { get; set; }

    /// <summary>Gets or sets whether the launch sequence plays when the bot is started.</summary>
    public bool? AutoLaunchInstance { get; set; }

    /// <summary>Gets or sets the attack rotation, in priority order.</summary>
    public List<SkillEntry>? Skills { get; set; }

    /// <summary>Gets or sets the buffs kept up on the character.</summary>
    public List<BuffEntry>? Buffs { get; set; }

    /// <summary>Gets or sets how early a buff may be refreshed, in seconds.</summary>
    public double? BuffRefreshMarginSeconds { get; set; }

    /// <summary>Gets or sets which key does what on the quick bar.</summary>
    public KeyBindings? Keys { get; set; }

    /// <summary>One waypoint.</summary>
    /// <summary>One recorded place to click in the game's interface.</summary>
    public sealed class UiPointEntry
    {
        /// <summary>Gets or sets what it is.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets X inside the game window.</summary>
        public int X { get; set; }

        /// <summary>Gets or sets Y inside the game window.</summary>
        public int Y { get; set; }

        /// <summary>Gets or sets whether it takes two clicks.</summary>
        public bool DoubleClick { get; set; }

        /// <summary>Gets or sets how long to wait afterwards.</summary>
        public int WaitAfterMs { get; set; } = 800;
    }

    /// <summary>One recorded step of the launch sequence.</summary>
    public sealed class StartupStepEntry
    {
        /// <summary>Gets or sets what the step is.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets whether it is a key or a click.</summary>
        public string? Action { get; set; }

        /// <summary>Gets or sets the key, for a key step.</summary>
        public string? Key { get; set; }

        /// <summary>Gets or sets X inside the game window.</summary>
        public int X { get; set; }

        /// <summary>Gets or sets Y inside the game window.</summary>
        public int Y { get; set; }

        /// <summary>Gets or sets whether the click is a double one.</summary>
        public bool DoubleClick { get; set; }

        /// <summary>Gets or sets the pause held before the step.</summary>
        public int WaitBeforeMs { get; set; }

        /// <summary>Gets or sets the map that has to have loaded first.</summary>
        public int? UntilMap { get; set; }

        /// <summary>Gets or sets the X that has to have been reached first.</summary>
        public int? UntilX { get; set; }

        /// <summary>Gets or sets the Y that has to have been reached first.</summary>
        public int? UntilY { get; set; }

        /// <summary>Gets or sets how long to wait for that before playing it anyway.</summary>
        public int TimeoutMs { get; set; } = 20000;
    }

    public sealed class WaypointEntry
    {
        /// <summary>Gets or sets the X coordinate.</summary>
        public int X { get; set; }

        /// <summary>Gets or sets the Y coordinate.</summary>
        public int Y { get; set; }

        /// <summary>Gets or sets the X offset of the matching minimap point in the game window.</summary>
        public int? ClickX { get; set; }

        /// <summary>Gets or sets the Y offset of the matching minimap point.</summary>
        public int? ClickY { get; set; }
    }

    /// <summary>One rotation entry.</summary>
    public sealed class SkillEntry
    {
        /// <summary>Gets or sets the cast id, i.e. the position in the skill bar.</summary>
        public short CastId { get; set; }

        /// <summary>Gets or sets the display name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the MP cost.</summary>
        public long MpCost { get; set; }

        /// <summary>Gets or sets the cooldown in seconds.</summary>
        public double CooldownSeconds { get; set; }

        /// <summary>Gets or sets whether the skill takes part in the rotation.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the quick bar key that casts it.</summary>
        public string? Key { get; set; }
    }

    /// <summary>One maintained buff.</summary>
    public sealed class BuffEntry
    {
        /// <summary>Gets or sets the display name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets how long it lasts, in seconds.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>Gets or sets how soon it can be applied again, in seconds.</summary>
        public double CooldownSeconds { get; set; }

        /// <summary>Gets or sets the skill's position in the bar, when it is a skill.</summary>
        public short? CastId { get; set; }

        /// <summary>Gets or sets the bag the item lives in, when it is an item.</summary>
        public string? ItemBag { get; set; }

        /// <summary>Gets or sets the item's slot, when it is an item.</summary>
        public long? ItemSlot { get; set; }

        /// <summary>Gets or sets the buff card the server reports, when known.</summary>
        public int? CardId { get; set; }

        /// <summary>Gets or sets whether the buff is maintained.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the quick bar key that applies it.</summary>
        public string? Key { get; set; }
    }

    /// <summary>
    /// Reads the "Bot" section over a set of defaults.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The resulting options, and a description of what was applied.</returns>
    public static (BotOptions Options, string Summary) Apply(IConfiguration configuration)
    {
        var options = new BotOptions();
        var section = configuration.GetSection("Bot");

        if (!section.Exists())
        {
            return (options, "no \"Bot\" section found, using built-in defaults");
        }

        var file = section.Get<BotConfigurationFile>();
        if (file is null)
        {
            return (options, "the \"Bot\" section could not be read, using built-in defaults");
        }

        var applied = new List<string>();

        Set(file.HpPotionThreshold, v => options.HpPotionThreshold = v, "HpPotionThreshold", applied);
        Set(file.MpPotionThreshold, v => options.MpPotionThreshold = v, "MpPotionThreshold", applied);
        Set(file.HpPotionSlot, v => options.HpPotionSlot = v, "HpPotionSlot", applied);
        Set(file.MpPotionSlot, v => options.MpPotionSlot = v, "MpPotionSlot", applied);
        Set(file.BasicAttackCastId, v => options.BasicAttackCastId = v, "BasicAttackCastId", applied);
        Set(file.AttackRange, v => options.AttackRange = v, "AttackRange", applied);
        Set(file.ApproachTargetOutOfRange, v => options.ApproachTargetOutOfRange = v, "ApproachTargetOutOfRange", applied);
        Set(file.EngagementRadius, v => options.EngagementRadius = v, "EngagementRadius", applied);
        Set(file.MaxStepDistance, v => options.MaxStepDistance = v, "MaxStepDistance", applied);
        Set(file.WaypointArrivalRadius, v => options.WaypointArrivalRadius = v, "WaypointArrivalRadius", applied);
        Set(file.RouteMapId, v => options.RouteMapId = v, "RouteMapId", applied);
        Set(file.InstanceMode, v => options.InstanceMode = v, "InstanceMode", applied);
        Set(file.ExitWaypoint, v => options.ExitWaypoint = v, "ExitWaypoint", applied);
        Set(file.RewardDelaySeconds, v => options.RewardDelay = TimeSpan.FromSeconds(v), "RewardDelay", applied);

        if (file.RewardSequence is { Count: > 0 } sequence)
        {
            options.RewardSequence = sequence
                .Select(p => new UiPoint(p.Name ?? "point", p.X, p.Y, p.DoubleClick, p.WaitAfterMs))
                .ToList();

            applied.Add("RewardSequence");
        }

        Set(file.AutoLaunchInstance, v => options.AutoLaunchInstance = v, "AutoLaunchInstance", applied);

        if (file.StartupSequence is { Count: > 0 } launch)
        {
            options.StartupSequence = launch
                .Select(s => new StartupStep
                (
                    s.Name ?? "étape",
                    Enum.TryParse<StartupAction>(s.Action, true, out var action) ? action : StartupAction.Click,
                    s.Key,
                    s.X,
                    s.Y,
                    s.DoubleClick,
                    s.WaitBeforeMs,
                    s.UntilMap,
                    s.UntilX,
                    s.UntilY,
                    s.TimeoutMs
                ))
                .ToList();

            applied.Add("StartupSequence");
        }

        if (Enum.TryParse<MinimapClickMode>(file.MinimapClickMode, true, out var clickMode))
        {
            options.MinimapClickMode = clickMode;
            applied.Add("MinimapClickMode");
        }
        Set(file.WalkSpeed, v => options.WalkSpeed = v, "WalkSpeed", applied);
        Set(file.PotionCooldownSeconds, v => options.PotionCooldown = TimeSpan.FromSeconds(v), "PotionCooldown", applied);
        Set(file.AttackIntervalMs, v => options.AttackInterval = TimeSpan.FromMilliseconds(v), "AttackInterval", applied);
        Set(file.TickIntervalMs, v => options.TickInterval = TimeSpan.FromMilliseconds(v), "TickInterval", applied);

        if (!string.IsNullOrWhiteSpace(file.PotionBag) && Enum.TryParse<BagType>(file.PotionBag, true, out var bag))
        {
            options.PotionBag = bag;
            applied.Add("PotionBag");
        }

        if (file.Waypoints is { Count: > 0 })
        {
            options.Waypoints = file.Waypoints.Select(w => new Waypoint(w.X, w.Y, w.ClickX, w.ClickY)).ToList();
            applied.Add($"{file.Waypoints.Count} waypoint(s), {options.Waypoints.Count(w => w.IsClickable)} clickable");
        }

        if (file.Skills is { Count: > 0 })
        {
            options.Skills = file.Skills
                .Select(s => new SkillDefinition
                (
                    s.CastId,
                    string.IsNullOrWhiteSpace(s.Name) ? $"cast {s.CastId}" : s.Name,
                    s.MpCost,
                    TimeSpan.FromSeconds(s.CooldownSeconds),
                    s.Enabled,
                    s.Key
                ))
                .ToList();

            applied.Add($"{file.Skills.Count} skill(s)");
        }

        Set(file.BuffRefreshMarginSeconds, v => options.BuffRefreshMargin = TimeSpan.FromSeconds(v), "BuffRefreshMargin", applied);

        if (file.Keys is { } keys)
        {
            options.Keys = keys;
            applied.Add($"key bindings ({keys.PressDelay.TotalMilliseconds:0}ms between presses)");
        }

        if (file.Buffs is { Count: > 0 })
        {
            options.Buffs = file.Buffs
                .Select(b => new BuffDefinition
                (
                    string.IsNullOrWhiteSpace(b.Name) ? "buff" : b.Name,
                    TimeSpan.FromSeconds(b.DurationSeconds),
                    TimeSpan.FromSeconds(b.CooldownSeconds),
                    b.CastId,
                    !string.IsNullOrWhiteSpace(b.ItemBag) && Enum.TryParse<BagType>(b.ItemBag, true, out var buffBag)
                        ? buffBag
                        : null,
                    b.ItemSlot,
                    b.CardId,
                    b.Enabled,
                    b.Key
                ))
                .ToList();

            var usable = options.Buffs.Count(b => b.IsUsable);
            applied.Add($"{file.Buffs.Count} buff(s), {usable} usable");
        }

        return applied.Count == 0
            ? (options, "the \"Bot\" section is empty, using built-in defaults")
            : (options, "applied " + string.Join(", ", applied));
    }

    private static void Set<T>(T? value, Action<T> assign, string name, ICollection<string> applied)
        where T : struct
    {
        if (value is not { } resolved)
        {
            return;
        }

        assign(resolved);
        applied.Add(name);
    }
}
