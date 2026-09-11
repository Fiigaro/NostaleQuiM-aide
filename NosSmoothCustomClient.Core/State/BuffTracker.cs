using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.State;

/// <summary>
/// A buff's current standing.
/// </summary>
/// <param name="Buff">The buff.</param>
/// <param name="Remaining">Time left before it lapses, zero when it already has.</param>
/// <param name="OnCooldown">Whether the skill cannot be recast yet.</param>
public readonly record struct BuffStatus(BuffDefinition Buff, TimeSpan Remaining, bool OnCooldown)
{
    /// <summary>Gets a value indicating whether the buff is currently up.</summary>
    public bool IsActive => Remaining > TimeSpan.Zero;
}

/// <summary>
/// Keeps the maintained buffs up.
/// </summary>
/// <remarks>
/// Two clocks matter and they are not the same: how long the buff lasts, and how soon the skill can
/// be cast again. A buff lasting two minutes on a thirty second cooldown can be refreshed early; one
/// whose cooldown outlasts its duration cannot, and will always lapse for a while. Tracking them
/// separately is what keeps the loop from trying to recast something it cannot.
///
/// The local estimate is a fallback. When a buff's card id is configured, the server's own
/// <c>bf</c> - which carries the real remaining time - overrides it, so drift cannot accumulate.
/// </remarks>
public sealed class BuffTracker
{
    private readonly BotOptions _options;
    private readonly ILogger<BuffTracker> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiresAt = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _castableAt = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BuffTracker"/> class.
    /// </summary>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public BuffTracker(BotOptions options, ILogger<BuffTracker> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Picks the buff that should be cast now, if any.
    /// </summary>
    /// <param name="engaged">Whether the character is currently fighting.</param>
    /// <returns>The buff to cast and whether it had already lapsed, or null when nothing is due.</returns>
    /// <remarks>
    /// A lapsed buff is cast even mid-fight, because fighting without it is the thing being avoided.
    /// One merely close to lapsing waits for a lull, so the attack cycle is not interrupted for a
    /// buff that is still working.
    /// </remarks>
    public (BuffDefinition Buff, bool Lapsed)? SelectNext(bool engaged)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var buff in _options.Buffs)
        {
            if (!buff.IsUsable)
            {
                continue;
            }

            if (_castableAt.TryGetValue(buff.Key, out var castableAt) && castableAt > now)
            {
                continue;
            }

            var expiresAt = _expiresAt.TryGetValue(buff.Key, out var value) ? value : DateTimeOffset.MinValue;
            var remaining = expiresAt > now ? expiresAt - now : TimeSpan.Zero;

            if (remaining <= TimeSpan.Zero)
            {
                return (buff, true);
            }

            if (!engaged && remaining <= _options.BuffRefreshMargin)
            {
                return (buff, false);
            }
        }

        return null;
    }

    /// <summary>
    /// Records a buff cast, starting both clocks from the local estimate.
    /// </summary>
    /// <param name="buff">The buff.</param>
    public void MarkCast(BuffDefinition buff)
    {
        var now = DateTimeOffset.UtcNow;
        _expiresAt[buff.Key] = now + buff.EffectiveDuration;
        _castableAt[buff.Key] = now + buff.EffectiveCooldown;
    }

    /// <summary>
    /// Applies the server's own timing for a buff card.
    /// </summary>
    /// <param name="cardId">The buff card reported in <c>bf</c>.</param>
    /// <param name="tenthsOfSecond">The duration as the packet carries it.</param>
    /// <returns>True when the card matched a maintained buff.</returns>
    /// <remarks>
    /// The <c>bf</c> duration is in tenths of a second, not seconds. Reading it as seconds made two
    /// minutes look like twenty and five minutes like fifty - close enough to a plausible buff
    /// duration to pass unnoticed, which is exactly what makes the unit worth stating here.
    /// </remarks>
    public bool ApplyServerTiming(int cardId, int tenthsOfSecond)
    {
        var buff = _options.Buffs.FirstOrDefault(b => b.CardId == cardId);
        if (buff is null || tenthsOfSecond <= 0)
        {
            return false;
        }

        var duration = TimeSpan.FromSeconds(tenthsOfSecond / 10d);
        _expiresAt[buff.Key] = DateTimeOffset.UtcNow + duration;

        _logger.LogDebug
        (
            "Buff {Name} confirmed by the server for {Seconds:0.#}s (raw {Raw}).",
            buff.Name,
            duration.TotalSeconds,
            tenthsOfSecond
        );

        return true;
    }

    /// <summary>
    /// Forgets every buff, e.g. after dying or changing map.
    /// </summary>
    public void Reset()
    {
        _expiresAt.Clear();
        _castableAt.Clear();
    }

    /// <summary>
    /// Takes a snapshot for display.
    /// </summary>
    /// <returns>One status per maintained buff.</returns>
    public IReadOnlyList<BuffStatus> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;

        return _options.Buffs.Select(buff =>
        {
            var expiresAt = _expiresAt.TryGetValue(buff.Key, out var e) ? e : DateTimeOffset.MinValue;
            var castableAt = _castableAt.TryGetValue(buff.Key, out var c) ? c : DateTimeOffset.MinValue;

            return new BuffStatus
            (
                buff,
                expiresAt > now ? expiresAt - now : TimeSpan.Zero,
                castableAt > now
            );
        }).ToArray();
    }
}
