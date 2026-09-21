namespace NosSmoothCustomClient.Configuration;

/// <summary>
/// What a manual send is made of.
/// </summary>
/// <remarks>
/// The same panel covers all four because they answer the same question - "do this again, now, as
/// many times as I say" - and which of them is available is decided by the transport, not by the
/// operator's intent.
/// </remarks>
public enum SendKind
{
    /// <summary>A frame sent to the server, as though the client had sent it.</summary>
    PacketToServer,

    /// <summary>A frame handed to the client, as though the server had sent it.</summary>
    PacketToClient,

    /// <summary>A key pressed in the game window.</summary>
    Key,

    /// <summary>A click at a point of the game window, written <c>x,y</c>.</summary>
    Click
}

/// <summary>
/// Something to send, kept under a name so it survives the next launch.
/// </summary>
/// <param name="Name">What it does, in the operator's words.</param>
/// <param name="Kind">How the lines are delivered.</param>
/// <param name="Body">The lines, one instruction per line.</param>
/// <param name="Repetitions">How many times the whole body is played.</param>
/// <param name="IntervalMs">The wait between two consecutive sends.</param>
/// <remarks>
/// Retyping a frame at every launch is the friction this removes: the packet worth repeating is
/// found once, in the trace, and is then a name in a list.
/// </remarks>
public sealed record SendMacro
(
    string Name,
    SendKind Kind = SendKind.PacketToServer,
    string Body = "",
    int Repetitions = 1,
    int IntervalMs = 500
)
{
    /// <summary>The largest run the panel accepts, so a stray digit cannot start an hour of traffic.</summary>
    public const int MaxRepetitions = 10000;

    /// <summary>Gets the body split into instructions, blank lines dropped.</summary>
    public IReadOnlyList<string> Lines
        => Body.Split('\n')
               .Select(line => line.Trim('\r', ' ', '\t'))
               .Where(line => line.Length > 0)
               .ToList();

    /// <summary>Gets how many sends playing this macro will make.</summary>
    public int TotalSends
        => Lines.Count * Math.Clamp(Repetitions, 1, MaxRepetitions);

    /// <inheritdoc />
    public override string ToString()
        => Name;
}
