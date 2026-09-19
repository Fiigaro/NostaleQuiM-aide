using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Finds the window of a NosTale client that actually takes gameplay input.
/// </summary>
/// <remarks>
/// <see cref="Process.MainWindowHandle"/> is not it. The client puts up more than one top level
/// window, and the one Windows nominates as "main" can be a shell that ignores keys and whose client
/// area has nothing to do with where the minimap is drawn. Everything that talks to the game has to
/// agree on which window it means, or input goes to one place and recorded coordinates refer to
/// another - so the search lives here rather than in each caller.
/// </remarks>
public static class GameWindowFinder
{
    /// <summary>The window class of the client's gameplay form, a Delphi window.</summary>
    public const string GameWindowClass = "TNosTaleMainF";

    /// <summary>
    /// Finds the gameplay window of a process.
    /// </summary>
    /// <param name="processId">The process id.</param>
    /// <param name="fallback">A handle to fall back to when nothing better is found.</param>
    /// <returns>The window and its class name, or zero when there is none.</returns>
    public static (IntPtr Handle, string ClassName) Find(int processId, IntPtr fallback)
    {
        var byClass = IntPtr.Zero;
        var nearMiss = IntPtr.Zero;
        var nearMissClass = string.Empty;

        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != processId)
            {
                return true;
            }

            var name = ReadClassName(handle);

            if (string.Equals(name, GameWindowClass, StringComparison.Ordinal))
            {
                byClass = handle;
                return false;
            }

            if (nearMiss == IntPtr.Zero && name.Contains("NosTale", StringComparison.OrdinalIgnoreCase))
            {
                nearMiss = handle;
                nearMissClass = name;
            }

            return true;
        }, IntPtr.Zero);

        if (byClass != IntPtr.Zero)
        {
            return (byClass, GameWindowClass);
        }

        return nearMiss != IntPtr.Zero
            ? (nearMiss, nearMissClass)
            : (fallback, fallback == IntPtr.Zero ? string.Empty : ReadClassName(fallback));
    }

    /// <summary>
    /// Reads a window's class name.
    /// </summary>
    /// <param name="handle">The window.</param>
    /// <returns>The class name, or an empty string.</returns>
    public static string ReadClassName(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
