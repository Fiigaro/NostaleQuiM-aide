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
    private const uint MapvkVkToVsc = 0;

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
            var handle = process.MainWindowHandle;

            if (handle == IntPtr.Zero)
            {
                error = $"Process {process.ProcessName} (pid {process.Id}) has no window. "
                        + "Pick the client you are actually playing with --pid.";

                return false;
            }

            _window = handle;
            error = string.Empty;

            _logger.LogInformation
            (
                "Input bound to \"{Title}\" (pid {Pid}, window 0x{Handle:X}).",
                process.MainWindowTitle,
                process.Id,
                handle.ToInt64()
            );

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
