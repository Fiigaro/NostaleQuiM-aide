using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Plays the game the way a person does: quick bar keys and clicks on the minimap.
/// </summary>
/// <remarks>
/// This is what makes the bot work on a server whose client-to-server traffic cannot be forged.
/// Nothing is sent to the server directly; the client is asked to do things and produces its own,
/// perfectly ordinary packets. Reading still comes from the capture, so the decisions stay informed
/// - which is the whole difference between this and a macro pressing keys on a timer.
/// </remarks>
public sealed class InputActuator : IBotActuator
{
    private readonly IGameInput _input;
    private readonly BotOptions _options;
    private readonly ILogger<InputActuator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InputActuator"/> class.
    /// </summary>
    /// <param name="input">The input sender.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="logger">The logger.</param>
    public InputActuator(IGameInput input, BotOptions options, ILogger<InputActuator> logger)
    {
        _input = input;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Description => "keyboard and minimap - " + _input.Description;

    /// <inheritdoc />
    public bool SupportsApproach => false;

    /// <inheritdoc />
    public Task<bool> ApproachAsync(int x, int y, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public bool TryPrepare(out string error)
    {
        if (!_input.TryAttach(out error))
        {
            return false;
        }

        // A missing binding is not an error, it just means that action is unavailable. Saying so at
        // startup beats discovering mid-session that the bot never heals.
        Warn(_options.Keys.TargetAndAttack, "target and attack");
        Warn(_options.Keys.HpPotion, "HP potion");

        var unbound = _options.Skills.Count(s => s.Enabled && string.IsNullOrWhiteSpace(s.Key));
        if (unbound > 0)
        {
            _logger.LogWarning("{Count} rotation skill(s) have no key and will be skipped.", unbound);
        }

        var unreachable = _options.Waypoints.Count(w => !w.IsClickable);
        if (unreachable > 0)
        {
            _logger.LogWarning
            (
                "{Count} waypoint(s) have no minimap click point; the bot cannot walk to them. " +
                "Record them with --record-waypoints.",
                unreachable
            );
        }

        return true;
    }

    /// <inheritdoc />
    public Task<bool> UseHpPotionAsync(CancellationToken ct = default)
        => Task.FromResult(Press(_options.Keys.HpPotion, "HP potion"));

    /// <inheritdoc />
    public Task<bool> UseMpPotionAsync(CancellationToken ct = default)
        => Task.FromResult(Press(_options.Keys.MpPotion, "MP potion"));

    /// <inheritdoc />
    public Task<bool> ApplyBuffAsync(BuffDefinition buff, CancellationToken ct = default)
        => Task.FromResult(Press(buff.Key, $"buff {buff.Name}"));

    /// <inheritdoc />
    public Task<bool> TargetNearestAsync(CancellationToken ct = default)
        => Task.FromResult(Press(_options.Keys.TargetAndAttack, "target and attack"));

    /// <inheritdoc />
    public Task<bool> CastSkillAsync(SkillDefinition? skill, long targetEntityId, CancellationToken ct = default)
    {
        // The basic attack needs no key of its own: the target key already attacks.
        if (skill is null)
        {
            return Task.FromResult(Press(_options.Keys.TargetAndAttack, "basic attack"));
        }

        return Task.FromResult(Press(skill.Key, $"skill {skill.Name}"));
    }

    /// <inheritdoc />
    public Task<bool> LootAsync(CancellationToken ct = default)
        => Task.FromResult(Press(_options.Keys.Loot, "loot"));

    /// <inheritdoc />
    public Task<bool> GoToWaypointAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _options.Waypoints.Count)
        {
            return Task.FromResult(false);
        }

        var waypoint = _options.Waypoints[index];
        if (waypoint.ClickX is not { } x || waypoint.ClickY is not { } y)
        {
            _logger.LogDebug("Waypoint {Waypoint} has no minimap point, cannot walk there.", waypoint);
            return Task.FromResult(false);
        }

        return Task.FromResult(_input.ClickAt(x, y));
    }

    private bool Press(string? binding, string what)
    {
        if (!GameKey.TryParse(binding, out var key))
        {
            _logger.LogDebug("No key bound for {What}, skipping.", what);
            return false;
        }

        _logger.LogDebug("Pressing {Key} for {What}.", key.Label, what);
        return _input.PressKey(key);
    }

    private void Warn(string? binding, string what)
    {
        if (!GameKey.TryParse(binding, out _))
        {
            _logger.LogWarning("No key bound for {What}; that action is disabled.", what);
        }
    }
}
