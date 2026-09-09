# Intercepter et deserialiser des paquets texte avec NosSmooth

Notes de travail sur l'injection de dependances dans NosSmooth, verifiees contre
les paquets NuGet publies (`NosSmooth.Core` 5.0.0, `NosSmooth.Packets` 3.6.0,
`NosSmooth.PacketSerializer` 2.2.7, `NosSmooth.PacketSerializer.Abstractions`
1.3.2, `NosSmooth.PacketSerializersGenerator` 1.1.1).

Le code correspondant est dans [`samples/PacketHandlerDemo`](../samples/PacketHandlerDemo).

## 1. La chaine de traitement

NosSmooth ne demande jamais d'ecrire un `IPacketHandler` soi-meme : il en fournit
deux implementations et on branche des *responders* dessus.

```
client (fichier, reseau, ...)
  |
  v
IPacketHandler.HandlePacketAsync(client, source, "quim 42 2 1250 Quiz")
  |
  +-- RawPacketHandler  -> IRawPacketResponder   (chaine brute)
  |
  +-- ManagedPacketHandler
        |
        +-- IPacketSerializer.Deserialize(chaine, source)
        |     ok           -> QuiMStatPacket
        |     en-tete inconnue -> UnresolvedPacket
        |     echec de parsing -> ParsingFailedPacket
        |
        +-- services.CreateScope()            <-- un scope NEUF par paquet
        +-- IPreExecutionEvent
        +-- Task.WhenAll(IPacketResponder<T>, IEveryPacketResponder)
        +-- IPostExecutionEvent
```

Deux consequences directes :

- `AddNostaleCore()` seul ne deserialise rien : il n'installe que
  `RawPacketHandler`. C'est `AddManagedNostaleCore()` qui remplace le handler par
  `ManagedPacketHandler` et appelle `AddPacketSerialization()`.
- Les responders d'un meme paquet tournent **en parallele**. Une exception levee
  dans l'un est capturee et convertie en `Result` en erreur ; renvoyer une erreur
  n'annule pas les autres responders.

## 2. Les quatre enregistrements a ne pas oublier

Pour un paquet **personnalise** defini dans son propre assembly :

| # | Quoi | Ou |
|---|------|-----|
| 1 | `AddManagedNostaleCore()` | `IServiceCollection` |
| 2 | `AddGeneratedSerializers(typeof(MonPaquet).Assembly)` | `IServiceCollection` |
| 3 | `AddPacketResponder<MonResponder>()` | `IServiceCollection` |
| 4 | `packetTypesRepository.AddPacketTypes(typeof(MonPaquet).Assembly)` | au demarrage |

Le point 2 est le piege classique : `AddPacketSerialization()` ne scanne que
`typeof(IPacket).Assembly`, c'est-a-dire `NosSmooth.Packets`. Les convertisseurs
generes pour vos propres paquets ne sont pas enregistres tant que vous ne
declarez pas votre assembly.

Le point 4 aussi : `IPacketTypesRepository` demarre vide. Sans
`AddDefaultPackets()` / `AddPacketTypes(...)`, absolument tout ressort en
`UnresolvedPacket`.

## 3. Durees de vie

| Service | Duree de vie | Remarque |
|---------|--------------|----------|
| `IPacketResponder<T>` | `Scoped` | un scope par paquet : l'instance ne survit pas d'un paquet a l'autre |
| `IPacketHandler` | `Singleton` | |
| `IPacketTypesRepository`, `IPacketSerializer`, `IStringConverter<T>` | `Singleton` | |

Tout etat qui doit persister entre deux paquets vit donc dans un `Singleton`
injecte dans le responder (`ScoreboardService` dans l'exemple), et doit etre
protege puisque les responders s'executent en parallele.

## 4. Definir un paquet texte

```csharp
[PacketHeader("quim", PacketSource.Server)]
[GenerateSerializer(true)]
public record QuiMStatPacket
(
    [PacketIndex(0)] long PlayerId,
    [PacketIndex(1)] QuiMCategory Category,
    [PacketIndex(2)] int Score,
    [PacketGreedyIndex(3)] string Label
) : IPacket;
```

- L'en-tete est consomme par le serialiseur : l'index `0` est le premier champ
  **apres** `quim`.
- `[GenerateSerializer]` ne fait rien tout seul : il faut la reference analyseur
  vers `NosSmooth.PacketSerializersGenerator` dans le `.csproj`. Le generateur
  emet le convertisseur dans le namespace `<VotreNamespace>.Generated`, ce que
  `AddGeneratedSerializers` va chercher (types **publics** uniquement).
- `PacketSource` distingue deux paquets de meme en-tete selon le sens : `say`
  serveur et `say` client sont deux types differents.

Attributs disponibles cote champs :

| Attribut | Usage |
|----------|-------|
| `[PacketIndex(n)]` | champ simple a la position `n` |
| `[PacketGreedyIndex(n)]` | consomme tout le reste de la ligne (messages, libelles) |
| `[PacketListIndex(n)]` | liste de sous-paquets |
| `[PacketConditionalIndex(...)]` | champ present selon la valeur d'un autre champ |
| `[PacketContextList(...)]` | liste dont la taille vient d'un autre champ |

Options utiles sur `[PacketIndex]` : `IsOptional` (le champ doit alors etre
nullable), `InnerSeparator`, `AfterSeparator`, `AllowMultipleSeparators`.

## 5. Le responder asynchrone

```csharp
public class QuiMStatResponder : IPacketResponder<QuiMStatPacket>, IPacketResponder<SayPacket>
{
    private readonly ScoreboardService _scoreboard;

    public QuiMStatResponder(ScoreboardService scoreboard) => _scoreboard = scoreboard;

    public async Task<Result> Respond(PacketEventArgs<QuiMStatPacket> args, CancellationToken ct = default)
    {
        await _scoreboard.AddScoreAsync(args.Packet.PlayerId, args.Packet.Score, ct);
        return Result.FromSuccess();
    }

    public Task<Result> Respond(PacketEventArgs<SayPacket> args, CancellationToken ct = default)
        => Task.FromResult(Result.FromSuccess());
}
```

`PacketEventArgs<T>` expose trois choses : `Packet` (l'objet typé), `PacketString`
(la chaine brute) et `Source`. Une meme classe peut implementer autant de
`IPacketResponder<T>` que voulu : `AddPacketResponder<T>()` parcourt les
interfaces fermees et enregistre le type sous chacune.

Pour tout voir passer sans cibler un type : `IEveryPacketResponder`. Pour
travailler sur la chaine avant deserialisation : `IRawPacketResponder`.

## 6. Mise au point

Enregistrer un responder sur `UnresolvedPacket` et `ParsingFailedPacket` fait
gagner beaucoup de temps :

- `UnresolvedPacket` -> aucun type n'est enregistre pour cet en-tete (etape 4
  oubliee, ou mauvaise `PacketSource`) ;
- `ParsingFailedPacket` -> le type existe mais la chaine ne correspond pas ;
  `SerializerResult` contient l'erreur precise du serialiseur.

`ILogger.LogResultError(result)` (extension de `NosSmooth.Core.Extensions`)
deroule les erreurs Remora imbriquees.
