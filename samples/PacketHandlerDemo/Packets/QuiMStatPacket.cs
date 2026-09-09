using NosSmooth.Packets;
using NosSmooth.PacketSerializer.Abstractions.Attributes;

namespace PacketHandlerDemo.Packets;

/// <summary>
/// Paquet textuel personnalise annoncant le score d'un joueur sur une manche.
/// </summary>
/// <remarks>
/// Forme sur le fil : <c>quim 42 2 1250 Quiz de printemps</c>
/// <para>
/// L'en-tete (<c>quim</c>) est consomme par le serialiseur : l'index 0
/// correspond donc au premier champ APRES l'en-tete.
/// </para>
/// <para>
/// Trois choses sont indispensables pour qu'un paquet personnalise fonctionne :
/// 1. implementer <see cref="IPacket"/> ;
/// 2. porter <see cref="PacketHeaderAttribute"/> (en-tete + source) ;
/// 3. porter <see cref="GenerateSerializerAttribute"/> pour que le generateur
///    Roslyn produise le convertisseur associe.
/// </para>
/// </remarks>
/// <param name="PlayerId">L'identifiant du joueur.</param>
/// <param name="Category">La categorie de la manche.</param>
/// <param name="Score">Le score obtenu.</param>
/// <param name="Label">Le libelle de la manche (peut contenir des espaces).</param>
[PacketHeader("quim", PacketSource.Server)]
[GenerateSerializer(true)]
public record QuiMStatPacket
(
    [PacketIndex(0)]
    long PlayerId,
    [PacketIndex(1)]
    QuiMCategory Category,
    [PacketIndex(2)]
    int Score,
    [PacketGreedyIndex(3)]
    string Label
) : IPacket;
