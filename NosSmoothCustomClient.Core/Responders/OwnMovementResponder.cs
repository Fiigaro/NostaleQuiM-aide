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
/// </remarks>
public sealed class OwnMovementResponder : IPacketResponder<WalkPacket>
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

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<WalkPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // Only what we sent. The same packet shape arriving from the server would be about somebody
        // else, and taking it for our own is how a bot ends up walking to another player's errands.
        if (packetArgs.Source != PacketSource.Client)
        {
            return Task.FromResult(Result.FromSuccess());
        }

        var hadPosition = _state.HasPosition;
        _state.NoteWalkIntent(packet.PositionX, packet.PositionY);

        if (!hadPosition && _state.HasPosition)
        {
            _logger.LogInformation
            (
                "Position learned from our own walk: ({X},{Y}). The server only announces it on map entry.",
                packet.PositionX,
                packet.PositionY
            );
        }

        return Task.FromResult(Result.FromSuccess());
    }
}
