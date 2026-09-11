using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient;

/// <summary>
/// Decides whether the loop may act as soon as the host starts.
/// </summary>
public static class ModeStartupPolicy
{
    /// <summary>
    /// Applies the run/pause policy for a transport, honouring an explicit request to start paused.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="mode">The transport.</param>
    /// <param name="explicitlyPaused">Whether the user asked for a paused start.</param>
    public static void Apply(IServiceProvider services, RunMode mode, bool explicitlyPaused)
    {
        var controller = services.GetRequiredService<BotController>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ModeStartupPolicy));

        if (mode == RunMode.Pcap)
        {
            // Capture can observe faithfully but cannot intercept: a packet it "sends" travels
            // alongside the client's own, so the server sees it twice. NosSmooth documents this as
            // detectable. Acting is therefore never the default here - the operator has to opt in
            // deliberately, at runtime, knowing the cost.
            controller.Pause();
            logger.LogWarning
            (
                "Capture transport starts READ-ONLY. Sending over pcap duplicates every frame " +
                "server-side, which is detectable. Press P to let the loop act anyway."
            );

            return;
        }

        if (explicitlyPaused)
        {
            controller.Pause();
        }
    }
}
