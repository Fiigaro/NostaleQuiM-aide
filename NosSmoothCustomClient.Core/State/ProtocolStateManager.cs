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
    private bool _hasPosition;
    private TargetSnapshot? _target;

    private long _currentHp;
    private long _maxHp;
    private long _currentMp;
    private long _maxMp;

    private long _ownCharacterId = -1;
    private int _currentMapId = -1;
    private volatile bool _roomCleared;
    private int _waypointIndex;

    private long _lastHpPotionStamp;
    private long _lastMpPotionStamp;
    private long _lastAttackStamp;
    private long _lastWalkStamp;
    private long _lastSearchStamp;

    // When the fight last got anywhere: something died, or the lock moved to another monster.
    private long _lastFightProgressStamp;

    // When the server last said anything about the target. Zero means "never".
    private long _lastTargetStamp;

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

    /// <summary>
    /// Gets a value indicating whether the character's position has ever been reported.
    /// </summary>
    /// <remarks>
    /// Position comes from <c>at</c>, which the server sends on entering a map, and from <c>mv</c>,
    /// which it sends while moving. A bot started on a character that is standing still has neither,
    /// and an unreported position reads as (0,0) - a real coordinate, and one that happens to be
    /// distance zero from any waypoint that was recorded the same way. Arrival logic against that is
    /// how a bot decides it is already everywhere it was going.
    /// </remarks>
    public bool HasPosition
    {
        get
        {
            lock (_positionSync)
            {
                return _hasPosition;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the server has wiped the entities on this map.
    /// </summary>
    /// <remarks>
    /// In an instance this is the room being finished: the monsters go in one packet rather than
    /// one at a time, which is why it says something a count of them never could. A count follows
    /// view range - monsters leave it by walking, and come back the same way - so it drops and
    /// rises with the character and reads zero in an empty corner of a room still full of them.
    /// Both recorded runs end their room on this and nothing else.
    /// </remarks>
    public bool RoomCleared => _roomCleared;

    /// <summary>Gets the map the character is currently on, or -1 while it is not known yet.</summary>
    public int CurrentMapId => Volatile.Read(ref _currentMapId);

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
            _hasPosition = true;
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
        Interlocked.Exchange(ref _lastFightProgressStamp, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Reports whether the fight has stopped getting anywhere.
    /// </summary>
    /// <param name="after">How long without progress counts.</param>
    /// <returns>True when nothing has died and the lock has not moved for that long.</returns>
    /// <remarks>
    /// Swinging is not progress. A character can hold a target and attack for as long as you like
    /// without the room emptying - the monster is out of reach, or the ones left are, and the
    /// attack key only ever finds what is already close. Something dying, or the lock moving to
    /// another monster, is what says the spot is still worth standing on; the absence of both is
    /// what says to go somewhere else.
    /// </remarks>
    public bool FightStalled(TimeSpan after)
    {
        var last = Interlocked.Read(ref _lastFightProgressStamp);
        return last != 0 && Stopwatch.GetElapsedTime(last, Stopwatch.GetTimestamp()) > after;
    }

    /// <summary>
    /// Drops every tracked entity, e.g. on a map change.
    /// </summary>
    /// <summary>
    /// Records that the server wiped the map's entities.
    /// </summary>
    public void MarkRoomCleared()
        => _roomCleared = true;

    /// <summary>
    /// Forgets every tracked entity.
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
            Interlocked.Exchange(ref _lastTargetStamp, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _lastFightProgressStamp, Stopwatch.GetTimestamp());
            return true;
        }
    }

    /// <summary>
    /// Records which map the character is on.
    /// </summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True when this is a different map than the one before.</returns>
    /// <remarks>
    /// A map change invalidates everything positional at once: the entity table describes monsters
    /// that are no longer anywhere near us, and the target sits on a map we have left. Clearing both
    /// here rather than leaving it to each caller is what stops the bot swinging at a ghost it can
    /// never reach after a teleport.
    /// </remarks>
    public bool EnterMap(int mapId)
    {
        var previous = Interlocked.Exchange(ref _currentMapId, mapId);
        if (previous == mapId)
        {
            return false;
        }

        // A new room has its own monsters; whatever was cleared belonged to the last one.
        _roomCleared = false;
        ForgetAllEntities();
        ClearTarget();
        return true;
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
            Interlocked.Exchange(ref _lastTargetStamp, Stopwatch.GetTimestamp());
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

        // The way out is not a stop on the round. Leaving it in the circuit means walking into it
        // partway through a room, which ends the run with the room still full - and it is the one
        // waypoint whose whole purpose is to be reached exactly once, at the end.
        var exit = _options.InstanceMode ? _options.ResolveExitWaypoint() : -1;

        for (var step = 0; step < waypoints.Count; step++)
        {
            var next = Interlocked.Increment(ref _waypointIndex);
            var index = ((next % waypoints.Count) + waypoints.Count) % waypoints.Count;

            if (index != exit)
            {
                return waypoints[index];
            }
        }

        // Every waypoint is the exit, so there is nowhere else to be.
        return waypoints[((Volatile.Read(ref _waypointIndex) % waypoints.Count) + waypoints.Count) % waypoints.Count];
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

    /// <summary>
    /// Opens the target search gate, so the next tick looks for a target straight away.
    /// </summary>
    /// <remarks>
    /// Called when a target dies. Waiting out the search interval there means several ticks with no
    /// target, which navigation reads as "nothing to do here" - so the bot walks away from the spot
    /// it was just farming, right as the next monster spawns into it.
    /// </remarks>
    public void ResetSearchGate()
        => Interlocked.Exchange(ref _lastSearchStamp, 0);

    /// <summary>Takes the target search gate if its cooldown has elapsed.</summary>
    /// <param name="cooldown">The cooldown.</param>
    /// <returns>True when the caller may probe for a target.</returns>
    public bool TryTakeSearchGate(TimeSpan cooldown)
        => TryTakeGate(ref _lastSearchStamp, cooldown);

    /// <summary>
    /// Reports whether the server has stopped mentioning the current target.
    /// </summary>
    /// <param name="after">How long silence has to last to count.</param>
    /// <returns>True when a target is held but has gone quiet for longer than that.</returns>
    /// <remarks>
    /// Needed because a lock can outlive the fight without anything announcing it: the monster
    /// walks out of range, another player kills it, or the selection was never really made. None of
    /// those produce a death packet, so only the silence gives it away.
    /// </remarks>
    public bool TargetWentQuiet(TimeSpan after)
    {
        lock (_targetSync)
        {
            if (_target is null)
            {
                return false;
            }
        }

        var last = Interlocked.Read(ref _lastTargetStamp);
        return last != 0 && Stopwatch.GetElapsedTime(last, Stopwatch.GetTimestamp()) > after;
    }

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

        return $"map={CurrentMapId} pos={Position} hp={CurrentHp}/{MaxHp} mp={CurrentMp}/{MaxMp} " +
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
