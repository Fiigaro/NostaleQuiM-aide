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
        if (!_controller.IsRunning)
        {
            // Paused: the packet pipeline keeps running, so the loop resumes on fresh state.
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

        // Hold everything while a cast is outstanding. Another key now could cancel the skill we
        // just asked for, and it would make the server's answer ambiguous - which matters, because
        // that answer is how the quick bar gets identified when no ski was ever seen. The wait is
        // bounded: the press expires with its confirmation window.
        if (_rotation.PendingCastId is { } pending)
        {
            _logger.LogDebug("Waiting for the server to confirm the cast from slot {CastId}.", pending);
            return true;
        }

        if (!_state.TryTakeAttackGate(_options.AttackInterval))
        {
            return true;
        }

        var skill = _rotation.SelectNext(_state.CurrentMp);

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
            // Pressed, not cast. The full cooldown waits for the server to confirm it went off.
            _rotation.MarkPressed(skill);
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
            return;
        }

        // A route is minimap click points, and a minimap belongs to a map. Walking one recorded
        // elsewhere is clicking at random - which is merely useless while grinding and actively
        // harmful in a raid, where the next room is another map entirely.
        if (_options.RouteMapId is { } routeMap && _state.CurrentMapId != routeMap)
        {
            LogPriority
            (
                4,
                "navigation: on map {0}, but the route belongs to map {1} - holding position",
                _state.CurrentMapId,
                routeMap
            );

            return;
        }

        var position = _state.Position;
        var waypoint = _state.CurrentWaypoint;
        var distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);

        if (distance <= _options.WaypointArrivalRadius)
        {
            var next = _state.AdvanceWaypoint();
            _logger.LogInformation
            (
                "Reached waypoint {Waypoint} (within {Radius} cells); next waypoint is {Next}.",
                waypoint,
                _options.WaypointArrivalRadius,
                next
            );

            waypoint = next;
            distance = ProtocolStateManager.Distance(position.X, position.Y, waypoint.X, waypoint.Y);
            if (distance == 0)
            {
                return;
            }
        }

        if (!_state.TryTakeWalkGate(_options.TickInterval))
        {
            return;
        }

        LogPriority(4, "navigation: at ({0},{1}), heading for {2}, {3} cells away", position.X, position.Y, waypoint, distance);

        await _actuator.GoToWaypointAsync(_state.WaypointIndex % waypoints.Count, ct).ConfigureAwait(false);
    }

    private void LogPriority(int priority, string format, params object?[] args)
    {
        var message = string.Format(format, args);

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
