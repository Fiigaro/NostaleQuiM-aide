namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// The raid loop: one key, Enter, one minimap click, then attack for a while - and again.
/// </summary>
/// <remarks>
/// Deliberately blind. Nothing here reads the game: no map, no monster, no position. It is a macro
/// with a schedule, which is exactly what a raid that always starts the same way needs, and exactly
/// why it works where everything that depends on the server announcing something has been fragile.
/// Every pause is a setting because the right value is a property of the raid and of the machine,
/// not of this code.
/// </remarks>
public sealed class RaidMacroOptions
{
    /// <summary>Gets or sets the key pressed first, e.g. the one that opens the raid.</summary>
    public string? Key { get; set; }

    /// <summary>Gets or sets how long to wait after that key.</summary>
    public int AfterKeyMs { get; set; } = 1000;

    /// <summary>Gets or sets how long to wait after Enter - long enough for a teleport to load.</summary>
    public int AfterEnterMs { get; set; } = 4000;

    /// <summary>Gets or sets X of the minimap click, inside the game window.</summary>
    public int? ClickX { get; set; }

    /// <summary>Gets or sets Y of the minimap click, inside the game window.</summary>
    public int? ClickY { get; set; }

    /// <summary>Gets or sets how long to wait after the click - long enough for the walk.</summary>
    public int AfterClickMs { get; set; } = 5000;

    /// <summary>Gets or sets how long to keep attacking before starting over.</summary>
    public double AttackSeconds { get; set; } = 60;

    /// <summary>Gets or sets the key that attacks.</summary>
    public string AttackKey { get; set; } = "space";

    /// <summary>Gets or sets the gap between two presses of the attack key.</summary>
    public int AttackIntervalMs { get; set; } = 400;
}
