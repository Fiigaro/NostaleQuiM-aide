using System.Text;

namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// Settings for the packet-capture transport.
/// </summary>
/// <remarks>
/// This transport reads the real client's TCP traffic through libpcap. It needs no injection and no
/// memory pattern scanning, which is what makes it usable on a modified client where the sigscan
/// bindings would not resolve. It requires Npcap on Windows and an elevated process.
/// </remarks>
public sealed class PcapOptions
{
    /// <summary>
    /// Gets or sets the NosTale process to listen to. When null, the process is discovered
    /// automatically and the first match is used.
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// Gets or sets the encryption key of the world connection, or 0 when it is not known.
    /// </summary>
    /// <remarks>
    /// Zero is the right value when attaching to a session that is already logged in: the key is
    /// recovered from the stream. Set it only if you have captured the handshake.
    /// </remarks>
    public int InitialEncryptionKey { get; set; }

    /// <summary>
    /// Gets or sets the code page used to decode packet text, or null for <see cref="Encoding.Latin1"/>.
    /// </summary>
    /// <remarks>
    /// Latin1 is the safe default: it is built into .NET and matches windows-1252 across the byte
    /// range NosTale actually uses for packet text. Set a code page only if accented characters in
    /// names come out wrong, and note that anything other than Latin1 or UTF-8 needs
    /// CodePagesEncodingProvider registered.
    /// </remarks>
    public int? EncodingCodePage { get; set; }

    /// <summary>
    /// Gets the encoding to decode packets with.
    /// </summary>
    /// <returns>The encoding.</returns>
    public Encoding ResolveEncoding()
    {
        if (EncodingCodePage is not { } codePage)
        {
            return Encoding.Latin1;
        }

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (NotSupportedException)
        {
            // The code page is not registered in this runtime; Latin1 is closer than failing.
            return Encoding.Latin1;
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }
}
