namespace NosSmoothCustomClient.Input;

/// <summary>
/// Sends keystrokes and clicks to the game.
/// </summary>
/// <remarks>
/// This is the actuator the whole bot ultimately acts through. It is deliberately the only place
/// that touches the outside world, so the decision loop stays testable and a dry run is a matter of
/// swapping the implementation rather than threading a flag through everything.
/// </remarks>
public interface IGameInput
{
    /// <summary>Gets a short description of what this actuator drives.</summary>
    string Description { get; }

    /// <summary>
    /// Binds to the game window.
    /// </summary>
    /// <param name="error">Why it could not bind, when it could not.</param>
    /// <returns>True when ready to send.</returns>
    bool TryAttach(out string error);

    /// <summary>
    /// Presses and releases a key.
    /// </summary>
    /// <param name="key">The key to press.</param>
    /// <returns>True when the input was accepted for delivery.</returns>
    bool PressKey(GameKey key);

    /// <summary>
    /// Clicks a point expressed relative to the game window's client area.
    /// </summary>
    /// <param name="x">X offset inside the window.</param>
    /// <param name="y">Y offset inside the window.</param>
    /// <returns>True when the input was accepted for delivery.</returns>
    bool ClickAt(int x, int y);
}

/// <summary>
/// The keys the bot uses. Restricted on purpose: the quick bar, space to target, and nothing else.
/// </summary>
/// <param name="Label">How the key reads in configuration and logs.</param>
/// <param name="VirtualKey">The Windows virtual-key code.</param>
public readonly record struct GameKey(string Label, ushort VirtualKey)
{
    /// <summary>Space, which on this server selects and attacks the nearest monster.</summary>
    public static GameKey Space => new(" ", 0x20);

    /// <summary>
    /// Parses a key from configuration.
    /// </summary>
    /// <param name="text">The key as written, e.g. "1", "0", "space", "r".</param>
    /// <param name="key">The parsed key.</param>
    /// <returns>True when the key is one the bot can send.</returns>
    public static bool TryParse(string? text, out GameKey key)
    {
        key = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (trimmed.Equals("space", StringComparison.OrdinalIgnoreCase) || trimmed == " ")
        {
            key = Space;
            return true;
        }

        if (trimmed.Length != 1)
        {
            return false;
        }

        var c = char.ToUpperInvariant(trimmed[0]);

        // Digits and letters map straight onto their virtual-key codes.
        if (c is >= '0' and <= '9' || c is >= 'A' and <= 'Z')
        {
            key = new GameKey(trimmed, c);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString()
        => Label;
}
