using System.Collections.Concurrent;
using System.Diagnostics;
using NosSmooth.Packets.Enums.Entities;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.State;

/// <summary>
/// A monster/NPC the client has seen spawn on the current map.
/// </summary>
/// <param name="EntityId">The network id of the entity.</param>
/// <param name="EntityType">The kind of the entity.</param>
/// <param name="X">Last known X coordinate.</param>
/// <param name="Y">Last known Y coordinate.</param>
/// <param name="HpPercentage">Last known HP percentage.</param>
public readonly record struct TrackedEntity(long EntityId, EntityType EntityType, int X, int Y, byte HpPercentage);

/// <summary>
/// A coherent snapshot of the current lock-on target.
/// </summary>
/// <param name="EntityId">The network id of the target.</param>
/// <param name="EntityType">The kind of the target.</param>
/// <param name="Hp">Last known absolute HP, or -1 when only a percentage is known.</param>
/// <param name="HpPercentage">Last known HP percentage.</param>
public readonly record struct TargetSnapshot(long EntityId, EntityType EntityType, long Hp, byte HpPercentage)
{
    /// <summary>
    /// Gets a value indicating whether the target still counts as alive.
    /// </summary>
    public bool IsAlive => HpPercentage > 0 && Hp != 0;
}

/// <summary>
/// Holds every piece of engine state that has to survive across packets.
/// </summary>
/// <remarks>
/// NosSmooth resolves packet responders out of a scope that is created per packet, so a responder
/// instance never sees the packet before it. This service is registered as a singleton and is the
/// only place where cross-packet state lives. Every member is safe to touch from the packet
/// threads and from the orchestration loop at the same time.
/// </remarks>
public sealed class ProtocolStateManager
{
    private readonly BotOptions _options;

    // Guards the (X, Y) pair so a reader can never observe a half-applied move.
    private readonly object _positionSync = new();

    // Guards the target id / hp pair for the same reason.
    private readonly object _targetSync = new();

    private readonly ConcurrentDictionary<long, TrackedEntity> _entities = new();

    // One decision cycle at a time. The orchestration loop holds this while it is
    // deciding and dispatching, so two ticks can never interleave their packets.
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    private Waypoint _position;
    private TargetSnapshot? _target;

    private long _currentHp;
    private long _maxHp;
    private long _currentMp;
    private long _maxMp;

    private long _ownCharacterId = -1;
    private int _waypointIndex;

    private long _lastHpPotionStamp;
    private long _lastMpPotionStamp;
    private long _lastAttackStamp;
    private long _lastWalkStamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolStateManager"/> class.
    /// </summary>
    /// <param name="options">The bot options.</param>
    public ProtocolStateManager(BotOptions options)
    {
        _options = options;
        _position = options.Waypoints.Count > 0 ? options.Waypoints[0] : new Waypoint(0, 0);
    }

    /// <summary>Gets the last known X coordinate of the character.</summary>
    public int CurrentX => Position.X;

    /// <summary>Gets the last known Y coordinate of the character.</summary>
    public int CurrentY => Position.Y;

    /// <summary>Gets the last known position of the character as one coherent pair.</summary>
    public Waypoint Position
    {
        get
        {
            lock (_positionSync)
            {
                return _position;
            }
        }
    }

    /// <summary>Gets the current HP of the character.</summary>
    public long CurrentHp => Interlocked.Read(ref _currentHp);

    /// <summary>Gets the maximum HP of the character.</summary>
    public long MaxHp => Interlocked.Read(ref _maxHp);

    /// <summary>Gets the current MP of the character.</summary>
    public long CurrentMp => Interlocked.Read(ref _currentMp);

    /// <summary>Gets the maximum MP of the character.</summary>
    public long MaxMp => Interlocked.Read(ref _maxMp);

    /// <summary>Gets the network id of the controlled character, or -1 when it is not known yet.</summary>
    public long OwnCharacterId => Interlocked.Read(ref _ownCharacterId);

    /// <summary>Gets the current lock-on target, if any.</summary>
    public TargetSnapshot? Target
    {
        get
        {
            lock (_targetSync)
            {
                return _target;
            }
        }
    }

    /// <summary>Gets the id of the current lock-on target, or 0 when nothing is locked.</summary>
    public long TargetEntityId => Target?.EntityId ?? 0;

    /// <summary>Gets the last known HP of the lock-on target.</summary>
    public long TargetHp => Target?.Hp ?? 0;

    /// <summary>Gets a value indicating whether a target is locked and still alive.</summary>
    public bool HasLiveTarget => Target is { } target && target.IsAlive;

    /// <summary>Gets the HP ratio in the 0..1 range. Returns 1 while max HP is unknown.</summary>
    public double HpRatio
    {
        get
        {
            var max = MaxHp;
            return max <= 0 ? 1d : Math.Clamp((double)CurrentHp / max, 0d, 1d);
        }
    }

    /// <summary>Gets the MP ratio in the 0..1 range. Returns 1 while max MP is unknown.</summary>
    public double MpRatio
    {
        get
        {
            var max = MaxMp;
            return max <= 0 ? 1d : Math.Clamp((double)CurrentMp / max, 0d, 1d);
        }
    }

    /// <summary>Gets a value indicating whether HP has dropped to the consumable threshold.</summary>
    public bool IsHpCritical => MaxHp > 0 && HpRatio <= _options.HpPotionThreshold;

    /// <summary>Gets a value indicating whether MP has dropped to the consumable threshold.</summary>
    public bool IsMpCritical => MaxMp > 0 && MpRatio <= _options.MpPotionThreshold;

    /// <summary>Gets the index of the waypoint currently being walked to.</summary>
    public int WaypointIndex => Volatile.Read(ref _waypointIndex);

    /// <summary>Gets the waypoint currently being walked to.</summary>
    public Waypoint CurrentWaypoint
    {
        get
        {
            var waypoints = _options.Waypoints;
            return waypoints.Count == 0 ? Position : waypoints[WaypointIndex % waypoints.Count];
        }
    }

    /// <summary>Gets every entity currently known to be on the map.</summary>
    public IReadOnlyCollection<TrackedEntity> KnownEntities => _entities.Values.ToArray();

    /// <summary>
    /// Takes the single-cycle lock. Dispose the result to release it.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A disposable that releases the lock.</returns>
    public async Task<IDisposable> EnterCycleAsync(CancellationToken ct)
    {
        await _cycleLock.WaitAsync(ct).ConfigureAwait(false);
        return new CycleLease(_cycleLock);
    }

    /// <summary>
    /// Records the character position.
    /// </summary>
    /// <param name="x">The X coordinate.</param>
    /// <param name="y">The Y coordinate.</param>
    public void UpdatePosition(int x, int y)
    {
        lock (_positionSync)
        {
            _position = new Waypoint(x, y);
        }
    }

    /// <summary>
    /// Records the network id of the controlled character.
    /// </summary>
    /// <param name="characterId">The character id.</param>
    public void SetOwnCharacterId(long characterId)
        => Interlocked.Exchange(ref _ownCharacterId, characterId);

    /// <summary>
    /// Records the character vitals from a stat packet.
    /// </summary>
    /// <param name="hp">Current HP.</param>
    /// <param name="maxHp">Maximum HP.</param>
    /// <param name="mp">Current MP.</param>
    /// <param name="maxMp">Maximum MP.</param>
    public void UpdateVitals(long hp, long maxHp, long mp, long maxMp)
    {
        Interlocked.Exchange(ref _currentHp, hp);
        Interlocked.Exchange(ref _maxHp, maxHp);
        Interlocked.Exchange(ref _currentMp, mp);
        Interlocked.Exchange(ref _maxMp, maxMp);
    }

    /// <summary>
    /// Adds or refreshes a tracked entity.
    /// </summary>
    /// <param name="entity">The entity.</param>
    public void TrackEntity(TrackedEntity entity)
        => _entities[entity.EntityId] = entity;

    /// <summary>
    /// Moves a tracked entity.
    /// </summary>
    /// <param name="entityId">The entity id.</param>
    /// <param name="x">The X coordinate.</param>
    /// <param name="y">The Y coordinate.</param>
    public void MoveEntity(long entityId, int x, int y)
    {
        if (_entities.TryGetValue(entityId, out var known))
        {
            _entities[entityId] = known with { X = x, Y = y };
        }
    }

    /// <summary>
    /// Drops a tracked entity, clearing the lock-on if it was the target.
    /// </summary>
    /// <param name="entityId">The entity id.</param>
    public void ForgetEntity(long entityId)
    {
        _entities.TryRemove(entityId, out _);
        ClearTarget(entityId);
    }

    /// <summary>
    /// Drops every tracked entity, e.g. on a map change.
    /// </summary>
    public void ForgetAllEntities()
    {
        _entities.Clear();
        ClearTarget();
    }

    /// <summary>
    /// Looks up a tracked entity.
    /// </summary>
    /// <param name="entityId">The entity id.</param>
    /// <param name="entity">The entity, when found.</param>
    /// <returns>True when the entity is tracked.</returns>
    public bool TryGetEntity(long entityId, out TrackedEntity entity)
        => _entities.TryGetValue(entityId, out entity);

    /// <summary>
    /// Finds the closest live monster within the engagement radius.
    /// </summary>
    /// <returns>The nearest engageable monster, if any.</returns>
    public TrackedEntity? FindNearestMonster()
    {
        var origin = Position;
        TrackedEntity? best = null;
        var bestDistance = double.MaxValue;

        foreach (var entity in _entities.Values)
        {
            if (entity.EntityType != EntityType.Monster || entity.HpPercentage == 0)
            {
                continue;
            }

            var distance = Distance(origin.X, origin.Y, entity.X, entity.Y);
            if (distance > _options.EngagementRadius || distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = entity;
        }

        return best;
    }

    /// <summary>
    /// Locks on to an entity, replacing any previous target.
    /// </summary>
    /// <param name="entityId">The entity id.</param>
    /// <param name="entityType">The entity type.</param>
    /// <param name="hpPercentage">The known HP percentage.</param>
    /// <returns>True when this call changed the target.</returns>
    public bool AcquireTarget(long entityId, EntityType entityType, byte hpPercentage = 100)
    {
        lock (_targetSync)
        {
            if (_target?.EntityId == entityId)
            {
                return false;
            }

            _target = new TargetSnapshot(entityId, entityType, -1, hpPercentage);
            return true;
        }
    }

    /// <summary>
    /// Records the vital signs of the locked entity. Ignores updates for anything else.
    /// </summary>
    /// <param name="entityId">The entity the update is about.</param>
    /// <param name="hp">The absolute HP, or -1 when unknown.</param>
    /// <param name="hpPercentage">The HP percentage.</param>
    /// <returns>True when the update applied to the current target.</returns>
    public bool UpdateTargetVitals(long entityId, long hp, byte hpPercentage)
    {
        if (_entities.TryGetValue(entityId, out var tracked))
        {
            _entities[entityId] = tracked with { HpPercentage = hpPercentage };
        }

        lock (_targetSync)
        {
            if (_target is not { } target || target.EntityId != entityId)
            {
                return false;
            }

            _target = target with { Hp = hp, HpPercentage = hpPercentage };
            return true;
        }
    }

    /// <summary>
    /// Clears the lock-on, but only when it still points at the given entity.
    /// </summary>
    /// <param name="entityId">The entity id.</param>
    /// <returns>True when a target was actually cleared.</returns>
    public bool ClearTarget(long entityId)
    {
        lock (_targetSync)
        {
            if (_target is not { } target || target.EntityId != entityId)
            {
                return false;
            }

            _target = null;
            return true;
        }
    }

    /// <summary>
    /// Clears the lock-on unconditionally.
    /// </summary>
    public void ClearTarget()
    {
        lock (_targetSync)
        {
            _target = null;
        }
    }

    /// <summary>
    /// Advances to the next waypoint, wrapping around at the end of the path.
    /// </summary>
    /// <returns>The waypoint that is now current.</returns>
    public Waypoint AdvanceWaypoint()
    {
        var waypoints = _options.Waypoints;
        if (waypoints.Count == 0)
        {
            return Position;
        }

        var next = Interlocked.Increment(ref _waypointIndex);
        return waypoints[((next % waypoints.Count) + waypoints.Count) % waypoints.Count];
    }

    /// <summary>Takes the HP consumable gate if its cooldown has elapsed.</summary>
    /// <param name="cooldown">The cooldown.</param>
    /// <returns>True when the caller may use the consumable.</returns>
    public bool TryTakeHpPotionGate(TimeSpan cooldown)
        => TryTakeGate(ref _lastHpPotionStamp, cooldown);

    /// <summary>Takes the MP consumable gate if its cooldown has elapsed.</summary>
    /// <param name="cooldown">The cooldown.</param>
    /// <returns>True when the caller may use the consumable.</returns>
    public bool TryTakeMpPotionGate(TimeSpan cooldown)
        => TryTakeGate(ref _lastMpPotionStamp, cooldown);

    /// <summary>Takes the attack gate if its cooldown has elapsed.</summary>
    /// <param name="cooldown">The cooldown.</param>
    /// <returns>True when the caller may dispatch an attack frame.</returns>
    public bool TryTakeAttackGate(TimeSpan cooldown)
        => TryTakeGate(ref _lastAttackStamp, cooldown);

    /// <summary>Takes the walk gate if its cooldown has elapsed.</summary>
    /// <param name="cooldown">The cooldown.</param>
    /// <returns>True when the caller may dispatch a movement frame.</returns>
    public bool TryTakeWalkGate(TimeSpan cooldown)
        => TryTakeGate(ref _lastWalkStamp, cooldown);

    /// <summary>
    /// Chebyshev distance, which is how NosTale measures range on its grid.
    /// </summary>
    /// <param name="x1">First X.</param>
    /// <param name="y1">First Y.</param>
    /// <param name="x2">Second X.</param>
    /// <param name="y2">Second Y.</param>
    /// <returns>The distance in cells.</returns>
    public static int Distance(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    /// <summary>
    /// A compact one-line dump of the engine state, used by the loop logs.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        var target = Target;
        var targetText = target is { } t
            ? $"#{t.EntityId} ({t.HpPercentage}%)"
            : "none";

        return $"pos={Position} hp={CurrentHp}/{MaxHp} mp={CurrentMp}/{MaxMp} " +
               $"target={targetText} waypoint={CurrentWaypoint} entities={_entities.Count}";
    }

    private static bool TryTakeGate(ref long stampField, TimeSpan cooldown)
    {
        var now = Stopwatch.GetTimestamp();

        while (true)
        {
            var last = Interlocked.Read(ref stampField);
            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < cooldown)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref stampField, now, last) == last)
            {
                return true;
            }
        }
    }

    private sealed class CycleLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public CycleLease(SemaphoreSlim semaphore)
            => _semaphore = semaphore;

        public void Dispose()
            => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
