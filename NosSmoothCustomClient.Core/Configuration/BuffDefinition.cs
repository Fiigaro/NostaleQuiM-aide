using NosSmooth.Packets.Enums.Inventory;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// Something the character keeps up on themselves.
/// </summary>
/// <remarks>
/// A buff is applied either by casting a skill or by consuming an item - a live session showed both
/// in use, with two buffs coming from the Specialist's skills and two apparently from items in the
/// Etc bag. Modelling one source and not the other would have covered half the case.
/// </remarks>
/// <param name="Name">A display name, used in the logs and the UI.</param>
/// <param name="Duration">How long it lasts. Overridden by the server when <paramref name="CardId"/> is known.</param>
/// <param name="Cooldown">How soon it can be applied again.</param>
/// <param name="CastId">The skill's position in the bar, when the buff is a skill.</param>
/// <param name="ItemBag">The bag the item lives in, when the buff is an item.</param>
/// <param name="ItemSlot">The item's slot, when the buff is an item.</param>
/// <param name="CardId">The buff card the server reports in <c>bf</c>, when known.</param>
/// <param name="Enabled">Whether the buff is maintained.</param>
/// <param name="Key">The quick bar key that applies it, used by the keyboard actuator.</param>
public sealed record BuffDefinition
(
    string Name,
    TimeSpan Duration,
    TimeSpan Cooldown,
    short? CastId = null,
    BagType? ItemBag = null,
    long? ItemSlot = null,
    int? CardId = null,
    bool Enabled = true,
    string? Key = null
)
{
    /// <summary>Gets a value indicating whether this buff is applied by casting a skill.</summary>
    public bool IsSkill => CastId is not null;

    /// <summary>Gets a value indicating whether this buff is applied by using an item.</summary>
    public bool IsItem => ItemBag is not null && ItemSlot is not null;

    /// <summary>Gets a value indicating whether the definition names a way to apply it at all.</summary>
    public bool IsUsable => Enabled && (IsSkill || IsItem || !string.IsNullOrWhiteSpace(Key));

    /// <summary>Gets a stable key for tracking, distinct per source.</summary>
    public string TrackingKey => Key is { Length: > 0 } k ? $"key:{k}" : IsSkill ? $"skill:{CastId}" : $"item:{ItemBag}:{ItemSlot}";

    /// <summary>Gets the duration to assume before the server says otherwise.</summary>
    public TimeSpan EffectiveDuration
        => Duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(20) : Duration;

    /// <summary>Gets the cooldown to assume.</summary>
    public TimeSpan EffectiveCooldown
        => Cooldown <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : Cooldown;
}
