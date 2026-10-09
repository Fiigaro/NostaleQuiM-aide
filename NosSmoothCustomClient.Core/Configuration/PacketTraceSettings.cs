namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// Which frames the trace is worth showing, as the settings file stores them.
/// </summary>
/// <remarks>
/// Split from the live <c>PacketFilter</c> in role only: the filter holds this very instance and
/// writes through it, so what is changed in the window is what the next save records, with no copy
/// to keep in step.
///
/// A trace that scrolls faster than it can be read is a trace nobody reads, and which headers are
/// noise depends entirely on what is being looked for - so the lists are settings rather than
/// constants. The defaults hide the handful of headers that make up the bulk of a NosTale session
/// and are almost never the answer; clear <see cref="Hide"/> to get the raw stream back.
/// </remarks>
public sealed class PacketTraceSettings
{
    /// <summary>
    /// The headers hidden unless asked for: entity movement, spawns and despawns, per-entity stat
    /// and condition refreshes, and the periodic housekeeping frames.
    /// </summary>
    public const string DefaultHidden = "mv in out cond eff st pairy rsfi fs char_sc";

    /// <summary>
    /// Gets or sets the only headers to show, separated by spaces or commas. Empty shows every
    /// header that <see cref="Hide"/> does not remove.
    /// </summary>
    public string Only { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the headers to drop, separated by spaces or commas.
    /// </summary>
    public string Hide { get; set; } = DefaultHidden;

    /// <summary>
    /// Gets or sets a value indicating whether server frames are shown.
    /// </summary>
    public bool ShowIncoming { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether client frames are shown.
    /// </summary>
    public bool ShowOutgoing { get; set; } = true;
}
