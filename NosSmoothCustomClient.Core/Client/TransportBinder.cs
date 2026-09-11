using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using NosSmooth.Core.Client;
using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Resolves the transport up front and turns its setup failures into actionable messages.
/// </summary>
/// <remarks>
/// Binding the client is the step that touches the outside world - it enumerates processes, and for
/// capture it loads the native libpcap bindings. Doing it explicitly, early and guarded means a
/// missing Npcap or a client that is not running reads as the setup problem it is, rather than
/// surfacing later as an unhandled dependency injection failure with a stack trace.
/// </remarks>
public static class TransportBinder
{
    /// <summary>
    /// Attempts to bind the transport.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="mode">The transport.</param>
    /// <param name="error">The message to show the operator, when binding failed.</param>
    /// <returns>True when the transport is ready.</returns>
    public static bool TryBind(IServiceProvider services, RunMode mode, out string error)
    {
        try
        {
            services.GetRequiredService<INostaleClient>();
            error = string.Empty;
            return true;
        }
        catch (NosTaleProcessNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (IsMissingNativeLibrary(ex))
        {
            error = mode == RunMode.Pcap
                ? "The packet capture driver could not be loaded. Install Npcap (npcap.com) with " +
                  "\"WinPcap API-compatible Mode\" ticked, then run again." + Environment.NewLine +
                  "Underlying error: " + Unwrap(ex).Message
                : "A native dependency could not be loaded: " + Unwrap(ex).Message;

            return false;
        }
        catch (Win32Exception ex)
        {
            error = "Windows refused the operation while binding the transport: " + ex.Message +
                    Environment.NewLine +
                    "Packet capture needs an elevated prompt. Run the terminal as administrator.";

            return false;
        }
        catch (Exception ex) when (mode != RunMode.Simulate)
        {
            // Anything else from a real transport is still a setup problem from the operator's
            // point of view; the simulator is left to fail loudly because a fault there is a bug.
            error = "The transport could not be started: " + Unwrap(ex).Message;
            return false;
        }
    }

    private static bool IsMissingNativeLibrary(Exception ex)
        => Unwrap(ex) is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static Exception Unwrap(Exception ex)
        => ex is TypeInitializationException { InnerException: { } inner } ? Unwrap(inner) : ex;
}
