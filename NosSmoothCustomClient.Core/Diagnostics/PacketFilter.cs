using System.Collections.Concurrent;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Diagnostics;

/// <summary>
/// One frame the transport carried, or one this bot sent.
/// </summary>
/// <param name="Index">A number that only grows, so a reader can tell what it has already shown.</param>
/// <param name="At">When it was seen.</param>
/// <param name="Source">Which side it came from.</param>
/// <param name="Packet">The frame itself.</param>
public readonly record struct PacketLine(long Index, DateTimeOffset At, PacketSource Source, string Packet)
{
    /// <summary>Gets the header the frame is addressed by.</summary>
    public string Header => PacketFilter.HeaderOf(Packet);

    /// <summary>Gets the direction, padded so columns line up.</summary>
    public string Direction => Source == PacketSource.Server ? "IN " : "OUT";

    /// <inheritdoc />
    public override string ToString()
        => $"{At:HH:mm:ss.fff}  {Direction}  {Packet}";
}

/// <summary>
/// A bounded ring of the frames that went past, kept whole.
/// </summary>
/// <remarks>
/// Deliberately unfiltered. Filtering on the way in would mean that narrowing the view throws away
/// what was already captured, and that widening it shows nothing until new traffic arrives - so the
/// header being looked for would have to be guessed before it was ever seen, which is the opposite
/// of what a trace is for. Everything is kept; <see cref="PacketFilter"/> decides what is read back.
/// </remarks>
public sealed class PacketLog
{
    private readonly ConcurrentQueue<PacketLine> _lines = new();
    private readonly int _capacity;
    private long _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketLog"/> class.
    /// </summary>
    /// <param name="capacity">How many frames to retain.</param>
    public PacketLog(int capacity = 4000)
        => _capacity = capacity;

    /// <summary>Gets how many frames have been recorded since the start, evictions included.</summary>
    public long Total => Interlocked.Read(ref _next);

    /// <summary>Records a frame.</summary>
    /// <param name="source">Which side it came from.</param>
    /// <param name="packet">The frame.</param>
    public void Add(PacketSource source, string packet)
    {
        _lines.Enqueue(new PacketLine(Interlocked.Increment(ref _next), DateTimeOffset.Now, source, packet));

        while (_lines.Count > _capacity && _lines.TryDequeue(out _))
        {
            // Evicting the overflow.
        }
    }

    /// <summary>Forgets everything retained, without resetting the running count.</summary>
    public void Clear()
    {
        while (_lines.TryDequeue(out _))
        {
            // Draining.
        }
    }

    /// <summary>Takes a snapshot of the retained frames, oldest first.</summary>
    /// <returns>The frames.</returns>
    public IReadOnlyList<PacketLine> Snapshot()
        => _lines.ToArray();
}

/// <summary>
/// Decides which frames are worth showing.
/// </summary>
/// <remarks>
/// Applied in two places for two reasons. On the way to the log it is what keeps a capture readable
/// at all, because the console front-end has no way to scroll back through what it already printed.
/// On the way to the window it is applied on read instead, so tightening or loosening the lists
/// re-reads the frames already captured rather than only affecting the next ones.
/// </remarks>
public sealed class PacketFilter
{
    private static readonly char[] Separators = { ' ', ',', ';', '\t', '\n', '\r' };

    private readonly PacketTraceSettings _settings;
    private readonly object _gate = new();

    private HashSet<string> _only = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _hide = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _search = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketFilter"/> class.
    /// </summary>
    /// <param name="settings">The stored lists, which this filter writes through.</param>
    public PacketFilter(PacketTraceSettings settings)
    {
        _settings = settings;
        Reparse();
    }

    /// <summary>Raised whenever what the filter accepts has changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets or sets a value indicating whether accepted frames also reach the log.
    /// </summary>
    /// <remarks>
    /// Off outside capture, where the transport prints its own frames and a second copy would
    /// double every line. The window reads <see cref="PacketLog"/> either way, so turning this off
    /// quietens the journal without blinding the packet view.
    /// </remarks>
    public bool TraceToLog { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether capture is frozen.
    /// </summary>
    /// <remarks>
    /// A live view scrolling at several frames a second cannot be read, let alone copied from. This
    /// stops what is recorded, not what is transmitted - the bot keeps running.
    /// </remarks>
    public bool Paused { get; set; }

    /// <summary>Gets or sets the only headers to show. Empty means every header not hidden.</summary>
    public string Only
    {
        get => _settings.Only;
        set => Update(() => _settings.Only = value ?? string.Empty);
    }

    /// <summary>Gets or sets the headers to drop.</summary>
    public string Hide
    {
        get => _settings.Hide;
        set => Update(() => _settings.Hide = value ?? string.Empty);
    }

    /// <summary>Gets or sets text that an accepted frame must contain. Not persisted.</summary>
    public string Search
    {
        get => _search;
        set
        {
            _search = value ?? string.Empty;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets a value indicating whether server frames are shown.</summary>
    public bool ShowIncoming
    {
        get => _settings.ShowIncoming;
        set => Update(() => _settings.ShowIncoming = value);
    }

    /// <summary>Gets or sets a value indicating whether client frames are shown.</summary>
    public bool ShowOutgoing
    {
        get => _settings.ShowOutgoing;
        set => Update(() => _settings.ShowOutgoing = value);
    }

    /// <summary>
    /// Reads the header a frame is addressed by.
    /// </summary>
    /// <param name="packet">The frame.</param>
    /// <returns>The header, or an empty string for a frame that has none.</returns>
    /// <remarks>
    /// Three shapes have to be recognised, or filtering quietly misses the frames it was pointed
    /// at. A plain frame is <c>header args...</c>. A client frame may carry the sequence number the
    /// game counts with in front of it - the capture transport strips it, an attached client does
    /// not. And the interface frames are written <c>#header^args</c>.
    /// </remarks>
    public static string HeaderOf(string packet)
    {
        var frame = packet.AsSpan().Trim();
        if (frame.IsEmpty)
        {
            return string.Empty;
        }

        if (frame[0] == '#')
        {
            frame = frame[1..];
            var caret = frame.IndexOf('^');
            return (caret < 0 ? frame : frame[..caret]).ToString();
        }

        var space = frame.IndexOf(' ');
        if (space < 0)
        {
            return frame.ToString();
        }

        var first = frame[..space];
        if (!long.TryParse(first, out _))
        {
            return first.ToString();
        }

        // A leading number is the client's sequence counter, never a header of its own.
        var rest = frame[(space + 1)..].TrimStart();
        var second = rest.IndexOf(' ');
        return (second < 0 ? rest : rest[..second]).ToString();
    }

    /// <summary>
    /// Restores the lists a fresh install starts with.
    /// </summary>
    public void Reset()
        => Update(() =>
        {
            _settings.Only = string.Empty;
            _settings.Hide = PacketTraceSettings.DefaultHidden;
            _settings.ShowIncoming = true;
            _settings.ShowOutgoing = true;
            _search = string.Empty;
        });

    /// <summary>
    /// Shows everything, whatever the lists said.
    /// </summary>
    public void ShowEverything()
        => Update(() =>
        {
            _settings.Only = string.Empty;
            _settings.Hide = string.Empty;
            _settings.ShowIncoming = true;
            _settings.ShowOutgoing = true;
            _search = string.Empty;
        });

    /// <summary>
    /// Decides whether a frame is shown.
    /// </summary>
    /// <param name="source">Which side it came from.</param>
    /// <param name="packet">The frame.</param>
    /// <returns>True when it passes.</returns>
    public bool Allows(PacketSource source, string packet)
    {
        if (source == PacketSource.Server ? !_settings.ShowIncoming : !_settings.ShowOutgoing)
        {
            return false;
        }

        var search = _search;
        if (search.Length > 0 && !packet.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var header = HeaderOf(packet);

        lock (_gate)
        {
            if (_only.Count > 0)
            {
                // A whitelist is an explicit answer to "show me this one", so it outranks the noise
                // list: a header named in both is one the operator has just asked for.
                return _only.Contains(header);
            }

            return !_hide.Contains(header);
        }
    }

    /// <summary>Gets a one-line description of what is being shown.</summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_settings.Only))
        {
            parts.Add("seulement " + string.Join(" ", Split(_settings.Only)));
        }
        else if (!string.IsNullOrWhiteSpace(_settings.Hide))
        {
            parts.Add("sans " + string.Join(" ", Split(_settings.Hide)));
        }
        else
        {
            parts.Add("tout");
        }

        if (!_settings.ShowIncoming)
        {
            parts.Add("sortants seulement");
        }
        else if (!_settings.ShowOutgoing)
        {
            parts.Add("entrants seulement");
        }

        if (_search.Length > 0)
        {
            parts.Add($"contenant « {_search} »");
        }

        return string.Join(", ", parts);
    }

    private static IEnumerable<string> Split(string list)
        => list.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Update(Action change)
    {
        lock (_gate)
        {
            change();
            Reparse();
        }

        Changed?.Invoke();
    }

    private void Reparse()
    {
        _only = new HashSet<string>(Split(_settings.Only), StringComparer.OrdinalIgnoreCase);
        _hide = new HashSet<string>(Split(_settings.Hide), StringComparer.OrdinalIgnoreCase);
    }
}
