namespace NosSmoothCustomClient.Input;

/// <summary>
/// A key the bot watches for, as opposed to one it presses.
/// </summary>
/// <param name="Label">How it reads in the window and in configuration.</param>
/// <param name="VirtualKey">The Windows virtual-key code.</param>
/// <remarks>
/// Separate from <see cref="GameKey"/> on purpose: that one describes keys sent to the game, which
/// are the quick bar and nothing else. These are pressed by the operator inside the game to talk to
/// the bot, so what matters about them is the opposite - that the game itself does nothing with
/// them. Which of them are free is a property of the client and of its key bindings, so the choice
/// belongs to the person playing it rather than to a constant in here.
/// </remarks>
public readonly record struct HotKey(string Label, int VirtualKey)
{
    /// <summary>
    /// The keys offered, most likely to be free first.
    /// </summary>
    /// <remarks>
    /// F7 to F10 are left out: the waypoint recorder already has them. F11 is offered but last -
    /// on this client it opens the shop, which is exactly the kind of collision this list exists
    /// to let someone fix without waiting for a new build.
    /// </remarks>
    public static IReadOnlyList<HotKey> Choices { get; } = new[]
    {
        new HotKey("F12", 0x7B),
        new HotKey("Pause", 0x13),
        new HotKey("Inser", 0x2D),
        new HotKey("Suppr", 0x2E),
        new HotKey("Fin", 0x23),
        new HotKey("Origine", 0x24),
        new HotKey("Arrêt défil", 0x91),
        new HotKey("F1", 0x70),
        new HotKey("F2", 0x71),
        new HotKey("F3", 0x72),
        new HotKey("F4", 0x73),
        new HotKey("F5", 0x74),
        new HotKey("F6", 0x75),
        new HotKey("F11", 0x7A)
    };

    /// <summary>
    /// Reads a hotkey from configuration.
    /// </summary>
    /// <param name="text">The key as written.</param>
    /// <param name="key">The key it names.</param>
    /// <returns>True when it names one of the keys on offer.</returns>
    public static bool TryParse(string? text, out HotKey key)
    {
        key = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        foreach (var choice in Choices)
        {
            if (choice.Label.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                key = choice;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a hotkey from configuration, falling back to the first choice.
    /// </summary>
    /// <param name="text">The key as written.</param>
    /// <returns>The key it names, or the default one.</returns>
    public static HotKey Resolve(string? text)
        => TryParse(text, out var key) ? key : Choices[0];

    /// <inheritdoc />
    public override string ToString()
        => Label;
}
