using System.Text.Json;
using System.Text.Json.Serialization;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// Persists what was changed in the window, without touching the hand-written file.
/// </summary>
/// <remarks>
/// Writes a separate <c>appsettings.local.json</c> loaded after the main one, so its values win.
/// Rewriting <c>appsettings.json</c> itself would be simpler and would also destroy every comment
/// in it - and those comments are where the captured values and their provenance are recorded.
/// </remarks>
public static class LocalConfigurationWriter
{
    /// <summary>The file overrides are written to.</summary>
    public const string FileName = "appsettings.local.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Writes the tunable parts of the options as an override file.
    /// </summary>
    /// <param name="options">The live options.</param>
    /// <param name="directory">Where to write, defaulting to the working directory.</param>
    /// <returns>The path written, or null with the reason on failure.</returns>
    public static (string? Path, string? Error) Save(BotOptions options, string? directory = null)
    {
        var path = Path.Combine(directory ?? Directory.GetCurrentDirectory(), FileName);

        try
        {
            var payload = new
            {
                Bot = new
                {
                    Skills = options.Skills.Select(s => new
                    {
                        s.Key,
                        s.Name,
                        CooldownSeconds = Math.Round(s.EffectiveCooldown.TotalSeconds, 1),
                        s.MpCost,
                        s.CastId,
                        s.Enabled
                    }),
                    Buffs = options.Buffs.Select(b => new
                    {
                        b.Key,
                        b.Name,
                        DurationSeconds = Math.Round(b.EffectiveDuration.TotalSeconds, 1),
                        CooldownSeconds = Math.Round(b.EffectiveCooldown.TotalSeconds, 1),
                        b.CardId,
                        b.Enabled
                    })
                }
            };

            File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
            return (path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, ex.Message);
        }
    }
}
