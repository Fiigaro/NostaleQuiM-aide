using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Stops the host when ESC or Q is pressed.
/// </summary>
/// <remarks>
/// Console.KeyAvailable throws when stdin is redirected - a pipe, a CI job, a service host - so the
/// watcher degrades to doing nothing rather than faulting the process. Ctrl+C still works either
/// way through the default lifetime.
/// </remarks>
public sealed class ConsoleExitService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConsoleExitService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleExitService"/> class.
    /// </summary>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public ConsoleExitService(IHostApplicationLifetime lifetime, ILogger<ConsoleExitService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            _logger.LogInformation("Console input is redirected; press Ctrl+C to stop.");
            return;
        }

        _logger.LogInformation("Press ESC or Q to stop.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var key = Console.ReadKey(intercept: true).Key;
                if (key is ConsoleKey.Escape or ConsoleKey.Q)
                {
                    _logger.LogInformation("{Key} pressed - shutting down gracefully.", key);
                    _lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (InvalidOperationException)
        {
            // No console attached after all.
            _logger.LogDebug("No interactive console available; key watcher disabled.");
        }
    }
}
