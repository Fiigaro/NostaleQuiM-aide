using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NosSmoothCustomClient.Diagnostics;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Delivers input straight to the game window's message queue.
/// </summary>
/// <remarks>
/// PostMessage is used rather than SendInput on purpose. SendInput injects at the system level and
/// therefore only reaches whichever window has focus, which would mean the machine could not be used
/// for anything else while the bot runs. PostMessage addresses a window handle directly, so the game
/// can sit in the background - at the cost of only working if the game reads its input from the
/// Windows message loop rather than through DirectInput. An old Win32 client generally does.
///
/// Messages carry a properly built lParam (repeat count, scan code, transition bits) because clients
/// that reconstruct the key from it ignore a message where it is left at zero.
/// </remarks>
public sealed class WindowsGameInput : IGameInput
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseMove = 0x0200;
    private const int MkLButton = 0x0001;
    private const uint WmChar = 0x0102;
    private const uint MapvkVkToVsc = 0;

    /// <summary>
    /// The Delphi form class of the NosTale client window.
    /// </summary>
    /// <remarks>
    /// Targeting the class rather than Process.MainWindowHandle matters: a client can own several
    /// windows and the "main" one is not necessarily the one that handles gameplay input. Existing
    /// tooling for this game addresses this class by name, which is a strong signal it is the right
    /// one.
    /// </remarks>
    public const string GameWindowClass = "TNosTaleMainF";

    private readonly CaptureTarget _target;
    private readonly ILogger<WindowsGameInput> _logger;

    private IntPtr _window = IntPtr.Zero;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsGameInput"/> class.
    /// </summary>
    /// <param name="target">The process the capture bound to; its window is the one driven.</param>
    /// <param name="logger">The logger.</param>
    public WindowsGameInput(CaptureTarget target, ILogger<WindowsGameInput> logger)
    {
        _target = target;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Description => "Windows window messages (works with the game in the background)";

    /// <summary>Gets the bound window handle, or zero.</summary>
    public IntPtr Window => _window;

    /// <inheritdoc />
    public bool TryAttach(out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Sending input to a game window is Windows only.";
            return false;
        }

        if (_target.Process is not { } process)
        {
            error = "No game process has been selected. Start in --pcap mode so the client is identified.";
            return false;
        }

        try
        {
            process.Refresh();

            var (handle, className) = FindGameWindow(process.Id, process.MainWindowHandle);

            if (handle == IntPtr.Zero)
            {
                error = $"Process {process.ProcessName} (pid {process.Id}) exposes no usable window. "
                        + "Pick the client you are actually playing with --pid.";

                return false;
            }

            _window = handle;
            error = string.Empty;

            _logger.LogInformation
            (
                "Input bound to window 0x{Handle:X} (class \"{Class}\") of pid {Pid}.",
                handle.ToInt64(),
                className,
                process.Id
            );

            if (!string.Equals(className, GameWindowClass, StringComparison.Ordinal))
            {
                _logger.LogWarning
                (
                    "That is not the expected \"{Expected}\" class. Input may not reach the game; "
                    + "if nothing happens, this is the first thing to look at.",
                    GameWindowClass
                );
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Could not reach the game window: " + ex.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public bool PressKey(GameKey key)
    {
        if (_window == IntPtr.Zero)
        {
            return false;
        }

        var scanCode = MapVirtualKey(key.VirtualKey, MapvkVkToVsc);

        // Repeat count 1, scan code in bits 16-23. On release, bits 30 and 31 mark the transition.
        var down = (IntPtr)(1 | (scanCode << 16));
        var up = (IntPtr)(1 | (scanCode << 16) | (1u << 30) | (1u << 31));

        var posted = PostMessage(_window, WmKeyDown, key.VirtualKey, down);
        posted &= PostMessage(_window, WmKeyUp, key.VirtualKey, up);

        if (!posted)
        {
            _logger.LogWarning("The game window refused key {Key}.", key.Label);
        }

        return posted;
    }

    /// <inheritdoc />
    public bool ClickAt(int x, int y)
    {
        if (_window == IntPtr.Zero)
        {
            return false;
        }

        var position = (IntPtr)((y << 16) | (x & 0xFFFF));

        // The move first: a client that tracks the cursor would otherwise register the click at
        // wherever it last believed the pointer to be.
        var posted = PostMessage(_window, WmMouseMove, 0, position);
        posted &= PostMessage(_window, WmLButtonDown, MkLButton, position);
        posted &= PostMessage(_window, WmLButtonUp, 0, position);

        if (!posted)
        {
            _logger.LogWarning("The game window refused a click at ({X},{Y}).", x, y);
        }

        return posted;
    }

    /// <summary>
    /// Sends a key as a character message rather than a key press.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True when the message was accepted for delivery.</returns>
    /// <remarks>
    /// A second way of saying the same thing. Some clients act on WM_KEYDOWN, others only on
    /// WM_CHAR; which one this build listens to is settled by trying, not by reasoning.
    /// </remarks>
    public bool SendCharacter(GameKey key)
    {
        if (_window == IntPtr.Zero || key.Label.Length != 1)
        {
            return false;
        }

        return PostMessage(_window, WmChar, key.Label[0], (IntPtr)1);
    }

    /// <summary>
    /// Finds the window that handles gameplay input.
    /// </summary>
    /// <param name="processId">The client's process id.</param>
    /// <param name="fallback">The handle to use when no better candidate is found.</param>
    /// <returns>The chosen handle and its class name.</returns>
    private static (IntPtr Handle, string ClassName) FindGameWindow(int processId, IntPtr fallback)
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

    private static string ReadClassName(IntPtr handle)
    {
        var buffer = new System.Text.StringBuilder(256);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    /// <summary>
    /// Reads the game window's size, for reporting and for sanity-checking click coordinates.
    /// </summary>
    /// <param name="width">The client width.</param>
    /// <param name="height">The client height.</param>
    /// <returns>True when the size could be read.</returns>
    public bool TryGetClientSize(out int width, out int height)
    {
        width = 0;
        height = 0;

        if (_window == IntPtr.Zero || !GetClientRect(_window, out var rect))
        {
            return false;
        }

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, uint wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buffer, int maxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
