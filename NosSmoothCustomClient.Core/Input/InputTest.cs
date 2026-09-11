using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NosSmoothCustomClient.Client;
using NosSmoothCustomClient.Diagnostics;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Settles, empirically, whether the game accepts input while it sits in the background.
/// </summary>
/// <remarks>
/// The whole shape of the bot depends on the answer. If window messages reach the client, the
/// machine stays usable while it farms; if they do not, the game must hold focus and nothing else
/// can be done at the same time. That is not worth guessing at, and it takes one run to know.
/// </remarks>
public static class InputTest
{
    /// <summary>
    /// Runs the test against the selected client.
    /// </summary>
    /// <param name="processId">An explicitly chosen process, or null to detect one.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(int? processId, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(InputTest));

        if (!OperatingSystem.IsWindows())
        {
            logger.LogError("Sending input to a game window is Windows only.");
            return 1;
        }

        var target = new CaptureTarget();

        try
        {
            target.Process = SelectProcess(processId, logger);
        }
        catch (NosTaleProcessNotFoundException ex)
        {
            logger.LogError("{Message}", ex.Message);
            return 3;
        }

        var input = new WindowsGameInput(target, loggerFactory.CreateLogger<WindowsGameInput>());

        if (!input.TryAttach(out var error))
        {
            logger.LogError("{Error}", error);
            return 4;
        }

        if (input.TryGetClientSize(out var width, out var height))
        {
            logger.LogInformation("Game client area: {Width}x{Height} pixels.", width, height);
        }

        logger.LogWarning
        (
            "In 5 seconds this will send: 1, then 2, then space. " +
            "Click on ANOTHER window now and leave NosTale in the background - that is the whole point of the test."
        );

        for (var remaining = 5; remaining > 0; remaining--)
        {
            logger.LogInformation("{Remaining}...", remaining);
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        var sequence = new[] { "1", "2", "space" };
        var accepted = 0;

        foreach (var name in sequence)
        {
            if (!GameKey.TryParse(name, out var key))
            {
                continue;
            }

            var ok = input.PressKey(key);
            accepted += ok ? 1 : 0;
            logger.LogInformation("Sent {Key} -> {Result}", key.Label, ok ? "accepted by the queue" : "REFUSED");

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        logger.LogInformation("{Accepted}/{Total} messages were accepted for delivery.", accepted, sequence.Length);
        logger.LogWarning
        (
            "Now look at the game. Accepted only means Windows queued the message - " +
            "whether the client acted on it is what you have to judge on screen. " +
            "If your character cast two skills and attacked, background input works and the bot can run while you do something else."
        );

        return accepted == sequence.Length ? 0 : 5;
    }

    private static System.Diagnostics.Process SelectProcess(int? processId, ILogger logger)
    {
        if (processId is { } pid)
        {
            return System.Diagnostics.Process.GetProcessById(pid);
        }

        var verdicts = NosTaleProcessScanner.Scan();
        var clients = verdicts.Where(v => v.IsClient).ToList();
        var windowed = clients.Where(v => v.HasWindow).ToList();

        try
        {
            if (windowed.Count == 1)
            {
                logger.LogInformation("Using {Description}", windowed[0].Describe());
                return windowed[0].Process;
            }

            if (clients.Count == 0)
            {
                throw new NosTaleProcessNotFoundException("No running NosTale client was found. Start the game and log in first.");
            }

            throw new NosTaleProcessNotFoundException
            (
                $"{clients.Count} clients are running and {windowed.Count} have a window, so the choice is yours. Use --pid <id>:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, clients.Select(c => "  " + c.Describe()))
            );
        }
        finally
        {
            foreach (var verdict in verdicts)
            {
                if (windowed.Count != 1 || !ReferenceEquals(verdict, windowed[0]))
                {
                    verdict.Process.Dispose();
                }
            }
        }
    }
}
