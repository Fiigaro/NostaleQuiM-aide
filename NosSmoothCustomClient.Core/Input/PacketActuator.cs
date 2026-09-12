using Microsoft.Extensions.Logging;
using NosSmooth.Packets;
using NosSmooth.Packets.Client.Battle;
using NosSmooth.Packets.Client.Inventory;
using NosSmooth.Packets.Enums.Entities;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Acts by sending packets. The original path, and the one the simulator exercises.
/// </summary>
/// <remarks>
/// Kept alongside the keyboard actuator rather than replaced by it: it is the version that can be
/// verified end to end without a game, so it stays the reference the decision logic is tested
/// against.
/// </remarks>
public sealed class PacketActuator : IBotActuator
{
    private readonly PacketDispatcher _dispatcher;
    private readonly IMovementStrategy _movement;
    private readonly ProtocolStateManager _state;
    private readonly BotOptions _options;
    private readonly ILogger<PacketActuator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketActuator"/> class.
    /// </summary>
    /// <param name="dispatcher">The outbound dispatcher.</param>
    /// <param name="movement">The movement strategy.</param>
    /// <param name="state">The state manager.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public PacketActuator
    (
        PacketDispatcher dispatcher,
        IMovementStrategy movement,
        ProtocolStateManager state,
        BotOptions options,
        ILogger<PacketActuator> logger
    )
    {
        _dispatcher = dispatcher;
        _movement = movement;
        _state = state;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Description => "packets - " + _movement.GetType().Name;

    /// <inheritdoc />
    public bool SupportsApproach => true;

    /// <inheritdoc />
    public bool TryPrepare(out string error)
    {
        error = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UseHpPotionAsync(CancellationToken ct = default)
        => _options.HpPotionSlot is { } slot && await SendAsync(new UseItemPacket(_options.PotionBag, slot), ct);

    /// <inheritdoc />
    public async Task<bool> UseMpPotionAsync(CancellationToken ct = default)
        => _options.MpPotionSlot is { } slot && await SendAsync(new UseItemPacket(_options.PotionBag, slot), ct);

    /// <inheritdoc />
    public async Task<bool> ApplyBuffAsync(BuffDefinition buff, CancellationToken ct = default)
    {
        if (buff.IsSkill)
        {
            return await SendAsync
            (
                new UseSkillPacket(buff.CastId!.Value, EntityType.Player, _state.OwnCharacterId, null, null),
                ct
            );
        }

        return buff.IsItem && await SendAsync(new UseItemPacket(buff.ItemBag!.Value, buff.ItemSlot!.Value), ct);
    }

    /// <inheritdoc />
    public Task<bool> TargetNearestAsync(CancellationToken ct = default)
    {
        // Packets address a target explicitly, so there is nothing to select first.
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> CastSkillAsync(SkillDefinition? skill, long targetEntityId, CancellationToken ct = default)
    {
        var castId = skill?.CastId ?? _options.BasicAttackCastId;
        var target = _state.Target;
        var entityType = target?.EntityType is { } type && type != EntityType.Map ? type : EntityType.Monster;

        return SendAsync(new UseSkillPacket(castId, entityType, targetEntityId, null, null), ct);
    }

    /// <inheritdoc />
    public Task<bool> LootAsync(CancellationToken ct = default)
    {
        // Server-side auto-loot is assumed; nothing to send.
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> ApproachAsync(int x, int y, CancellationToken ct = default)
        => MoveAsync(x, y, ct);

    /// <inheritdoc />
    public Task<bool> GoToWaypointAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _options.Waypoints.Count)
        {
            return Task.FromResult(false);
        }

        // Packets place one capped step at a time rather than jumping to the destination.
        var waypoint = _options.Waypoints[index];
        var position = _state.Position;
        var deltaX = Math.Clamp(waypoint.X - position.X, -_options.MaxStepDistance, _options.MaxStepDistance);
        var deltaY = Math.Clamp(waypoint.Y - position.Y, -_options.MaxStepDistance, _options.MaxStepDistance);

        return MoveAsync(position.X + deltaX, position.Y + deltaY, ct);
    }

    private async Task<bool> MoveAsync(int x, int y, CancellationToken ct)
    {
        var result = await _movement.MoveToAsync(x, y, ct).ConfigureAwait(false);
        return result.IsSuccess;
    }

    private async Task<bool> SendAsync(IPacket packet, CancellationToken ct)
    {
        var result = await _dispatcher.SendAsync(packet, ct).ConfigureAwait(false);
        return result.IsSuccess;
    }
}
