using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Extensions;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Drives <see cref="INostaleClient.RunAsync"/> for the lifetime of the host.
/// </summary>
/// <remarks>
/// This is the pump that turns inbound frames into responder invocations, whichever client is
/// registered. If it stops on its own the process has nothing left to react to, so the host is
/// brought down with it rather than left spinning an orchestration loop over frozen state.
/// </remarks>
public sealed class NostaleClientHostedService : BackgroundService
{
    private readonly INostaleClient _client;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<NostaleClientHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NostaleClientHostedService"/> class.
    /// </summary>
    /// <param name="client">The NosTale client.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public NostaleClientHostedService
    (
        INostaleClient client,
        IHostApplicationLifetime lifetime,
        ILogger<NostaleClientHostedService> logger
    )
    {
        _client = client;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await _client.RunAsync(stoppingToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogError("The NosTale client stopped with an error: {Error}", result.ToFullString());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The NosTale client faulted.");
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("The client pump ended; shutting the host down.");
            _lifetime.StopApplication();
        }
    }
}
