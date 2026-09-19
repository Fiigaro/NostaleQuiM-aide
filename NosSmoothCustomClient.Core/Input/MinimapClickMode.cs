namespace NosSmoothCustomClient.Input;

/// <summary>
/// How a minimap click reaches the game.
/// </summary>
/// <remarks>
/// Two ways, and which one works is a property of the client build rather than something that can
/// be reasoned out. Posted messages leave the mouse alone and work with the game in the background,
/// but a client that hit-tests against the real cursor ignores them without a word. Moving the real
/// pointer cannot be ignored, and costs the use of the machine while the bot walks.
/// </remarks>
public enum MinimapClickMode
{
    /// <summary>Start with posted messages and switch to the cursor if the character never moves.</summary>
    Auto,

    /// <summary>Window messages only. Background friendly, silently ignored by some clients.</summary>
    Posted,

    /// <summary>Move the real mouse pointer and click. Works everywhere, takes over the cursor.</summary>
    RealCursor
}
