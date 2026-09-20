namespace NosSmoothCustomClient.Input;

/// <summary>
/// What the recorder has actually seen pressed, in words.
/// </summary>
/// <remarks>
/// A hotkey that does nothing has two completely different causes - the key never reaches us, or it
/// reaches us and what it triggers is broken - and from the outside they look identical. Reporting
/// the last key seen separates them in one press: nothing appears and the key is being swallowed
/// before anything here runs, which no amount of reading this code would have revealed. That is not
/// hypothetical: F11 went to the game's shop and F12 to Windows, and both looked like a recorder
/// that had stopped working.
/// </remarks>
public static class HotKeyWatch
{
    /// <summary>
    /// Describes the last hotkey seen.
    /// </summary>
    /// <param name="label">The key, or null when none has been seen.</param>
    /// <param name="ago">How long ago it was seen.</param>
    /// <param name="expected">The key that is supposed to start and stop recording.</param>
    /// <returns>The line to show, and whether it is good news.</returns>
    public static (string Text, bool Good) Describe(string? label, TimeSpan? ago, string expected)
    {
        if (label is null || ago is not { } since)
        {
            return
            (
                $"aucune touche de la liste captée pour l'instant — appuie sur {expected} dans le jeu "
                + "pour vérifier qu'elle arrive jusqu'ici",
                false
            );
        }

        var when = since < TimeSpan.FromSeconds(1) ? "à l'instant" : $"il y a {since.TotalSeconds:0}s";

        return label.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? ($"touche {label} captée {when}", true)
            : ($"touche {label} captée {when} — mais l'enregistrement écoute {expected}", false);
    }
}
