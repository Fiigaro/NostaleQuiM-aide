namespace PacketHandlerDemo.Packets;

/// <summary>
/// Categorie d'une manche de quiz.
/// </summary>
/// <remarks>
/// Le convertisseur d'enums de NosSmooth serialise/deserialise la valeur
/// numerique sous-jacente (ici <see cref="int"/>), pas le nom. Dans le paquet
/// texte on lira donc "2" et non "Culture".
/// </remarks>
public enum QuiMCategory
{
    /// <summary>Manche d'echauffement.</summary>
    Warmup = 0,

    /// <summary>Manche de rattrapage.</summary>
    Repechage = 1,

    /// <summary>Manche de culture generale.</summary>
    Culture = 2,

    /// <summary>Manche finale.</summary>
    Finale = 3
}
