namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// How the assembly talks to the game.
/// </summary>
public enum RunMode
{
    /// <summary>
    /// Frames are synthesised in-process. Runs anywhere, attaches to nothing.
    /// </summary>
    Simulate,

    /// <summary>
    /// Binds to a running NosTale process through NosSmooth.LocalClient. Windows x86 only.
    /// </summary>
    Attach,

    /// <summary>
    /// Reads the real client's traffic off the wire with NosSmooth.Pcap - no injection, no
    /// memory pattern scanning. Read-only by default: see <see cref="PcapOptions"/>.
    /// </summary>
    Pcap
}
