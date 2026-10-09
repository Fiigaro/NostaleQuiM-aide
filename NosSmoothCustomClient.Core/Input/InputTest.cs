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
    /// <param name="singleKey">A single key to test, instead of the full sequence.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(int? processId, ILoggerFactory loggerFactory, string? singleKey = null)
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
            "In 5 seconds this runs two tests. Click on ANOTHER window now and leave NosTale in " +
            "the background - that is the whole point."
        );

        for (var remaining = 5; remaining > 0; remaining--)
        {
            logger.LogInformation("{Remaining}...", remaining);
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        // A single named key, for confirming one behaviour without sitting through the rest.
        if (singleKey is not null)
        {
            if (!GameKey.TryParse(singleKey, out var only))
            {
                logger.LogError("\"{Key}\" is not a key I can send. Use a digit, a letter, or \"space\".", singleKey);
                return 6;
            }

            logger.LogWarning("Sending \"{Key}\" three times, one second apart.", only.Label);

            var sent = 0;
            for (var i = 0; i < 3; i++)
            {
                sent += input.PressKey(only) ? 1 : 0;
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            logger.LogInformation("Queued {Sent}/3. Now look at the game and tell me what happened.", sent);
            return sent == 3 ? 0 : 5;
        }

        GameKey.TryParse("1", out var one);
        GameKey.TryParse("2", out var two);

        // Two ways of saying the same thing. Which one a given client listens to is settled by
        // trying both once, rather than by another round trip of guessing.
        logger.LogWarning("TEST A - key press messages (WM_KEYDOWN/WM_KEYUP). Sending \"1\" three times.");
        var keyAccepted = 0;
        for (var i = 0; i < 3; i++)
        {
            keyAccepted += input.PressKey(one) ? 1 : 0;
            await Task.Delay(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        logger.LogWarning("TEST B - character messages (WM_CHAR). Sending \"2\" three times.");
        var charAccepted = 0;
        for (var i = 0; i < 3; i++)
        {
            charAccepted += input.SendCharacter(two) ? 1 : 0;
            await Task.Delay(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        logger.LogWarning("TEST C - space, which should select and attack the nearest monster.");
        var spaceAccepted = input.PressKey(GameKey.Space) ? 1 : 0;

        logger.LogInformation
        (
            "Queued: test A {KeyAccepted}/3, test B {CharAccepted}/3, test C {SpaceAccepted}/1.",
            keyAccepted,
            charAccepted,
            spaceAccepted
        );

        logger.LogWarning
        (
            "Now tell me what the character actually did. Queued only means Windows accepted the " +
            "message; acting on it is the client's decision, and that is what decides how the bot " +
            "is built. Did skill 1 fire (A), skill 2 (B), both, or neither?"
        );

        var accepted = keyAccepted + charAccepted + spaceAccepted;
        var total = 7;

        return accepted == total ? 0 : 5;
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
