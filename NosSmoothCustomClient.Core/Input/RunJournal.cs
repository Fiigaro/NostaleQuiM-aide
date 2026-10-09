using System.Collections.Concurrent;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// A place for responders to say what just happened, so a recorded run can name its own turning
/// points.
/// </summary>
/// <remarks>
/// A count of monsters is not an account of a fight. The table holds what the server has announced
/// as visible, and <c>out</c> is sent when an entity leaves view, not when it dies - so the number
/// rises and falls with where the character walks, and a room going quiet looks identical to a
/// room being cleared. What separates them is the packet that did it, which only the responder
/// handling it knows. Writing it down here is what turns "38 became 0" into something a replay can
/// wait for.
/// </remarks>
public sealed class RunJournal
{
    private readonly ConcurrentQueue<string> _pending = new();

    /// <summary>Gets or sets a value indicating whether notes are being kept.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Notes something worth a line in the recorded run.
    /// </summary>
    /// <param name="what">What happened, in the operator's words.</param>
    public void Note(string what)
    {
        if (!Enabled)
        {
            return;
        }

        _pending.Enqueue(what);
    }

    /// <summary>
    /// Takes everything noted since the last drain.
    /// </summary>
    /// <returns>The notes, oldest first.</returns>
    public IReadOnlyList<string> Drain()
    {
        var taken = new List<string>();

        while (_pending.TryDequeue(out var note))
        {
            taken.Add(note);
        }

        return taken;
    }

    /// <summary>Discards anything not yet drained.</summary>
    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }
    }
}
