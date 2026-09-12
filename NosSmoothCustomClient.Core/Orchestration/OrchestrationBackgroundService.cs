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

    private async Task TickAsync(CancellationToken ct)
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
        if (_state.Target is not { } target)
        {
            if (_state.FindNearestMonster() is not { } candidate)
            {
                return false;
            }

            _state.AcquireTarget(candidate.EntityId, candidate.EntityType, candidate.HpPercentage);
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

        // Selecting is a no-op where targeting is explicit, and the whole attack where it is not.
        await _actuator.TargetNearestAsync(ct).ConfigureAwait(false);

        // Fixed priority: the first rotation entry that is off cooldown and affordable wins,
        // otherwise the basic attack keeps the damage flowing rather than idling the frame.
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
