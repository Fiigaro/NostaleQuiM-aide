namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// A place to click in the game's own interface, and how.
/// </summary>
/// <param name="Name">What it is, in the operator's words.</param>
/// <param name="X">X inside the game window.</param>
/// <param name="Y">Y inside the game window.</param>
/// <param name="DoubleClick">Whether it takes two clicks rather than one.</param>
/// <param name="WaitAfterMs">How long to leave the client to react before the next one.</param>
/// <remarks>
/// Unlike a waypoint this carries no map coordinates, and cannot: a panel is drawn over the window,
/// not placed in the world. That also makes it the one part of a sequence the server never mentions
/// - no packet announces a reward screen - so these points are recorded by hand and played in order,
/// with the arrival of the items they produce as the only evidence they landed.
/// </remarks>
public readonly record struct UiPoint(string Name, int X, int Y, bool DoubleClick = false, int WaitAfterMs = 800)
{
    /// <inheritdoc />
    public override string ToString()
        => $"{Name} ({X},{Y}){(DoubleClick ? " double-clic" : string.Empty)}";
}
