using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Extensions;
using NosSmooth.Packets.Client.Battle;
using NosSmooth.Packets.Client.Inventory;
using NosSmooth.Packets.Enums.Entities;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Orchestration;

/// <summary>
/// The decision matrix. Runs on a fixed clock and resolves exactly one priority per tick.
/// </summary>
/// <remarks>
/// The responders are edge triggered - they act on the packet that just arrived. This loop is level
/// triggered: it looks at the state that those responders accumulated and keeps acting while a
/// condition holds. The two are deliberately gated through the same cooldowns on
/// <see cref="ProtocolStateManager"/>, so a potion or an attack frame is never dispatched twice for
/// one event.
/// </remarks>
public sealed class OrchestrationBackgroundService : BackgroundService
{
    private readonly ProtocolStateManager _state;
    private readonly PacketDispatcher _dispatcher;
    private readonly IMovementStrategy _movement;
    private readonly SkillRotation _rotation;
    private readonly BotController _controller;
    private readonly BotOptions _options;
    private readonly ILogger<OrchestrationBackgroundService> _logger;

    private int _lastLoggedPriority = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestrationBackgroundService"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="dispatcher">The outbound dispatcher.</param>
    /// <param name="movement">The movement strategy.</param>
    /// <param name="rotation">The attack rotation.</param>
    /// <param name="controller">The run/pause switch.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public OrchestrationBackgroundService
    (
        ProtocolStateManager state,
        PacketDispatcher dispatcher,
        IMovementStrategy movement,
        SkillRotation rotation,
        BotController controller,
        BotOptions options,
        ILogger<OrchestrationBackgroundService> logger
    )
    {
        _state = state;
        _dispatcher = dispatcher;
        _movement = movement;
        _rotation = rotation;
        _controller = controller;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation
        (
            "Orchestration loop started on a {Interval}ms clock over {Count} waypoint(s): {Waypoints}.",
            _options.TickInterval.TotalMilliseconds,
            _options.Waypoints.Count,
            string.Join(" -> ", _options.Waypoints)
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

        // Serialize the whole decision so two ticks can never interleave their outbound frames.
        using var cycle = await _state.EnterCycleAsync(ct).ConfigureAwait(false);

        if (await TrySurviveAsync(ct).ConfigureAwait(false))
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
    /// Priority 1 - survival. Dispatches a consumable frame while a pool sits below its threshold.
    /// </summary>
    private async Task<bool> TrySurviveAsync(CancellationToken ct)
    {
        var acted = false;

        if (_state.IsHpCritical && _state.TryTakeHpPotionGate(_options.PotionCooldown))
        {
            LogPriority(1, "survival: HP at {0:P0}", _state.HpRatio);
            Log(await _dispatcher.SendAsync(new UseItemPacket(_options.PotionBag, _options.HpPotionSlot), ct));
            acted = true;
        }

        if (_state.IsMpCritical && _state.TryTakeMpPotionGate(_options.PotionCooldown))
        {
            LogPriority(1, "survival: MP at {0:P0}", _state.MpRatio);
            Log(await _dispatcher.SendAsync(new UseItemPacket(_options.PotionBag, _options.MpPotionSlot), ct));
            acted = true;
        }

        return acted;
    }

    /// <summary>
    /// Priority 2 - engagement. All movement halts while a target is alive; attack frames are
    /// dispatched at the configured cadence.
    /// </summary>
    private async Task<bool> TryEngageAsync(CancellationToken ct)
    {
        if (_state.Target is not { } target)
        {
            // Nothing locked; give the scan one chance to lock the nearest live monster.
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

        // Close the gap first when the target is out of reach, otherwise the loop would hold
        // position forever against a monster it can never hit. Inside range, movement stops.
        if (_options.ApproachTargetOutOfRange
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

                var (approachX, approachY) = StepToward
                (
                    position,
                    new Waypoint(tracked.X, tracked.Y),
                    _options.MaxStepDistance
                );

                LogPriority(2, "engagement: closing on #{0}, {1} cells away (range {2})", target.EntityId, range, _options.AttackRange);
                Log(await _movement.MoveToAsync(approachX, approachY, ct));
                return true;
            }
        }

        // In range: the character holds position while the attack cycle runs.
        if (!_state.TryTakeAttackGate(_options.AttackInterval))
        {
            return true;
        }

        // Fixed priority: the first rotation entry that is off cooldown and affordable wins,
        // otherwise the basic attack keeps the damage flowing rather than idling the frame.
        var skill = _rotation.SelectNext(_state.CurrentMp);
        var castId = skill?.CastId ?? _options.BasicAttackCastId;

        LogPriority
        (
            2,
            "engagement: #{0} at {1}% HP -> {2} (cast {3})",
            target.EntityId,
            target.HpPercentage,
            skill?.Name ?? "attaque de base",
            castId
        );

        var attack = new UseSkillPacket
        (
            castId,
            target.EntityType == EntityType.Map ? EntityType.Monster : target.EntityType,
            target.EntityId,
            null,
            null
        );

        var sent = await _dispatcher.SendAsync(attack, ct);

        // Only start the cooldown if the frame actually went out.
        if (sent.IsSuccess && skill is not null)
        {
            _rotation.MarkCast(skill);
        }

        Log(sent);
        return true;
    }

    /// <summary>
    /// Priority 3 - navigation. Steps towards the active grid waypoint and advances the index once
    /// the character is inside the arrival radius.
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

        var (stepX, stepY) = StepToward(position, waypoint, _options.MaxStepDistance);

        LogPriority(3, "navigation: ({0},{1}) -> ({2},{3}), waypoint {4} is {5} cells away", position.X, position.Y, stepX, stepY, waypoint, distance);

        Log(await _movement.MoveToAsync(stepX, stepY, ct));
    }

    /// <summary>
    /// Takes one capped step from the current cell towards the destination.
    /// </summary>
    private static (int X, int Y) StepToward(Waypoint from, Waypoint to, int maxStep)
    {
        var deltaX = Math.Clamp(to.X - from.X, -maxStep, maxStep);
        var deltaY = Math.Clamp(to.Y - from.Y, -maxStep, maxStep);
        return (from.X + deltaX, from.Y + deltaY);
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

    private void Log(Result result)
    {
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Dispatch failed: {Error}", result.ToFullString());
        }
    }
}
