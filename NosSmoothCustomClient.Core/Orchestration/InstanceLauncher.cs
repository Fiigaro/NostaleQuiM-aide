using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Orchestration;

/// <summary>What the launcher is doing.</summary>
public enum LaunchState
{
    /// <summary>Nothing started.</summary>
    Idle,

    /// <summary>Playing the recorded steps.</summary>
    Running,

    /// <summary>Every step played.</summary>
    Done
}

/// <summary>
/// Plays the recorded sequence that opens an instance and walks into its first room.
/// </summary>
/// <remarks>
/// Everything before the first monster is interface: sitting down to wake the entrance up, the
/// mission window, START, the walk to the portal, the prompt that asks whether to go to the first
/// room. None of it is announced by a packet, so none of it can be decided - it can only be
/// recorded once and played back.
///
/// What keeps the playback honest is that each step names the state it was recorded against. The
/// map that has to have loaded is checked before the click that belongs to it; the spot that has to
/// have been reached is checked before the key that answers the prompt there. The recorded delay
/// stays as a floor, and a step whose condition never comes true is played anyway once its timeout
/// runs out, with a line saying so - a launch that stops halfway in silence is worse than one that
/// tries and can be seen to have failed.
/// </remarks>
public sealed class InstanceLauncher
{
    private readonly BotOptions _options;
    private readonly ProtocolStateManager _state;
    private readonly ILogger<InstanceLauncher> _logger;

    private int _index = -1;
    private DateTimeOffset _stepStartedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceLauncher"/> class.
    /// </summary>
    /// <param name="options">The bot options.</param>
    /// <param name="state">The state manager.</param>
    /// <param name="logger">The logger.</param>
    public InstanceLauncher(BotOptions options, ProtocolStateManager state, ILogger<InstanceLauncher> logger)
    {
        _options = options;
        _state = state;
        _logger = logger;
    }

    /// <summary>Gets what the launcher is doing.</summary>
    public LaunchState State { get; private set; } = LaunchState.Idle;

    /// <summary>Gets the step being played, counting from one, or zero.</summary>
    public int Step => _index + 1;

    /// <summary>Gets how many steps the sequence has.</summary>
    public int Count => _options.StartupSequence.Count;

    /// <summary>Gets the last thing the launcher did or waited for.</summary>
    public string Status { get; private set; } = "pas lancé";

    /// <summary>
    /// Starts the sequence from the beginning.
    /// </summary>
    /// <returns>True when there was a sequence to start.</returns>
    public bool Start()
    {
        if (_options.StartupSequence.Count == 0)
        {
            Status = "aucune séquence de lancement enregistrée";
            return false;
        }

        _index = 0;
        _stepStartedAt = DateTimeOffset.UtcNow;
        State = LaunchState.Running;
        Status = $"lancement : étape 1 / {Count}";

        _logger.LogInformation("Instance launch started: {Count} step(s).", Count);
        return true;
    }

    /// <summary>Stops the sequence where it is.</summary>
    public void Stop()
    {
        if (State != LaunchState.Running)
        {
            return;
        }

        _index = -1;
        State = LaunchState.Idle;
        Status = "lancement interrompu";
        _logger.LogInformation("Instance launch stopped.");
    }

    /// <summary>
    /// Plays at most one step.
    /// </summary>
    /// <param name="actuator">How the step reaches the game.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the launcher claims the tick.</returns>
    public async Task<bool> TickAsync(IBotActuator actuator, CancellationToken ct)
    {
        if (State != LaunchState.Running || _index < 0 || _index >= _options.StartupSequence.Count)
        {
            return false;
        }

        var step = _options.StartupSequence[_index];
        var elapsed = DateTimeOffset.UtcNow - _stepStartedAt;

        if (elapsed < TimeSpan.FromMilliseconds(step.WaitBeforeMs))
        {
            Status = $"lancement {Step}/{Count} : {step.Name} dans {(TimeSpan.FromMilliseconds(step.WaitBeforeMs) - elapsed).TotalSeconds:0.#}s";
            return true;
        }

        var timedOut = elapsed >= TimeSpan.FromMilliseconds(step.TimeoutMs);

        if (!IsReady(step) && !timedOut)
        {
            Status = $"lancement {Step}/{Count} : {step.Name} attend {step.Condition}";
            return true;
        }

        if (timedOut && !IsReady(step))
        {
            // Played anyway, and said out loud. The alternative is a launch that stops on a
            // condition nobody can see, which reads exactly like a bot that has crashed.
            _logger.LogWarning
            (
                "Launch step {Step}/{Count} ({Name}) never saw {Condition}; playing it anyway.",
                Step,
                Count,
                step.Name,
                step.Condition
            );
        }

        var sent = step.Action == StartupAction.Key
            ? await actuator.PressKeyAsync(step.Key ?? string.Empty, ct).ConfigureAwait(false)
            : await actuator.ClickSequenceAsync
            (
                new[] { new UiPoint(step.Name, step.X, step.Y, step.DoubleClick, WaitAfterMs: 0) },
                ct
            ).ConfigureAwait(false);

        _logger.LogInformation("Launch step {Step}/{Count}: {Step_}{Refused}", Step, Count, step, sent ? string.Empty : " - REFUSÉ");

        _index++;
        _stepStartedAt = DateTimeOffset.UtcNow;

        if (_index >= _options.StartupSequence.Count)
        {
            State = LaunchState.Done;
            Status = "lancement terminé - le bot prend la suite";
            _logger.LogInformation("Instance launch finished.");
        }
        else
        {
            Status = $"lancement {Step}/{Count} : {_options.StartupSequence[_index].Name}";
        }

        return true;
    }

    private bool IsReady(StartupStep step)
    {
        if (step.UntilMap is { } map && _state.CurrentMapId != map)
        {
            return false;
        }

        if (step.UntilX is { } x && step.UntilY is { } y)
        {
            if (!_state.HasPosition)
            {
                return false;
            }

            var position = _state.Position;
            if (ProtocolStateManager.Distance(position.X, position.Y, x, y) > _options.WaypointArrivalRadius)
            {
                return false;
            }
        }

        return true;
    }
}
