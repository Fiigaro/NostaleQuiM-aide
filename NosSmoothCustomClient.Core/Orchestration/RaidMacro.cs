using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Orchestration;

/// <summary>
/// Plays the raid loop: a key, Enter, a minimap click, attack for a while, and start over.
/// </summary>
/// <remarks>
/// A macro on purpose, and a separate one. It reads nothing from the game, so it cannot be stopped
/// by a packet that never arrives - which is the one failure everything smarter in this program has
/// kept running into. And it pauses the main loop when it starts, because the two would otherwise
/// press keys into the same client at the same time and each undo what the other just did.
///
/// It goes through the same switchable input as everything else, so the JOUE switch still governs
/// it: refused outright while that switch is off, rather than running and quietly sending nothing,
/// which reads exactly like a macro that is broken.
/// </remarks>
public sealed class RaidMacro
{
    private readonly IGameInput _input;
    private readonly BotOptions _options;
    private readonly BotController _controller;
    private readonly ILogger<RaidMacro> _logger;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="RaidMacro"/> class.
    /// </summary>
    /// <param name="input">How keys and clicks reach the game.</param>
    /// <param name="options">The bot options.</param>
    /// <param name="controller">The main loop's run/pause switch.</param>
    /// <param name="logger">The logger.</param>
    public RaidMacro(IGameInput input, BotOptions options, BotController controller, ILogger<RaidMacro> logger)
    {
        _input = input;
        _options = options;
        _controller = controller;
        _logger = logger;
    }

    /// <summary>Raised whenever the step or the cycle changes.</summary>
    public event Action? Changed;

    /// <summary>Gets a value indicating whether the loop is running.</summary>
    public bool Running { get; private set; }

    /// <summary>Gets how many full cycles have been played since the last start.</summary>
    public int Cycles { get; private set; }

    /// <summary>Gets what the loop is doing right now.</summary>
    public string Status { get; private set; } = "arrêté";

    /// <summary>
    /// Says what is missing before the loop can run, or null when nothing is.
    /// </summary>
    /// <returns>The reason, in words, or null.</returns>
    public string? WhyNot()
    {
        var raid = _options.Raid;

        if (!GameKey.TryParse(raid.Key, out _))
        {
            return "aucune touche de départ valable (une lettre, un chiffre, espace ou entrée)";
        }

        if (raid.ClickX is null || raid.ClickY is null)
        {
            return "aucun point de clic sur la minimap : capture-le d'abord";
        }

        if (!GameKey.TryParse(raid.AttackKey, out _))
        {
            return "aucune touche d'attaque valable";
        }

        if (raid.AttackSeconds <= 0)
        {
            return "la durée d'attaque doit être supérieure à zéro";
        }

        if (_input is SwitchableGameInput { IsLive: false })
        {
            return "le bot est en simulation : passe en JOUE, sinon rien ne part vers le jeu";
        }

        return null;
    }

    /// <summary>
    /// Starts the loop.
    /// </summary>
    /// <param name="error">Why it did not start, when it did not.</param>
    /// <returns>True when the loop is now running.</returns>
    public bool Start(out string error)
    {
        lock (_sync)
        {
            if (Running)
            {
                error = string.Empty;
                return true;
            }

            if (WhyNot() is { } reason)
            {
                error = reason;
                Report(reason);
                return false;
            }

            // Two things pressing keys into one client is two things undoing each other.
            if (_controller.IsRunning)
            {
                _controller.Pause();
                _logger.LogInformation("Raid loop starting: the main loop is paused so the two do not fight over the keyboard.");
            }

            Cycles = 0;
            Running = true;
            _cts = new CancellationTokenSource();

            var mine = _cts;
            _loop = Task.Run(() => RunAsync(mine));

            error = string.Empty;
            return true;
        }
    }

    /// <summary>Stops the loop where it is.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!Running)
            {
                return;
            }

            _cts?.Cancel();
            Running = false;
        }

        Report($"arrêté après {Cycles} cycle(s)");
    }

    /// <summary>
    /// Plays one full cycle.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the cycle has been played.</returns>
    /// <remarks>
    /// Public so a check can drive exactly one, against a recording input, without a clock.
    /// </remarks>
    public async Task PlayCycleAsync(CancellationToken ct)
    {
        var raid = _options.Raid;

        GameKey.TryParse(raid.Key, out var first);
        GameKey.TryParse("entrée", out var enter);
        GameKey.TryParse(raid.AttackKey, out var attack);

        Report($"cycle {Cycles + 1} : touche {first.Label}");
        _input.PressKey(first);
        await Task.Delay(raid.AfterKeyMs, ct).ConfigureAwait(false);

        Report($"cycle {Cycles + 1} : Entrée");
        _input.PressKey(enter);
        await Task.Delay(raid.AfterEnterMs, ct).ConfigureAwait(false);

        var x = raid.ClickX ?? 0;
        var y = raid.ClickY ?? 0;
        Report($"cycle {Cycles + 1} : clic minimap ({x},{y})");
        _input.ClickAt(x, y);
        await Task.Delay(raid.AfterClickMs, ct).ConfigureAwait(false);

        var started = Stopwatch.GetTimestamp();
        var duration = TimeSpan.FromSeconds(raid.AttackSeconds);
        var lastShown = -1;

        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp());
            if (elapsed >= duration)
            {
                break;
            }

            // Once a second is plenty: the line is for a person, and rewriting it at every press
            // would only make it flicker.
            var left = (int)Math.Ceiling((duration - elapsed).TotalSeconds);
            if (left != lastShown)
            {
                lastShown = left;
                Report($"cycle {Cycles + 1} : attaque ({attack.Label}) — encore {left} s");
            }

            _input.PressKey(attack);
            await Task.Delay(Math.Max(1, raid.AttackIntervalMs), ct).ConfigureAwait(false);
        }

        Cycles++;
        _logger.LogInformation("Raid cycle {Cycle} done.", Cycles);
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await PlayCycleAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raid loop failed.");
            Report("erreur : " + ex.Message);
        }
        finally
        {
            // Only the loop that is still the current one may say it has stopped. A stop followed
            // at once by a start leaves the old loop finishing after the new one began, and letting
            // it clear the flag would show a running loop as stopped.
            lock (_sync)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    Running = false;
                }
            }

            Changed?.Invoke();
        }
    }

    private void Report(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
