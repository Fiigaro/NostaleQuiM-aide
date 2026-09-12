namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// Which key does what, as the character's quick bar is actually laid out.
/// </summary>
/// <remarks>
/// This is the whole configuration surface once the bot drives the game by keyboard. No cast ids, no
/// skill VNums, no inventory slots: the player moves a skill on their bar, they change one letter
/// here. It also sidesteps the Specialist problem entirely - a key is a key whichever skill set is
/// equipped.
/// </remarks>
public sealed class KeyBindings
{
    /// <summary>
    /// Gets or sets the key that selects and attacks the nearest monster. Space on this server.
    /// </summary>
    public string TargetAndAttack { get; set; } = "space";

    /// <summary>Gets or sets the key that picks up loot, or null when the server loots for you.</summary>
    public string? Loot { get; set; }

    /// <summary>Gets or sets the key that drinks an HP potion, or null when there is none.</summary>
    public string? HpPotion { get; set; }

    /// <summary>Gets or sets the key that drinks an MP potion, or null when there is none.</summary>
    public string? MpPotion { get; set; }
}
