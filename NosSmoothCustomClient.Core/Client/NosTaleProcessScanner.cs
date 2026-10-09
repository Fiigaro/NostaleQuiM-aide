using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// One process and why it was or was not taken for a NosTale client.
/// </summary>
/// <param name="Process">The process.</param>
/// <param name="ExecutablePath">Its executable, when readable.</param>
/// <param name="IsClient">Whether it was taken for a client.</param>
/// <param name="Reason">Why.</param>
/// <param name="WindowTitle">Its main window title, when it has one.</param>
/// <param name="StartedAt">When it started, when readable.</param>
/// <param name="Bounds">Where its window sits on screen, when it has one.</param>
public sealed record ProcessVerdict
(
    Process Process,
    string? ExecutablePath,
    bool IsClient,
    string Reason,
    string? WindowTitle = null,
    DateTime? StartedAt = null,
    string? Bounds = null
)
{
    /// <summary>Gets a value indicating whether the process owns a visible main window.</summary>
    public bool HasWindow => !string.IsNullOrWhiteSpace(WindowTitle);

    /// <summary>Gets a one-line description used to tell sibling clients apart.</summary>
    public string Describe()
    {
        var window = HasWindow ? $"\"{WindowTitle}\"" : "(no window)";
        var started = StartedAt is { } at ? at.ToString("HH:mm:ss") : "?";

        // Two clients of the same build share a title and a path; where the window sits is what
        // actually tells them apart on screen.
        var where = Bounds is { Length: > 0 } b ? $"  at {b}" : string.Empty;

        return $"{Process.ProcessName,-14} pid {Process.Id,-7} started {started}  {window}{where}";
    }
}

/// <summary>
/// Finds the NosTale client among the running processes.
/// </summary>
/// <remarks>
/// This deliberately does not use NosBrowserManager.IsProcessNostaleProcess. That predicate does
/// <c>Directory.Exists(Path.Combine(Path.GetDirectoryName(mainModule.FileName), "NostaleData"))</c>,
/// and GetDirectoryName yields an empty string - not null - when the file name carries no directory.
/// Path.Combine then produces the bare relative path "NostaleData", which Directory.Exists resolves
/// against the <i>current working directory</i>. One such folder next to the bot makes every
/// inspectable process look like a client: on a real machine this matched 116 processes and bound
/// the capture to lsass.
///
/// So the path is required to be rooted, and executables under the Windows directory are refused
/// outright - a game client never lives there, and that single rule is what keeps a detection bug
/// from ever pointing the capture at a system process.
/// </remarks>
public static class NosTaleProcessScanner
{
    /// <summary>
    /// Inspects every running process.
    /// </summary>
    /// <returns>One verdict per process. Callers must dispose the processes they do not keep.</returns>
    public static IReadOnlyList<ProcessVerdict> Scan()
    {
        var verdicts = new List<ProcessVerdict>();

        foreach (var process in Process.GetProcesses())
        {
            verdicts.Add(Inspect(process));
        }

        return verdicts;
    }

    private static ProcessVerdict Inspect(Process process)
    {
        string? path;

        try
        {
            path = process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return new ProcessVerdict(process, null, false, "not inspectable (protected or other bitness)");
        }
        catch (InvalidOperationException)
        {
            return new ProcessVerdict(process, null, false, "exited during the scan");
        }
        catch (NotSupportedException)
        {
            return new ProcessVerdict(process, null, false, "not supported on this platform");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ProcessVerdict(process, null, false, "no executable path");
        }

        var directory = Path.GetDirectoryName(path);

        // The guard that matters: an empty or relative directory would turn the check below into a
        // lookup against our own working directory, which is how every process starts matching.
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
        {
            return new ProcessVerdict(process, path, false, "executable path is not absolute");
        }

        if (IsSystemDirectory(directory))
        {
            return new ProcessVerdict(process, path, false, "system process");
        }

        var hasGameData = Directory.Exists(Path.Combine(directory, "NostaleData"));

        return new ProcessVerdict
        (
            process,
            path,
            hasGameData,
            hasGameData ? "NostaleData found next to the executable" : "no NostaleData directory",
            ReadWindowTitle(process),
            ReadStartTime(process),
            ReadBounds(process)
        );
    }

    private static string? ReadWindowTitle(Process process)
    {
        try
        {
            // The discriminator when several clients of the same build are running: the one being
            // played owns a window, a leftover or background instance usually does not.
            return process.MainWindowHandle == IntPtr.Zero ? null : process.MainWindowTitle;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadBounds(Process process)
    {
        try
        {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            {
                return null;
            }

            return $"({rect.Left},{rect.Top}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Flashes a client's window so it can be told apart from its siblings.
    /// </summary>
    /// <param name="process">The process whose window to flash.</param>
    /// <returns>True when the request was accepted.</returns>
    /// <remarks>
    /// Flashing rather than raising: bringing a window to the front while the player is in another
    /// one would disrupt exactly the session being identified.
    /// </remarks>
    public static bool Flash(Process process)
    {
        try
        {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = handle,
                Flags = FlashAll | FlashTimerNoForeground,
                Count = 6,
                Timeout = 0
            };

            return FlashWindowEx(ref info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static DateTime? ReadStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private const uint FlashAll = 0x00000003;
    private const uint FlashTimerNoForeground = 0x0000000C;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private static bool IsSystemDirectory(string directory)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return !string.IsNullOrEmpty(windows)
               && directory.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }
}
