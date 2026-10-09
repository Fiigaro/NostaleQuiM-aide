using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using NosSmooth.Packets.Client.Movement;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Reads what our own client sends, which is the only thing that is ours beyond doubt.
/// </summary>
/// <remarks>
/// Capture sees both directions of the connection, and that second direction answers a question the
/// server side cannot when a session is joined already underway: which character is ours. The
/// server names the controlled character in <c>at</c>, sent on map entry and on teleport and never
/// again - so a bot started next to a standing character learns nothing about it, including from
/// the <c>mv</c> packets that would otherwise track it, since those are only ours once the id is
/// known. A walk packet needs no such introduction: it was sent by the client we are listening to.
///
/// Read twice, from the raw line and from the deserialised packet, because only one of them is
/// guaranteed. Typed responders depend on the client's own packets being parsed and dispatched like
/// the server's, which is a property of the transport rather than of this code; the raw line is
/// handed out whatever happens, as the packet trace shows. Both paths end in the same call, and
/// recording the same walk twice is recording it once.
/// </remarks>
public sealed class OwnMovementResponder : IRawPacketResponder, IPacketResponder<WalkPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly ILogger<OwnMovementResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnMovementResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public OwnMovementResponder(ProtocolStateManager state, ILogger<OwnMovementResponder> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Reads the coordinates out of a raw walk line.
    /// </summary>
    /// <param name="packet">The raw packet line.</param>
    /// <param name="x">The destination X.</param>
    /// <param name="y">The destination Y.</param>
    /// <returns>True when the line is a walk with coordinates.</returns>
    /// <remarks>
    /// Tolerant about what comes before the header on purpose: a client line may carry a sequence
    /// number in front of it, and which of the two forms arrives is not something this code gets to
    /// decide.
    /// </remarks>
    public static bool TryReadWalk(string? packet, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (string.IsNullOrWhiteSpace(packet))
        {
            return false;
        }

        var parts = packet.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length - 2 && i < 2; i++)
        {
            if (!parts[i].Equals("walk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(parts[i + 1], out x) && int.TryParse(parts[i + 2], out y);
        }

        return false;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs packetArgs, CancellationToken ct = default)
    {
        if (packetArgs.Source == PacketSource.Client && TryReadWalk(packetArgs.PacketString, out var x, out var y))
        {
            Note(x, y);
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<WalkPacket> packetArgs, CancellationToken ct = default)
    {
        // Only what we sent. The same packet shape arriving from the server would be about somebody
        // else, and taking it for our own is how a bot ends up walking to another player's errands.
        if (packetArgs.Source == PacketSource.Client)
        {
            Note(packetArgs.Packet.PositionX, packetArgs.Packet.PositionY);
        }

        return Task.FromResult(Result.FromSuccess());
    }

    private void Note(int x, int y)
    {
        var hadPosition = _state.HasPosition;
        _state.NoteWalkIntent(x, y);

        if (!hadPosition && _state.HasPosition)
        {
            _logger.LogInformation
            (
                "Position learned from our own walk: ({X},{Y}). The server only announces it on map entry.",
                x,
                y
            );
        }
    }
}
