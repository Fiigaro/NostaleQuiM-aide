namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// One entry of the attack rotation.
/// </summary>
/// <param name="CastId">
/// The cast id, i.e. the skill's slot as it appears in the outbound <c>u_s</c> packet. 0 is the
/// basic attack.
/// </param>
/// <param name="Name">A display name, used in the logs and the UI.</param>
/// <param name="MpCost">
/// The MP the skill consumes. The rotation skips a skill the character cannot currently pay for.
/// </param>
/// <param name="Cooldown">
/// The fallback cooldown. The server's <c>sr</c> packet is authoritative when it arrives; this timer
/// is what keeps the rotation correct if it does not.
/// </param>
/// <param name="Enabled">Whether the skill takes part in the rotation.</param>
public sealed record SkillDefinition
(
    short CastId,
    string Name,
    long MpCost = 0,
    TimeSpan Cooldown = default,
    bool Enabled = true
)
{
    /// <summary>
    /// Gets the cooldown to apply, falling back to a conservative default when none was configured.
    /// </summary>
    public TimeSpan EffectiveCooldown
        => Cooldown <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : Cooldown;
}
