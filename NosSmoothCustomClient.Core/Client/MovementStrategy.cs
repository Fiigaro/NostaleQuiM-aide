using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Commands.Walking;
using NosSmooth.Packets.Client.Movement;
using NosSmoothCustomClient.Configuration;
using Remora.Results;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Dispatches one movement frame towards a destination.
/// </summary>
public interface IMovementStrategy
{
    /// <summary>
    /// Moves the character towards the given cell.
    /// </summary>
    /// <param name="x">The destination X coordinate.</param>
    /// <param name="y">The destination Y coordinate.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A result.</returns>
    Task<Result> MoveToAsync(int x, int y, CancellationToken ct = default);
}

/// <summary>
/// Computes the checksum byte carried by the outbound <c>walk</c> packet.
/// </summary>
/// <remarks>
/// NosSmooth models the field but never computes it, because its local client walks by calling the
/// game's own movement routine rather than by emitting packets. The value is therefore
/// client-build specific and this default - the formula used by the common private-server
/// codebases - MUST be validated against the target server before the packet path is trusted; many
/// builds ignore the field entirely, some disconnect on a mismatch. Swap this single
/// implementation rather than editing the strategy.
/// </remarks>
public static class WalkChecksumCalculator
{
    /// <summary>
    /// Computes the walk checksum for a destination cell.
    /// </summary>
    /// <param name="x">The destination X coordinate.</param>
    /// <param name="y">The destination Y coordinate.</param>
    /// <returns>The checksum byte.</returns>
    public static byte Compute(int x, int y)
        => (byte)((x + y) % 3 % 2);
}

/// <summary>
/// Emits raw <c>walk</c> packets. This is the packet-level path, used against the simulated client
/// and against server builds that accept synthesised movement frames.
/// </summary>
public sealed class PacketWalkStrategy : IMovementStrategy
{
    private readonly PacketDispatcher _dispatcher;
    private readonly BotOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketWalkStrategy"/> class.
    /// </summary>
    /// <param name="dispatcher">The outbound dispatcher.</param>
    /// <param name="options">The bot options.</param>
    public PacketWalkStrategy(PacketDispatcher dispatcher, BotOptions options)
    {
        _dispatcher = dispatcher;
        _options = options;
    }

    /// <inheritdoc />
    public Task<Result> MoveToAsync(int x, int y, CancellationToken ct = default)
        => _dispatcher.SendAsync
        (
            new WalkPacket((short)x, (short)y, WalkChecksumCalculator.Compute(x, y), _options.WalkSpeed),
            ct
        );
}

/// <summary>
/// Walks through NosSmooth's <see cref="WalkCommand"/>, which drives the client's own movement
/// routine. This is the correct path when attached to a real process: the game builds a valid
/// frame itself, so no checksum has to be guessed and user input is arbitrated properly.
/// </summary>
public sealed class CommandWalkStrategy : IMovementStrategy
{
    private readonly INostaleClient _client;
    private readonly ILogger<CommandWalkStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandWalkStrategy"/> class.
    /// </summary>
    /// <param name="client">The NosTale client.</param>
    /// <param name="logger">The logger.</param>
    public CommandWalkStrategy(INostaleClient client, ILogger<CommandWalkStrategy> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> MoveToAsync(int x, int y, CancellationToken ct = default)
    {
        _logger.LogDebug("Issuing WalkCommand to ({X},{Y}).", x, y);

        var command = new WalkCommand
        (
            (short)x,
            (short)y,
            ReturnDistanceTolerance: 2,
            Pets: Array.Empty<(long, short, short)>(),
            CanBeCancelledByAnother: true,
            WaitForCancellation: false,
            AllowUserCancel: true
        );

        return await _client.SendCommandAsync(command, ct).ConfigureAwait(false);
    }
}
