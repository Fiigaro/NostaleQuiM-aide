using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Orchestration;

/// <summary>
/// The decision matrix. Runs on a fixed clock and resolves exactly one priority per tick.
/// </summary>
/// <remarks>
/// This is the only place that decides what should happen, and it says so in terms of intent -
/// "drink a potion", "go to waypoint 2" - never in terms of packets or keystrokes. How an intent is
/// carried out belongs to the actuator, which is what lets the same tested logic drive a simulated
/// client through packets and a real one through the keyboard.
///
/// The responders are edge triggered: they act on the packet that just arrived. This loop is level
/// triggered: it looks at the state those responders accumulated and keeps acting while a condition
/// holds. Both go through the same cooldown gates, so nothing is dispatched twice for one event.
/// </remarks>
public sealed class OrchestrationBackgroundService : BackgroundService
{
    private readonly ProtocolStateManager _state;
    private readonly IBotActuator _actuator;
    private readonly SkillRotation _rotation;
    private readonly BuffTracker _buffs;
    private readonly BotController _controller;
    private readonly BotOptions _options;
    private readonly ILogger<OrchestrationBackgroundService> _logger;

    private int _lastLoggedPriority = -1;

    // The journey in progress, for a movement order that walks the whole path by itself.
    private int _walkingTo = -1;
    private Waypoint _walkedFrom;
    private DateTimeOffset _walkedAt;
    private DateTimeOffset _nextRefusalWarning = DateTimeOffset.MinValue;
    private int _stalls;

    // Once per room. Cleared on the next map, since that is a new run of the same sequence.
    private bool _rewardTaken;
    private DateTimeOffset? _rewardDueAt;
    private int _rewardMap = int.MinValue;

    // A destination that is not a waypoint, so the journey bookkeeping can tell it from one.
    private const int MonsterDestination = -2;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestrationBackgroundService"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="actuator">How intents are carried out.</param>
    /// <param name="rotation">The attack rotation.</param>
    /// <param name="buffs">The buff tracker.</param>
    /// <param name="controller">The run/pause switch.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public OrchestrationBackgroundService
    (
        ProtocolStateManager state,
        IBotActuator actuator,
        SkillRotation rotation,
        BuffTracker buffs,
        BotController controller,
        BotOptions options,
        ILogger<OrchestrationBackgroundService> logger
    )
    {
        _state = state;
        _actuator = actuator;
        _rotation = rotation;
        _buffs = buffs;
        _controller = controller;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation
        (
            "Orchestration loop started on a {Interval}ms clock, acting through {Actuator}.",
            _options.TickInterval.TotalMilliseconds,
            _actuator.Description
        );

        using var timer = new PeriodicTimer(_options.TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single bad tick must never take the loop down.
                _logger.LogError(ex, "Orchestration tick failed; continuing.");
            }
        }

        _logger.LogInformation("Orchestration loop stopped. Final state: {State}", _state.Describe());
    }

    /// <summary>
    /// Resolves exactly one priority.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the decision has been dispatched.</returns>
    /// <remarks>
    /// Public so a test can drive the decision matrix one step at a time. Running the service and
    /// watching what comes out would test the same logic through a clock, which turns assertions
    /// about behaviour into assertions about timing.
    /// </remarks>
    public async Task TickAsync(CancellationToken ct)
    {
        // A new map is a new room, and the reward panel that belongs to the last one is gone.
        if (_rewardMap != _state.CurrentMapId)
        {
            _rewardMap = _state.CurrentMapId;
            _rewardTaken = false;
            _rewardDueAt = null;
        }

        if (!_controller.IsRunning)
        {
            // Paused: the packet pipeline keeps running, so the loop resumes on fresh state.
            LastDecision = "en pause";
            return;
        }

        // Serialize the whole decision so two ticks can never interleave their actions.
        using var cycle = await _state.EnterCycleAsync(ct).ConfigureAwait(false);

        if (await TrySurviveAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        if (await TryMaintainBuffsAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        if (await TryEngageAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        await NavigateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Priority 1 - survival. Drinks while a pool sits below its threshold.
    /// </summary>
    private async Task<bool> TrySurviveAsync(CancellationToken ct)
    {
        var acted = false;

        if (_state.IsHpCritical && _state.TryTakeHpPotionGate(_options.PotionCooldown))
        {
            LogPriority(1, "survival: HP at {0:P0}", _state.HpRatio);
            acted |= await _actuator.UseHpPotionAsync(ct).ConfigureAwait(false);
        }

        if (_state.IsMpCritical && _state.TryTakeMpPotionGate(_options.PotionCooldown))
        {
            LogPriority(1, "survival: MP at {0:P0}", _state.MpRatio);
            acted |= await _actuator.UseMpPotionAsync(ct).ConfigureAwait(false);
        }

        return acted;
    }

    /// <summary>
    /// Priority 2 - buffs. Reapplies anything that has lapsed, and tops up what is close to
    /// lapsing while there is a lull.
    /// </summary>
    private async Task<bool> TryMaintainBuffsAsync(CancellationToken ct)
    {
        if (_buffs.SelectNext(_state.HasLiveTarget) is not { } due)
        {
            return false;
        }

        var (buff, lapsed) = due;

        LogPriority(2, "buffs: {0} {1}", buff.Name, lapsed ? "has lapsed, reapplying" : "is about to lapse, topping up");

        if (!await _actuator.ApplyBuffAsync(buff, ct).ConfigureAwait(false))
        {
            return false;
        }

        _buffs.MarkCast(buff);
        return true;
    }

    /// <summary>
    /// Priority 3 - engagement. Holds position and works the rotation while a target is alive.
    /// </summary>
    private async Task<bool> TryEngageAsync(CancellationToken ct)
    {
        // A lock can outlive the fight in silence - the monster wanders off, someone else kills it,
        // or the selection never really happened. Dropping a target the server has stopped talking
        // about is what stops the bot swinging at nothing.
        if (_state.TargetWentQuiet(_options.TargetStaleAfter))
        {
            _logger.LogInformation
            (
                "Target #{EntityId} has gone quiet for over {Seconds:0.#}s; dropping the lock.",
                _state.TargetEntityId,
                _options.TargetStaleAfter.TotalSeconds
            );

            _state.ClearTarget();
        }

        // Told there is nothing left, rather than deciding it. In a room whose monsters were wiped
        // in one packet, probing for a target is asking a question already answered - and the one
        // thing left to do is leave.
        if (_options.InstanceMode && _state.RoomCleared)
        {
            return false;
        }

        return _actuator.SelectsTargetItself
            ? await EngageByKeyAsync(ct).ConfigureAwait(false)
            : await EngageByPacketAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Engagement when the game does the targeting: the attack key both asks and acts.
    /// </summary>
    /// <remarks>
    /// This deliberately does not consult our own entity table. That table is built from the spawn
    /// packets we captured, so every monster already on screen when the capture started is invisible
    /// to us and perfectly visible to the player - gating the attack on it means standing next to a
    /// monster doing nothing. The client knows what is there; pressing the key asks it.
    ///
    /// It also means skills are only ever pressed during a fight the server has confirmed, which is
    /// what stops a refused cast from starting a cooldown on a skill that never went off.
    /// </remarks>
    private async Task<bool> EngageByKeyAsync(CancellationToken ct)
    {
        if (_state.Target is not { IsAlive: true } target)
        {
            if (!_state.TryTakeSearchGate(_options.SearchInterval))
            {
                // Between probes there is nothing to fight and nothing to ask, so the tick belongs
                // to navigation.
                return false;
            }

            LogPriority(3, "engagement: no target, asking the game with the attack key");
            await _actuator.TargetNearestAsync(ct).ConfigureAwait(false);

            // Claim the tick rather than falling through to navigation. The answer to the probe
            // arrives as a packet a moment later, and clicking the minimap in the meantime would
            // walk away from the monster we just told the character to attack.
            return true;
        }

        if (!_state.TryTakeAttackGate(_options.AttackInterval))
        {
            return true;
        }

        // The attack key on every frame, unconditionally. It used to be the fallback for a tick
        // where no skill was ready - which, with a rotation that nearly always has one, meant it
        // was never pressed at all. That is backwards: on this client the attack key is what keeps
        // the character swinging and closing distance, and the skills are what go on top of it.
        // Played by hand the same fight is forty-one presses of it against six of anything else.
        await _actuator.CastSkillAsync(null, target.EntityId, ct).ConfigureAwait(false);

        // A skill alongside it, when one is ready and nothing is still awaiting its answer. Holding
        // only the skills is deliberate: a plain swing carries no cooldown of its own, so it cannot
        // be mistaken for the confirmation of the cast we are waiting on.
        var skill = _rotation.PendingCastId is null ? _rotation.SelectNext(_state.CurrentMp) : null;

        LogPriority
        (
            3,
            "engagement: #{0} at {1}% HP -> attaque{2}",
            target.EntityId,
            target.HpPercentage,
            skill is null ? string.Empty : " + " + skill.Name
        );

        if (skill is not null && await _actuator.CastSkillAsync(skill, target.EntityId, ct).ConfigureAwait(false))
        {
            // Pressed, not cast. The full cooldown waits for the server to confirm it went off.
            _rotation.MarkPressed(skill);
        }

        // Claiming the tick while a target is alive starves navigation completely, and in a room
        // full of monsters a target is always alive - so the bot attacked forever and never moved
        // an inch. Holding one is not headway: the attack key only reaches what is already close,
        // so the cluster within reach dies and the rest of the room waits for a character that has
        // no reason left to walk. Yielding the tick once the fight stops getting anywhere is what
        // sends it to the next cluster.
        if (_state.FightStalled(_options.RepositionAfter))
        {
            Decide(3, "engagement: plus rien ne tombe ici depuis {0:0}s - on se replace", _options.RepositionAfter.TotalSeconds);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Engagement when the target has to be named in a packet, so it has to be found first.
    /// </summary>
    private async Task<bool> EngageByPacketAsync(CancellationToken ct)
    {
        var justAcquired = false;

        if (_state.Target is not { } target)
        {
            if (_state.FindNearestMonster() is not { } candidate)
            {
                return false;
            }

            justAcquired = _state.AcquireTarget(candidate.EntityId, candidate.EntityType, candidate.HpPercentage);
            _logger.LogInformation("Scan picked up monster #{EntityId} at ({X},{Y}).", candidate.EntityId, candidate.X, candidate.Y);
            target = _state.Target!.Value;
        }

        if (!target.IsAlive)
        {
            _state.ClearTarget(target.EntityId);
            return false;
        }

        // Close the gap only where the actuator can place a step. Driving the client by keyboard, the
        // game walks into range by itself once told to attack, so there is nothing to do here.
        if (_actuator.SupportsApproach
            && _options.ApproachTargetOutOfRange
            && _state.TryGetEntity(target.EntityId, out var tracked))
        {
            var position = _state.Position;
            var range = ProtocolStateManager.Distance(position.X, position.Y, tracked.X, tracked.Y);

            if (range > _options.AttackRange)
            {
                if (!_state.TryTakeWalkGate(_options.TickInterval))
                {
                    return true;
                }

                LogPriority(3, "engagement: closing on #{0}, {1} cells away (range {2})", target.EntityId, range, _options.AttackRange);
                await _actuator.ApproachAsync(tracked.X, tracked.Y, ct).ConfigureAwait(false);
                return true;
            }
        }

        if (!_state.TryTakeAttackGate(_options.AttackInterval))
        {
            return true;
        }

        // Select only when the target actually changed. Re-issuing it every frame would keep
        // re-selecting whatever is nearest, which mid-fight can drag the character onto a different
        // monster. Actuators that address the target explicitly treat this as a no-op.
        if (justAcquired)
        {
            await _actuator.TargetNearestAsync(ct).ConfigureAwait(false);
        }

        // Fixed priority: the first rotation entry that is off cooldown and affordable wins,
        // otherwise the basic attack keeps the damage flowing rather than idling the frame.
        var skill = _rotation.SelectNext(_state.CurrentMp);

        // Selecting already started the basic attack, so repeating it in the same breath would be
        // the same key twice for one decision.
        if (skill is null && justAcquired)
        {
            return true;
        }

        LogPriority
        (
            3,
            "engagement: #{0} at {1}% HP -> {2}",
            target.EntityId,
            target.HpPercentage,
            skill?.Name ?? "basic attack"
        );

        if (await _actuator.CastSkillAsync(skill, target.EntityId, ct).ConfigureAwait(false) && skill is not null)
        {
            _rotation.MarkCast(skill);
        }

        return true;
    }

    /// <summary>
    /// Priority 4 - navigation. Heads for the active waypoint and advances once inside the arrival
    /// radius.
    /// </summary>
    private async Task NavigateAsync(CancellationToken ct)
    {
        var waypoints = _options.Waypoints;
        if (waypoints.Count == 0)
        {
            Decide(4, "navigation: aucun waypoint configuré");
            return;
        }

        // A route is minimap click points, and a minimap belongs to a map. Walking one recorded
        // elsewhere is clicking at random - which is merely useless while grinding and actively
        // harmful in a raid, where the next room is another map entirely.
        if (!_options.RouteAppliesOnMap(_state.CurrentMapId))
        {
            Decide
            (
                4,
                "navigation: on map {0}, but the route belongs to map {1} - holding position",
                _state.CurrentMapId,
                _options.RouteMapId
            );

            return;
        }

        // A cleared room has one destination, and staying on it is the point: the way out is a tile
        // to stand on, not a stop on a circuit to be left again on the next tick.
        if (_options.InstanceMode && _state.RoomCleared)
        {
            await HeadForTheExitAsync(ct).ConfigureAwait(false);
            return;
        }

        // Off by default. Walking to whichever monster is nearest looks efficient and destroys the
        // one property a sweep needs - that it goes everywhere.
        if (_options.InstanceMode && _options.ChaseMonsters
            && await TryCloseInOnAMonsterAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        // The round, in order. Each point is held until it stops producing kills, then the next one
        // is taken - which is what makes a room get swept rather than shuttled across.
        if (_options.InstanceMode)
        {
            await SweepRoomAsync(ct).ConfigureAwait(false);
            return;
        }

        var position = _state.Position;
        var waypoint = _state.CurrentWaypoint;
        var distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);

        // Without a position there is no such thing as having arrived, and (0,0) is a real
        // coordinate rather than a way of saying so. Walk to the current waypoint: the character
        // moving is what makes the server report where it is, which is what makes arrival mean
        // anything at all from the next tick on.
        if (!_state.HasPosition)
        {
            Decide(4, "navigation: position encore inconnue, on lance la marche vers {0}", waypoint);
        }
        else if (distance <= _options.WaypointArrivalRadius)
        {
            if (!TryAdvancePastArrivedWaypoints(position, ref waypoint, ref distance))
            {
                return;
            }
        }

        var index = _state.WaypointIndex % waypoints.Count;

        // The starting index is zero, and the exit can be waypoint one. Stepping off it before the
        // room is done is the same mistake as cycling onto it.
        if (_options.InstanceMode && index == _options.ResolveExitWaypoint())
        {
            waypoint = _state.AdvanceWaypoint();
            index = _state.WaypointIndex % waypoints.Count;
            distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);
        }

        if (_actuator.WalkIsSustained && !ShouldReissueWalk(index, position))
        {
            // Already walking there, and getting closer. Saying so again would only restart it.
            Decide(4, "navigation: en route vers {0}, {1} cases restantes", waypoint, distance);
            return;
        }

        if (!_actuator.WalkIsSustained && !_state.TryTakeWalkGate(_options.TickInterval))
        {
            return;
        }

        LogPriority(4, "navigation: at ({0},{1}), heading for {2}, {3} cells away", position.X, position.Y, waypoint, distance);

        if (!await _actuator.GoToWaypointAsync(index, ct).ConfigureAwait(false))
        {
            // The order was refused, so there is no journey to be patient about. Saying so matters:
            // a waypoint with no minimap point can never be walked to, and a bot that stands still
            // reporting "heading for" every tick is indistinguishable from one that is broken.
            ReportWaypointRefused(index, waypoint);
            _walkingTo = -1;
            return;
        }

        _walkingTo = index;
        _walkedFrom = position;
        _walkedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Steps past every waypoint the character is already standing on.
    /// </summary>
    /// <returns>True when a waypoint worth walking to was found.</returns>
    /// <remarks>
    /// One step per tick was not enough, and stopping at a waypoint zero cells away was worse than
    /// not enough: starting a run standing on the first point, the loop advanced once, found the
    /// next point also within reach, and returned having done nothing - every tick, forever. A route
    /// whose points are all in the same place says so once rather than looping in silence.
    /// </remarks>
    private bool TryAdvancePastArrivedWaypoints(Waypoint position, ref Waypoint waypoint, ref int distance)
    {
        var waypoints = _options.Waypoints;

        for (var step = 0; step < waypoints.Count; step++)
        {
            var reached = waypoint;
            waypoint = _state.AdvanceWaypoint();
            distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);

            if (distance > _options.WaypointArrivalRadius)
            {
                _logger.LogInformation
                (
                    "Reached waypoint {Waypoint} (within {Radius} cells); next waypoint is {Next}, {Distance} cells away.",
                    reached,
                    _options.WaypointArrivalRadius,
                    waypoint,
                    distance
                );

                return true;
            }
        }

        // A whole lap without finding anywhere to go.
        Decide
        (
            4,
            "navigation: les {0} waypoints sont tous à moins de {1} cases de ({2},{3}) - la route ne mène nulle part",
            waypoints.Count,
            _options.WaypointArrivalRadius,
            position.X,
            position.Y
        );

        ReportDegenerateRoute(position);
        return false;
    }

    private void ReportDegenerateRoute(Waypoint position)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextRefusalWarning)
        {
            return;
        }

        _nextRefusalWarning = now.AddSeconds(10);

        _logger.LogWarning
        (
            "Every waypoint is within {Radius} cells of ({X},{Y}), so there is nowhere to walk. " +
            "This is what a route recorded before the character's position was known looks like - "
            + "the points all read (0,0). Record it again, moving the character between each F9.",
            _options.WaypointArrivalRadius,
            position.X,
            position.Y
        );
    }

    /// <summary>
    /// Works the round one point at a time: stand, fight, move on when nothing more falls.
    /// </summary>
    /// <remarks>
    /// The patrol logic is wrong for a room. It steps past every point already within the arrival
    /// radius, so points close together are never visited - and it advances on arrival, which means
    /// walking through the round rather than fighting it. Here arriving is not the goal: the point
    /// is held until the fight there stops producing anything, and only then does the next one
    /// become current. Strictly the next one, so a round of three is a round of three.
    /// </remarks>
    private async Task SweepRoomAsync(CancellationToken ct)
    {
        var waypoints = _options.Waypoints;
        var index = _state.WaypointIndex % waypoints.Count;

        // The way out is not part of the round; it is where the round ends.
        if (index == _options.ResolveExitWaypoint())
        {
            _state.AdvanceWaypoint();
            index = _state.WaypointIndex % waypoints.Count;
        }

        var waypoint = waypoints[index];
        var position = _state.Position;
        var distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);
        var arrived = _state.HasPosition && distance <= _options.WaypointArrivalRadius;

        // Just got here: the point deserves its full window before being judged finished, whatever
        // the clock said about the place we walked from.
        if (arrived && _walkingTo == index)
        {
            _state.NoteFightProgress();
            _walkingTo = -1;
            Decide(4, "instance : arrivé au point {0} {1}", index + 1, waypoint);
            return;
        }

        if (arrived && !_state.FightStalled(_options.RepositionAfter))
        {
            Decide(4, "instance : on tient le point {0} {1} tant que ça tombe", index + 1, waypoint);
            return;
        }

        if (arrived)
        {
            var next = _state.AdvanceWaypoint();

            // The clock starts again here, or the next point is judged finished before the character
            // has even set off for it and the whole round is walked through in seconds.
            _state.NoteFightProgress();
            _walkingTo = -1;

            _logger.LogInformation
            (
                "Point {Index} {Waypoint} ne produit plus rien après {Seconds:0.#}s - au suivant, {Next}.",
                index + 1,
                waypoint,
                _options.RepositionAfter.TotalSeconds,
                next
            );

            waypoint = next;
            index = _state.WaypointIndex % waypoints.Count;
            distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);
        }

        if (!ShouldReissueWalk(index, position))
        {
            Decide(4, "instance : en route vers le point {0} {1}, {2} cases", index + 1, waypoint, distance);
            return;
        }

        Decide(4, "instance : direction le point {0} {1}, {2} cases", index + 1, waypoint, distance);

        if (!await _actuator.GoToWaypointAsync(index, ct).ConfigureAwait(false))
        {
            ReportWaypointRefused(index, waypoint);
            _walkingTo = -1;
            return;
        }

        _walkingTo = index;
        _walkedFrom = position;
        _walkedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Plays the recorded clicks that take the reward and close the instance.
    /// </summary>
    /// <remarks>
    /// Blind, and unavoidably so: the panel is drawn over the window and no packet mentions it. So
    /// the sequence waits for the panel to have had time to appear, plays once, and is judged by
    /// what follows - the items arriving, and the map changing back. Once per room, because a second
    /// run would click into whatever is on screen by then.
    /// </remarks>
    private async Task CollectTheRewardAsync(CancellationToken ct)
    {
        if (_options.RewardSequence.Count == 0)
        {
            Decide(4, "instance : salle terminée, sur la sortie - aucun clic de récompense enregistré");
            return;
        }

        if (_rewardTaken)
        {
            Decide(4, "instance : récompense déjà prise, en attente de la sortie");
            return;
        }

        if (_rewardDueAt is null)
        {
            _rewardDueAt = DateTimeOffset.UtcNow + _options.RewardDelay;
            Decide(4, "instance : sur la sortie, on laisse {0:0.#}s au panneau pour s'afficher", _options.RewardDelay.TotalSeconds);
            return;
        }

        if (DateTimeOffset.UtcNow < _rewardDueAt)
        {
            return;
        }

        _rewardTaken = true;
        Decide(4, "instance : récompense - {0} clic(s) enregistré(s)", _options.RewardSequence.Count);

        await _actuator.ClickSequenceAsync(_options.RewardSequence.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks towards the nearest monster the attack key is not reaching.
    /// </summary>
    /// <returns>True when a monster was headed for.</returns>
    /// <remarks>
    /// Standing in the middle of a room does not put every monster in range of a key that selects
    /// the nearest, so a room is cleared by going to them. Where they are is not a guess: every
    /// spawn arrives as a packet carrying its coordinates, and the recorded route measures the
    /// minimap well enough to turn any of those into a click.
    /// </remarks>
    private async Task<bool> TryCloseInOnAMonsterAsync(CancellationToken ct)
    {
        if (!_actuator.SupportsApproach || !_state.HasPosition)
        {
            return false;
        }

        if (_state.FindNearestMonster() is not { } monster)
        {
            return false;
        }

        var position = _state.Position;
        var distance = ProtocolStateManager.Distance(position.X, position.Y, monster.X, monster.Y);

        // Close enough for the attack key to find it by itself: walking would only interrupt.
        if (distance <= _options.AttackRange)
        {
            return false;
        }

        if (!ShouldReissueWalk(MonsterDestination, position))
        {
            Decide(4, "instance : en route vers le monstre #{0} en ({1},{2})", monster.EntityId, monster.X, monster.Y);
            return true;
        }

        Decide(4, "instance : direction le monstre #{0} en ({1},{2}), {3} cases", monster.EntityId, monster.X, monster.Y, distance);

        if (!await _actuator.ApproachAsync(monster.X, monster.Y, ct).ConfigureAwait(false))
        {
            _walkingTo = -1;
            return false;
        }

        _walkingTo = MonsterDestination;
        _walkedFrom = position;
        _walkedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// Walks to the room's way out and stays on it.
    /// </summary>
    private async Task HeadForTheExitAsync(CancellationToken ct)
    {
        var index = _options.ResolveExitWaypoint();

        if (index < 0)
        {
            Decide(4, "instance : salle terminée, mais aucun waypoint de sortie n'est défini");
            return;
        }

        var exit = _options.Waypoints[index];
        var position = _state.Position;
        var distance = ProtocolStateManager.Distance(position.X, position.Y, exit.X, exit.Y);

        if (_state.HasPosition && distance <= _options.WaypointArrivalRadius)
        {
            await CollectTheRewardAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!ShouldReissueWalk(index, position))
        {
            Decide(4, "instance : salle terminée, en route vers la sortie {0}, {1} cases", exit, distance);
            return;
        }

        Decide(4, "instance : salle terminée, direction la sortie {0}, {1} cases", exit, distance);

        if (!await _actuator.GoToWaypointAsync(index, ct).ConfigureAwait(false))
        {
            ReportWaypointRefused(index, exit);
            _walkingTo = -1;
            return;
        }

        _walkingTo = index;
        _walkedFrom = position;
        _walkedAt = DateTimeOffset.UtcNow;
    }

    private void ReportWaypointRefused(int index, Waypoint waypoint)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextRefusalWarning)
        {
            return;
        }

        _nextRefusalWarning = now.AddSeconds(10);

        _logger.LogWarning
        (
            waypoint.IsClickable
                ? "Waypoint {Index} {Waypoint} could not be reached: the click was refused - is the game window still bound?"
                : "Waypoint {Index} {Waypoint} has no minimap click point, so the bot cannot walk anywhere. "
                  + "Record a route: arm F9 in the window, stand on a spot, point at it on the minimap, press F9, then F10.",
            index + 1,
            waypoint
        );
    }

    /// <summary>
    /// Decides whether a sustained movement order has to be sent again.
    /// </summary>
    /// <remarks>
    /// The test is progress, not time. A character that is moving is obeying the order it already
    /// has, and interrupting it every tick is what turns travelling into jittering on the spot. Only
    /// a character that has not moved at all for the whole re-issue window is one whose order went
    /// nowhere - a click the client dropped, or a point it will not walk to.
    /// </remarks>
    private bool ShouldReissueWalk(int index, Waypoint position)
    {
        var now = DateTimeOffset.UtcNow;

        // A different destination is a new order, always.
        if (_walkingTo != index)
        {
            return true;
        }

        // Without a position, "has not moved" is not something we know - it is something we cannot
        // see. An unreported position holds still at (0,0) whatever the character does, so judging
        // the order by it condemns clicks that are working: that is how a click the client accepted
        // ended up blamed, escalated away from, and finally abandoned along with its waypoint.
        if (!_state.HasPosition)
        {
            return false;
        }

        if (position != _walkedFrom)
        {
            // Moving. Reset the stall clock against where we are now.
            _walkedFrom = position;
            _walkedAt = now;
            _stalls = 0;
            return false;
        }

        if (now - _walkedAt < _options.WalkReissueInterval)
        {
            return false;
        }

        _stalls++;

        // Changing how the order is sent has had its chance by now, and repeating it has had
        // several. The waypoint itself is the problem: a point the character cannot reach, or one
        // it is standing near enough to that it will never be judged to have arrived. Leaving it
        // for the next one keeps the patrol going instead of parking the bot there for good.
        if (_stalls >= 4)
        {
            var abandoned = _options.Waypoints[index];
            var attempts = _stalls;
            var next = _state.AdvanceWaypoint();
            _stalls = 0;
            _walkingTo = -1;

            _logger.LogWarning
            (
                "Giving up on waypoint {Index} {Waypoint}: {Stalls} orders and the character never " +
                "moved. Skipping to {Next}. If this one always fails, its minimap point is wrong, or " +
                "WaypointArrivalRadius ({Radius}) is too small for how close the click gets.",
                index + 1,
                abandoned,
                attempts,
                next,
                _options.WaypointArrivalRadius
            );

            return false;
        }

        // Once is a lost click. Twice in a row is the order being accepted and ignored, which no
        // amount of repeating will fix - so change how it is sent before trying again.
        if (_stalls >= 2 && _actuator.TryAnotherWayToMove(out var changed))
        {
            _logger.LogWarning
            (
                "Waypoint {Index} was clicked {Stalls} times without the character moving. " +
                "Switching to: {Changed}.",
                index + 1,
                _stalls,
                changed
            );

            _stalls = 0;
            return true;
        }

        _logger.LogWarning
        (
            "Still at ({X},{Y}) {Seconds:0.#}s after clicking for waypoint {Index}; sending it again. " +
            "If this repeats, that waypoint's minimap point is wrong - record the route again.",
            position.X,
            position.Y,
            (now - _walkedAt).TotalSeconds,
            index + 1
        );

        return true;
    }

    /// <summary>Gets what the loop decided on its last tick, for the window to show.</summary>
    /// <remarks>
    /// The log says what happened; this says what is happening. Every "the bot does nothing" so far
    /// has been the loop deciding, correctly and quietly, not to act - a state the log only shows to
    /// someone who knows which line to look for, and which one glance at this answers instead.
    /// </remarks>
    public string LastDecision { get; private set; } = "en attente du premier tick";

    private void Decide(int priority, string format, params object?[] args)
        => LogPriority(priority, format, args);

    private void LogPriority(int priority, string format, params object?[] args)
    {
        var message = string.Format(format, args);
        LastDecision = message;

        // Only announce a priority switch at information level; the rest stays at debug so the
        // 300ms cadence does not drown the log.
        if (Interlocked.Exchange(ref _lastLoggedPriority, priority) != priority)
        {
            _logger.LogInformation("Priority {Priority} | {Message}", priority, message);
            return;
        }

        _logger.LogDebug("Priority {Priority} | {Message}", priority, message);
    }
}
