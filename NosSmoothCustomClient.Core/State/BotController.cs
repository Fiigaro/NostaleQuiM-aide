namespace NosSmoothCustomClient.State;

/// <summary>
/// The run/pause switch for the orchestration loop.
/// </summary>
/// <remarks>
/// Pausing stops the loop from making decisions but leaves the packet pipeline running, so the
/// state manager stays current and the loop resumes against fresh data rather than a stale
/// snapshot.
/// </remarks>
public sealed class BotController
{
    private int _running = 1;

    /// <summary>
    /// Raised when the run state changes. The argument is the new running state.
    /// </summary>
    public event Action<bool>? StateChanged;

    /// <summary>Gets a value indicating whether the loop is currently acting.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Resumes the loop.</summary>
    public void Start() => Set(true);

    /// <summary>Pauses the loop.</summary>
    public void Pause() => Set(false);

    /// <summary>Flips the run state.</summary>
    /// <returns>The new running state.</returns>
    public bool Toggle()
    {
        var next = !IsRunning;
        Set(next);
        return next;
    }

    private void Set(bool running)
    {
        var value = running ? 1 : 0;
        if (Interlocked.Exchange(ref _running, value) == value)
        {
            return;
        }

        StateChanged?.Invoke(running);
    }
}
