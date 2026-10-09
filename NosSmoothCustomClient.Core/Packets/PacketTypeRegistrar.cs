using System.Reflection;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Extensions;
using NosSmooth.Packets;
using NosSmooth.PacketSerializer.Extensions;
using NosSmooth.PacketSerializer.Packets;
using Remora.Results;

namespace NosSmoothCustomClient.Packets;

/// <summary>
/// Fills the packet type repository with the stock NosTale packets and this assembly's custom ones.
/// </summary>
/// <remarks>
/// Assembly scanning is the fast path. When it fails - a partially loadable assembly, a packet whose
/// converter was not generated - the scan is abandoned for an explicit per-type registration so that
/// one bad definition cannot silently drop every other custom packet to
/// <see cref="UnresolvedPacket"/>.
/// </remarks>
public sealed class PacketTypeRegistrar
{
    private readonly IPacketTypesRepository _repository;
    private readonly ILogger<PacketTypeRegistrar> _logger;

    /// <summary>
    /// The custom packet types, used as the fallback when assembly scanning does not work out.
    /// </summary>
    private static readonly Type[] ExplicitCustomPacketTypes =
    {
        typeof(QuiMStatPacket),
        typeof(QuiMTargetPacket)
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketTypeRegistrar"/> class.
    /// </summary>
    /// <param name="repository">The packet types repository.</param>
    /// <param name="logger">The logger.</param>
    public PacketTypeRegistrar(IPacketTypesRepository repository, ILogger<PacketTypeRegistrar> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Registers the stock and custom packet types.
    /// </summary>
    /// <returns>A result that fails only when the stock packets could not be registered.</returns>
    public Result Register()
    {
        var defaults = _repository.AddDefaultPackets();
        if (!defaults.IsSuccess)
        {
            _logger.LogError("Could not register the stock NosTale packets: {Error}", defaults.ToFullString());
            return defaults;
        }

        _logger.LogInformation("Registered the stock NosTale packet types.");

        var customAssembly = typeof(PacketTypeRegistrar).Assembly;
        var custom = _repository.AddPacketTypes(customAssembly);
        if (custom.IsSuccess)
        {
            _logger.LogInformation
            (
                "Registered {Count} custom packet type(s) from {Assembly}.",
                CountCustomPacketTypes(customAssembly),
                customAssembly.GetName().Name
            );

            return Result.FromSuccess();
        }

        _logger.LogWarning
        (
            "Scanning {Assembly} for packet types failed ({Error}). Falling back to explicit registration.",
            customAssembly.GetName().Name,
            custom.ToFullString()
        );

        return RegisterExplicitly();
    }

    private Result RegisterExplicitly()
    {
        var registered = 0;

        foreach (var packetType in ExplicitCustomPacketTypes)
        {
            var result = _repository.AddPacketType(packetType);
            if (result.IsSuccess)
            {
                registered++;
                continue;
            }

            // One unusable packet must not take the rest of the table down with it.
            _logger.LogError
            (
                "Could not register custom packet {Packet}: {Error}",
                packetType.Name,
                result.ToFullString()
            );
        }

        _logger.LogInformation
        (
            "Explicitly registered {Registered}/{Total} custom packet type(s).",
            registered,
            ExplicitCustomPacketTypes.Length
        );

        return registered > 0
            ? Result.FromSuccess()
            : new InvalidOperationError("None of the custom packet types could be registered.");
    }

    private static int CountCustomPacketTypes(Assembly assembly)
        => assembly.GetTypes().Count(t => typeof(IPacket).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
}
