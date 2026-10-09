using Microsoft.Extensions.Logging;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Server.Maps;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.State;
using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Tracks which map the character is on.
/// </summary>
/// <remarks>
/// Three frames answer the question and none of them can be relied on alone. <c>c_map</c> is the
/// explicit map change and is the clearest, but it is only sent on the change itself - a bot started
/// while already in game never sees one. <c>at</c> carries the map id too and is sent on every
/// arrival and teleport, which is what makes the map knowable mid-session. <c>mapout</c> says we
/// have left without saying for where, and matters because the moment between maps is exactly when
/// acting on stale coordinates does damage.
/// </remarks>
public sealed class MapResponder :
    IPacketResponder<CMapPacket>,
    IPacketResponder<AtPacket>,
    IPacketResponder<MapoutPacket>
{
    private readonly ProtocolStateManager _state;
    private readonly BotOptions _options;
    private readonly ILogger<MapResponder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MapResponder"/> class.
    /// </summary>
    /// <param name="state">The state manager.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public MapResponder(ProtocolStateManager state, BotOptions options, ILogger<MapResponder> logger)
    {
        _state = state;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<CMapPacket> packetArgs, CancellationToken ct = default)
    {
        Enter(packetArgs.Packet.Id, "c_map");
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<AtPacket> packetArgs, CancellationToken ct = default)
    {
        var packet = packetArgs.Packet;

        // Only our own arrival says which map WE are on.
        if (_state.OwnCharacterId < 0 || _state.OwnCharacterId == packet.CharacterId)
        {
            Enter(packet.MapId, "at");
        }

        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public Task<Result> Respond(PacketEventArgs<MapoutPacket> packetArgs, CancellationToken ct = default)
    {
        if (_state.CurrentMapId >= 0)
        {
            _logger.LogInformation("mapout -> left map {MapId}; waiting to be told where we landed.", _state.CurrentMapId);
        }

        // -1 is "unknown", which the navigation guard reads as "do not walk a route yet".
        _state.EnterMap(-1);
        return Task.FromResult(Result.FromSuccess());
    }

    private void Enter(int mapId, string source)
    {
        if (!_state.EnterMap(mapId))
        {
            return;
        }

        var routeMap = _options.RouteMapId;

        var note = routeMap is null
            ? "no map is recorded for the patrol route, so it will be walked here as well"
            : routeMap == mapId
                ? "this is the route's own map"
                : $"the patrol route belongs to map {routeMap}, so walking it is on hold here";

        _logger.LogInformation
        (
            "{Source} -> now on map {MapId} ({Note}). Entity table and target cleared.",
            source,
            mapId,
            note
        );
    }
}
