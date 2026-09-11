namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// What the configuration file actually changed, reported at startup.
/// </summary>
/// <param name="Description">A human-readable summary.</param>
/// <remarks>
/// Surfacing this matters: a mistyped section name leaves every default silently in place, and the
/// bot then behaves correctly for a configuration nobody intended.
/// </remarks>
public sealed record BotConfigurationSummary(string Description);
