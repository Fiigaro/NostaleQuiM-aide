using Microsoft.Extensions.Logging;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Flashes each running client in turn so the player can tell which pid is which.
/// </summary>
/// <remarks>
/// Two clients of the same build share a title, a path and an icon. Listing them cannot resolve
/// that; making one of them blink can. Flashing rather than raising, because bringing a window to
/// the front would disrupt the very session being identified.
/// </remarks>
public static class ProcessIdentifier
{
    /// <summary>
    /// Flashes each windowed client, one at a time.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ProcessIdentifier));

        if (!OperatingSystem.IsWindows())
        {
            logger.LogError("Identifying a game window is Windows only.");
            return 1;
        }

        var verdicts = NosTaleProcessScanner.Scan();

        try
        {
            var windowed = verdicts.Where(v => v is { IsClient: true, HasWindow: true }).ToList();

            if (windowed.Count == 0)
            {
                logger.LogError("No NosTale client with a window is running.");
                return 3;
            }

            logger.LogWarning
            (
                "Watch your game windows. Each one will flash in turn, four seconds apart, " +
                "with its pid printed here just before."
            );

            foreach (var verdict in windowed)
            {
                logger.LogInformation("--> pid {Pid} is flashing NOW  ({Bounds})", verdict.Process.Id, verdict.Bounds ?? "position unknown");

                if (!NosTaleProcessScanner.Flash(verdict.Process))
                {
                    logger.LogWarning("    could not flash that window");
                }

                await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }

            logger.LogInformation("Then start with the one you want:  --pcap --pid <id>");
            return 0;
        }
        finally
        {
            foreach (var verdict in verdicts)
            {
                verdict.Process.Dispose();
            }
        }
    }
}
