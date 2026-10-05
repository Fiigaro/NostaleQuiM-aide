using System.Globalization;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Diagnostics;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// What a run of a macro did.
/// </summary>
/// <param name="Sent">How many sends went out.</param>
/// <param name="Requested">How many were asked for.</param>
/// <param name="Stopped">Whether it was stopped rather than finished.</param>
/// <param name="Error">Why it stopped early, when it did.</param>
/// <param name="Warning">Something true of the whole run that the count does not say.</param>
public readonly record struct SendOutcome(int Sent, int Requested, bool Stopped, string? Error = null, string? Warning = null)
{
    /// <summary>Gets a value indicating whether everything asked for went out.</summary>
    public bool Complete => Error is null && !Stopped && Sent == Requested;

    /// <summary>Gets a sentence describing the run, for the panel.</summary>
    /// <returns>The sentence.</returns>
    public string Describe()
    {
        var head = Error is not null
            ? $"Arrêté à {Sent}/{Requested} : {Error}"
            : Stopped
                ? $"Arrêté à la demande, {Sent}/{Requested} envoyés."
                : $"{Sent}/{Requested} envoyés.";

        return Warning is null ? head : head + " " + Warning;
    }
}

/// <summary>
/// Sends what the operator asks for, as many times as asked, outside the decision loop.
/// </summary>
/// <remarks>
/// The loop decides when to act from what it reads; this does not decide anything. It exists for
/// the actions the game paces rather than the server - an upgrade window that spends its seconds on
/// an animation, a panel that has to be answered once per attempt - where the work is one frame
/// repeated and the only thing missing is a way to repeat it.
///
/// One run at a time, on purpose. Two overlapping runs would interleave their frames, and neither
/// count would then describe what the server actually received.
/// </remarks>
public sealed class ManualSender
{
    private readonly INostaleClient _client;
    private readonly IBotActuator _actuator;
    private readonly PacketLog _log;
    private readonly SwitchableGameInput? _input;
    private readonly RunMode _mode;
    private readonly ILogger<ManualSender> _logger;

    private CancellationTokenSource? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualSender"/> class.
    /// </summary>
    /// <param name="client">The transport.</param>
    /// <param name="actuator">The way keys and clicks reach the game.</param>
    /// <param name="log">The packet ring, so a manual frame shows up in the trace like any other.</param>
    /// <param name="input">The live/dry-run switch, when the transport has one.</param>
    /// <param name="mode">The transport, which decides what can be sent at all.</param>
    /// <param name="logger">The logger.</param>
    public ManualSender
    (
        INostaleClient client,
        IBotActuator actuator,
        PacketLog log,
        SwitchableGameInput? input,
        RunMode mode,
        ILogger<ManualSender> logger
    )
    {
        _client = client;
        _actuator = actuator;
        _log = log;
        _input = input;
        _mode = mode;
        _logger = logger;
    }

    /// <summary>Raised when a run starts or ends.</summary>
    public event Action? Changed;

    /// <summary>Gets a value indicating whether a run is in progress.</summary>
    public bool IsBusy => _current is not null;

    /// <summary>
    /// Says why a kind of send cannot work here, or null when it can.
    /// </summary>
    /// <param name="kind">The kind of send.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// Asked before the button is offered rather than after it is pressed. The capture transport
    /// reads the wire and has no way to write to it - <c>NosSmooth.Pcap</c> leaves both of its
    /// send methods unimplemented - so a packet button there is a button that can only throw.
    /// </remarks>
    public string? WhyUnavailable(SendKind kind)
    {
        if (kind is not (SendKind.PacketToServer or SendKind.PacketToClient))
        {
            return null;
        }

        return _mode == RunMode.Pcap
            ? "Le mode capture (--pcap) ne fait que lire le trafic : il ne sait pas en émettre. "
              + "Pour envoyer des paquets, lance en --attach ; sinon utilise « Touche » ou « Clic »."
            : null;
    }

    /// <summary>Stops the run in progress, if there is one.</summary>
    public void Stop()
        => _current?.Cancel();

    /// <summary>
    /// Plays a macro.
    /// </summary>
    /// <param name="macro">What to send, how, and how often.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What the run did.</returns>
    public async Task<SendOutcome> RunAsync(SendMacro macro, CancellationToken ct = default)
    {
        var lines = macro.Lines;
        if (lines.Count == 0)
        {
            return new SendOutcome(0, 0, false, "il n'y a rien à envoyer.");
        }

        if (WhyUnavailable(macro.Kind) is { } unavailable)
        {
            return new SendOutcome(0, 0, false, unavailable);
        }

        // Parsed before the first send, never during: a sequence that stops halfway through because
        // its fourth line was mistyped leaves the game in a state nobody asked for.
        var points = new List<UiPoint>();
        if (macro.Kind == SendKind.Click)
        {
            foreach (var line in lines)
            {
                if (!TryReadPoint(line, out var point, out var why))
                {
                    return new SendOutcome(0, 0, false, why);
                }

                points.Add(point);
            }
        }

        var repetitions = Math.Clamp(macro.Repetitions, 1, SendMacro.MaxRepetitions);
        var interval = TimeSpan.FromMilliseconds(Math.Max(0, macro.IntervalMs));
        var requested = lines.Count * repetitions;

        var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (Interlocked.CompareExchange(ref _current, running, null) is not null)
        {
            running.Dispose();
            return new SendOutcome(0, requested, false, "un envoi est déjà en cours.");
        }

        Changed?.Invoke();

        _logger.LogInformation
        (
            "Envoi manuel : {Count} x {Kind} « {First} », toutes les {Interval}ms.",
            requested,
            macro.Kind,
            lines[0],
            interval.TotalMilliseconds
        );

        var sent = 0;
        string? error = null;

        try
        {
            for (var pass = 1; pass <= repetitions && error is null; pass++)
            {
                for (var index = 0; index < lines.Count; index++)
                {
                    running.Token.ThrowIfCancellationRequested();

                    var ok = macro.Kind == SendKind.Click
                        ? await ClickAsync(points[index], running.Token).ConfigureAwait(false)
                        : await SendOneAsync(macro.Kind, Fill(lines[index], pass), running.Token).ConfigureAwait(false);

                    if (!ok.Success)
                    {
                        error = ok.Error;
                        break;
                    }

                    sent++;

                    // Not after the last one: the wait is what separates two sends, and there is
                    // nothing left to separate it from.
                    if (interval > TimeSpan.Zero && sent < requested)
                    {
                        await Task.Delay(interval, running.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Envoi manuel arrêté après {Sent}/{Requested}.", sent, requested);
            return Finish(running, new SendOutcome(sent, requested, true, null, Caveat(macro.Kind)));
        }

        if (error is not null)
        {
            _logger.LogWarning("Envoi manuel interrompu après {Sent}/{Requested} : {Error}", sent, requested, error);
        }
        else
        {
            _logger.LogInformation("Envoi manuel terminé : {Sent}/{Requested}.", sent, requested);
        }

        return Finish(running, new SendOutcome(sent, requested, false, error, Caveat(macro.Kind)));
    }

    /// <summary>
    /// Reads a click point written <c>x,y</c>, optionally followed by <c>double</c>.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="point">The point read.</param>
    /// <param name="error">Why the line could not be read.</param>
    /// <returns>True when it was read.</returns>
    public static bool TryReadPoint(string line, out UiPoint point, out string error)
    {
        point = default;

        var parts = line.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            error = $"« {line} » n'est pas un point : écris x,y (par exemple 467,460).";
            return false;
        }

        var twice = parts.Length > 2
                    && parts[2].StartsWith("d", StringComparison.OrdinalIgnoreCase);

        // The wait between two clicks is the macro's interval, so the point itself asks for none.
        point = new UiPoint("clic", x, y, twice, WaitAfterMs: 0);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Replaces the placeholders a line may carry.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="pass">Which pass is being played, counting from one.</param>
    /// <returns>The line to send.</returns>
    public static string Fill(string line, int pass)
        => line.Replace("{i}", pass.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private string? Caveat(SendKind kind)
        => kind is SendKind.Key or SendKind.Click && _input is { IsLive: false }
            ? "Attention : l'entrée est en simulation, donc rien n'a atteint le jeu. Passe en direct pour qu'elle y arrive."
            : null;

    private SendOutcome Finish(CancellationTokenSource running, SendOutcome outcome)
    {
        Interlocked.CompareExchange(ref _current, null, running);
        running.Dispose();
        Changed?.Invoke();
        return outcome;
    }

    private async Task<(bool Success, string? Error)> ClickAsync(UiPoint point, CancellationToken ct)
    {
        try
        {
            return await _actuator.ClickSequenceAsync(new[] { point }, ct).ConfigureAwait(false)
                ? (true, null)
                : (false, $"le clic {point.X},{point.Y} n'a pas pu être envoyé ({_actuator.Description}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string? Error)> SendOneAsync(SendKind kind, string line, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case SendKind.Key:
                    return await _actuator.PressKeyAsync(line, ct).ConfigureAwait(false)
                        ? (true, null)
                        : (false, $"la touche « {line} » n'a pas pu être envoyée ({_actuator.Description}).");

                case SendKind.PacketToClient:
                {
                    var received = await _client.ReceivePacketAsync(line, ct).ConfigureAwait(false);
                    if (!received.IsSuccess)
                    {
                        return (false, received.Error?.Message ?? "le transport a refusé la trame.");
                    }

                    _log.Add(PacketSource.Server, line);
                    return (true, null);
                }

                default:
                {
                    var result = await _client.SendPacketAsync(line, ct).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return (false, result.Error?.Message ?? "le transport a refusé la trame.");
                    }

                    _log.Add(PacketSource.Client, line);
                    return (true, null);
                }
            }
        }
        catch (NotImplementedException)
        {
            return (false, "ce transport n'implémente pas l'envoi de paquets. Lance en --attach pour en envoyer.");
        }
        catch (NotSupportedException)
        {
            return (false, "ce transport n'accepte pas l'envoi de paquets. Lance en --attach pour en envoyer.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
