using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.State;

/// <summary>
/// A read-only view of one rotation entry, for logging and the UI.
/// </summary>
/// <param name="CastId">The cast id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Enabled">Whether the skill takes part in the rotation.</param>
/// <param name="MpCost">The MP cost.</param>
/// <param name="Remaining">Time left on the cooldown, zero when ready.</param>
/// <param name="AffordableNow">Whether the character can currently pay the MP cost.</param>
/// <param name="AwaitingConfirmation">Whether the key was pressed and the server has not confirmed yet.</param>
public readonly record struct SkillStatus
(
    short CastId,
    string Name,
    bool Enabled,
    long MpCost,
    TimeSpan Remaining,
    bool AffordableNow,
    bool AwaitingConfirmation = false
)
{
    /// <summary>Gets a value indicating whether the skill could be cast right now.</summary>
    public bool IsReady => Enabled && Remaining <= TimeSpan.Zero && AffordableNow;
}

/// <summary>
/// Picks the next skill to cast, by fixed priority.
/// </summary>
/// <remarks>
/// Cooldowns are driven by the server: NosTale sends <c>sr &lt;skillId&gt;</c> when a skill comes
/// off cooldown, which is authoritative and immune to drift. A local timer runs alongside it as the
/// safety net, so the rotation still behaves correctly if <c>sr</c> never arrives or numbers its
/// skills differently than the cast ids - in that case the timer alone governs and the rotation
/// degrades to a plain cooldown scheduler rather than misfiring.
///
/// Driving the client by keyboard adds a step that does not exist when sending packets: a key press
/// is a request, not a cast. The client drops it silently with nothing selected, out of range, or
/// while the game's own cooldown runs. Starting the full cooldown on the press would therefore
/// retire a skill that never fired - the bot would stand there believing it had just used
/// everything. So a press only reserves the skill briefly, and the cooldown proper starts when
/// <c>su</c> confirms the cast actually happened.
/// </remarks>
public sealed class SkillRotation
{
    private readonly BotOptions _options;
    private readonly ILogger<SkillRotation> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<short, DateTimeOffset> _readyAt = new();

    // The skill whose key was pressed and which is still waiting for the server to confirm it. At
    // most one: the loop dispatches a single action per tick and the keyboard is serialized. It
    // expires with the confirmation window, so a cast the server never answers for cannot leave the
    // rotation waiting forever.
    private short? _pendingCastId;
    private DateTimeOffset _pendingUntil;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillRotation"/> class.
    /// </summary>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public SkillRotation(BotOptions options, ILogger<SkillRotation> logger)
    {
        _options = options;
        _logger = logger;

        foreach (var skill in _options.Skills)
        {
            _readyAt[skill.CastId] = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Returns the highest priority skill that is off cooldown and affordable.
    /// </summary>
    /// <param name="currentMp">The character's current MP.</param>
    /// <returns>The skill to cast, or null when the basic attack should be used.</returns>
    public SkillDefinition? SelectNext(long currentMp)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            foreach (var skill in _options.Skills)
            {
                if (!skill.Enabled || skill.MpCost > currentMp)
                {
                    continue;
                }

                if (_readyAt.TryGetValue(skill.CastId, out var readyAt) && readyAt > now)
                {
                    continue;
                }

                return skill;
            }
        }

        return null;
    }

    /// <summary>
    /// Starts the full cooldown of a skill that is known to have been cast.
    /// </summary>
    /// <param name="skill">The skill.</param>
    /// <remarks>
    /// For the packet path, where the cast is the packet and there is nothing to confirm.
    /// </remarks>
    public void MarkCast(SkillDefinition skill)
    {
        lock (_sync)
        {
            _readyAt[skill.CastId] = DateTimeOffset.UtcNow + skill.EffectiveCooldown;
            _pendingCastId = null;
        }
    }

    /// <summary>
    /// Records that a skill's key was pressed, reserving it only until the server answers.
    /// </summary>
    /// <param name="skill">The skill.</param>
    /// <remarks>
    /// The reservation is deliberately short. If the cast went through, <see cref="ConfirmCast"/>
    /// replaces it with the real cooldown; if the client refused the key, the reservation lapses and
    /// the skill is tried again, which is the correct behaviour for something that never fired.
    /// </remarks>
    public void MarkPressed(SkillDefinition skill)
    {
        lock (_sync)
        {
            _pendingUntil = DateTimeOffset.UtcNow + _options.SkillConfirmationWindow;
            _readyAt[skill.CastId] = _pendingUntil;
            _pendingCastId = skill.CastId;
        }
    }

    /// <summary>
    /// Gets the skill whose key was pressed and which the server has not answered for yet.
    /// </summary>
    public short? PendingCastId
    {
        get
        {
            lock (_sync)
            {
                return DateTimeOffset.UtcNow < _pendingUntil ? _pendingCastId : null;
            }
        }
    }

    /// <summary>
    /// Confirms whatever press is outstanding, for a cast the server named in a way we cannot map.
    /// </summary>
    /// <param name="castId">The slot that was confirmed.</param>
    /// <returns>True when a press was outstanding and has now been confirmed.</returns>
    /// <remarks>
    /// The fallback for a session that never saw a <c>ski</c>, so the skill bar is unknown. Only one
    /// press is ever outstanding and the loop presses nothing else while it is, so an unmapped skill
    /// cast by us can only be that one.
    /// </remarks>
    public bool TryConfirmPending(out short castId)
    {
        lock (_sync)
        {
            if (_pendingCastId is not { } pending || DateTimeOffset.UtcNow >= _pendingUntil)
            {
                castId = 0;
                return false;
            }

            castId = pending;
        }

        return ConfirmCast(castId);
    }

    /// <summary>
    /// Starts the real cooldown of a skill the server reported as cast.
    /// </summary>
    /// <param name="castId">The cast id, resolved from the VNum in <c>su</c>.</param>
    /// <returns>True when the id matched a rotation entry.</returns>
    public bool ConfirmCast(short castId)
    {
        SkillDefinition? skill;

        lock (_sync)
        {
            if (!_readyAt.ContainsKey(castId))
            {
                return false;
            }

            skill = _options.Skills.FirstOrDefault(s => s.CastId == castId);
            if (skill is null)
            {
                return false;
            }

            _readyAt[castId] = DateTimeOffset.UtcNow + skill.EffectiveCooldown;

            if (_pendingCastId == castId)
            {
                _pendingCastId = null;
            }
        }

        _logger.LogDebug("su -> skill {Name} confirmed cast, cooldown {Seconds:0.#}s.", skill.Name, skill.EffectiveCooldown.TotalSeconds);
        return true;
    }

    /// <summary>
    /// Clears the cooldown of a skill the server reported ready.
    /// </summary>
    /// <param name="castId">The cast id reported by the <c>sr</c> packet.</param>
    /// <returns>True when the id matched a rotation entry.</returns>
    public bool MarkServerReady(short castId)
    {
        lock (_sync)
        {
            if (!_readyAt.ContainsKey(castId))
            {
                return false;
            }

            _readyAt[castId] = DateTimeOffset.MinValue;

            if (_pendingCastId == castId)
            {
                _pendingCastId = null;
            }
        }

        _logger.LogDebug("sr -> skill {CastId} is off cooldown.", castId);
        return true;
    }

    /// <summary>
    /// Clears one skill's cooldown.
    /// </summary>
    /// <param name="castId">The cast id.</param>
    /// <returns>True when the id matched a rotation entry.</returns>
    /// <remarks>
    /// Editing a cooldown in the window only changes what the next cast will use; the countdown
    /// already running was started from the old number. Clearing it by hand is what makes a
    /// corrected value take effect now rather than after the wrong wait.
    /// </remarks>
    public bool Reset(short castId)
    {
        lock (_sync)
        {
            if (!_readyAt.ContainsKey(castId))
            {
                return false;
            }

            _readyAt[castId] = DateTimeOffset.MinValue;

            if (_pendingCastId == castId)
            {
                _pendingCastId = null;
            }
        }

        return true;
    }

    /// <summary>
    /// Clears every cooldown, e.g. after leaving combat or changing map.
    /// </summary>
    public void ResetAll()
    {
        lock (_sync)
        {
            foreach (var castId in _readyAt.Keys.ToArray())
            {
                _readyAt[castId] = DateTimeOffset.MinValue;
            }

            _pendingCastId = null;
        }
    }

    /// <summary>
    /// Takes a snapshot of the rotation for display.
    /// </summary>
    /// <param name="currentMp">The character's current MP.</param>
    /// <returns>One status per configured skill, in priority order.</returns>
    public IReadOnlyList<SkillStatus> Snapshot(long currentMp)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<SkillStatus>(_options.Skills.Count);

        lock (_sync)
        {
            foreach (var skill in _options.Skills)
            {
                var readyAt = _readyAt.TryGetValue(skill.CastId, out var value) ? value : DateTimeOffset.MinValue;
                var remaining = readyAt > now ? readyAt - now : TimeSpan.Zero;

                result.Add(new SkillStatus
                (
                    skill.CastId,
                    skill.Name,
                    skill.Enabled,
                    skill.MpCost,
                    remaining,
                    skill.MpCost <= currentMp,
                    _pendingCastId == skill.CastId && now < _pendingUntil
                ));
            }
        }

        return result;
    }
}
