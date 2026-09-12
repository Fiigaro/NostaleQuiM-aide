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
public readonly record struct SkillStatus
(
    short CastId,
    string Name,
    bool Enabled,
    long MpCost,
    TimeSpan Remaining,
    bool AffordableNow
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
/// </remarks>
public sealed class SkillRotation
{
    private readonly BotOptions _options;
    private readonly ILogger<SkillRotation> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<short, DateTimeOffset> _readyAt = new();

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
    /// Starts the cooldown of a skill that was just cast.
    /// </summary>
    /// <param name="skill">The skill.</param>
    public void MarkCast(SkillDefinition skill)
    {
        lock (_sync)
        {
            _readyAt[skill.CastId] = DateTimeOffset.UtcNow + skill.EffectiveCooldown;
        }
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
                    skill.MpCost <= currentMp
                ));
            }
        }

        return result;
    }
}
