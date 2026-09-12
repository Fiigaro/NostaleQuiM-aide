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
    /// <param name="play">Whether actions actually reach the game.</param>
    public static void Apply(IServiceProvider services, RunMode mode, bool explicitlyPaused, bool play = false, bool recordWaypoints = false)
    {
        var controller = services.GetRequiredService<BotController>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ModeStartupPolicy));

        if (recordWaypoints)
        {
            // The loop must not wander off while the route is being walked by hand.
            controller.Pause();
            return;
        }

        if (mode == RunMode.Pcap && play)
        {
            services.GetRequiredService<Input.SwitchableGameInput>().SetLive(true);

            // The bot is about to press real keys in a real client. Starting stopped means the
            // operator chooses the moment, rather than discovering it mid-pull.
            controller.Pause();
            logger.LogWarning
            (
                "PLAY MODE: keystrokes will reach the game. Starting PAUSED - press P when you are " +
                "somewhere safe and ready."
            );

            return;
        }

        if (mode == RunMode.Pcap)
        {
            logger.LogInformation
            (
                "Dry run: every decision is logged as [WOULD PRESS] and nothing reaches the game. " +
                "Play normally and check the decisions look right, then add --play."
            );
        }

        if (explicitlyPaused)
        {
            controller.Pause();
        }
    }
}
