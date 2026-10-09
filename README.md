# NostaleQuiM-aide — NosSmoothCustomClient

Assembly d'automatisation .NET 8 bâtie sur NosSmooth : interception de paquets typée, moteur d'état
partagé, rotation de sorts par priorité et boucle de décision à 300 ms — avec deux front-ends
(console et fenêtre Avalonia) qui pilotent **exactement le même moteur**.

```bash
dotnet build

dotnet run --project NosSmoothCustomClient              # console, simulateur
dotnet run --project NosSmoothCustomClient -- --verbose # + détail par tick
dotnet run --project NosSmoothCustomClient.Gui          # fenêtre Avalonia (fenêtre de démarrage)
dotnet run --project NosSmoothCustomClient.Gui -- --selftest  # test headless du moteur + de l'UI

# lire le trafic d'un vrai client, en lecture seule (Npcap + admin requis)
dotnet run --project NosSmoothCustomClient -- --pcap
dotnet run --project NosSmoothCustomClient -- --pcap --pid 1234

# ne tracer que ce qu'on cherche, ou au contraire tout voir
dotnet run --project NosSmoothCustomClient -- --pcap --only "u_i guri sp"
dotnet run --project NosSmoothCustomClient -- --pcap --hide ""

dotnet run --project NosSmoothCustomClient -- --help
```

## Sans ligne de commande : l'exe

Double-clique sur `NosSmoothCustomClient.exe` : Windows demande les droits administrateur, puis une
fenêtre de démarrage remplace toutes les options ci-dessus.

1. **Ton NosTale** : la liste montre les clients ouverts (numéro, heure d'ouverture, titre, position
   de la fenêtre). Choisis le tien, ou laisse **Automatique** s'il n'y en a qu'un.
   **Faire clignoter sa fenêtre** montre lequel est lequel quand plusieurs sont ouverts.
2. **Mode** : *Jouer* (`--pcap --play`, démarre en pause : clique « Reprendre » quand le perso est
   prêt), *Observer* (`--pcap`, rien n'est envoyé au jeu) ou *Simulateur* (`--simulate`, sans jeu).
3. **Démarrer**. Si le bot ne peut pas se lier, la raison s'affiche dans la même fenêtre : choisis un
   autre client ou un autre mode et recommence.

Prérequis : [Npcap](https://npcap.com) installé (case « WinPcap API-compatible Mode » cochée), et
NosTale ouvert avec le personnage connecté. Garde `appsettings.json` à côté de l'exe.

**Fabriquer l'exe** : double-clique sur `publier.bat` (il faut le SDK .NET 8). Il apparaît dans
`dist\NosSmoothCustomClient.exe` : un seul fichier, sans .NET à installer sur la machine qui le lance,
et sans fenêtre de console derrière. `dotnet run` sans option ouvre la même fenêtre de démarrage.

## Configuration

`appsettings.json`, a la racine du depot, pilote seuils, waypoints, rotation et slots de
consommables. Modifie-le et relance : **aucune recompilation**. Les commentaires JSON sont acceptes.

Au demarrage, la ligne `Configuration :` liste ce qui a reellement ete applique — une section mal
nommee laisserait sinon tous les defauts en place sans rien dire.

## Le bot au clavier

Sur un serveur dont le trafic client -> serveur ne peut pas etre forge, le bot **joue comme un
humain** : touches de la barre rapide et clics sur la minimap. La lecture continue de venir de la
capture, ce qui le distingue d'une macro aveugle - il ne presse la potion que si les PV sont
reellement bas, et ne change de spot que s'il n'y a vraiment plus rien a taper.

```bash
# 1. Verifier que les touches atteignent le client en arriere-plan
dotnet run --project NosSmoothCustomClient -- --test-input --key space

# 2. Enregistrer la route : sur place, viser la minimap, F9. F10 pour sauver.
dotnet run --project NosSmoothCustomClient -- --record-waypoints

# 3. Observer les decisions sans rien envoyer
dotnet run --project NosSmoothCustomClient.Gui -- --pcap

# 4. Laisser le bot jouer (demarre en pause, P pour lancer)
dotnet run --project NosSmoothCustomClient.Gui -- --pcap --play
```

Les reglages vivent dans `appsettings.json`, et la fenetre permet de cocher/decocher chaque sort et
chaque buff et d'ajuster leurs temps a chaud. Le bouton d'enregistrement ecrit
`appsettings.local.json`, relu au lancement suivant.

## Lire les paquets

La fenêtre ne montre plus la trace des paquets ni l'envoi manuel : elle garde ce qui sert à jouer.
La trace reste lisible depuis le front-end console, avec les mêmes filtres :

| Option | Effet |
|---|---|
| `--only "u_i guri"` | seulement ces en-têtes ; rempli, il est seul à décider |
| `--hide "..."` | le bruit de fond, préréglé sur `mv in out cond eff st pairy rsfi fs char_sc` ; `--hide ""` montre le flux brut |

Un en-tête nommé dans `--only` l'emporte sur `--hide`. L'en-tête est lu correctement dans les trois
formes qui existent — `u_i 1 …`, `1043 walk …` (le numéro de séquence que le client met devant ses
trames) et `#guri^710^1^1`. Les listes se gardent aussi dans `appsettings.json` sous `PacketTrace`.

**En `--pcap`, aucun paquet ne peut être envoyé.** Ce n'est pas un réglage : `NosSmooth.Pcap` laisse
`SendPacketAsync` et `ReceivePacketAsync` non implémentées, parce qu'écrire dans une connexion TCP
qu'on ne fait qu'écouter demanderait d'en usurper les numéros de séquence, ce qui casserait le flux
du vrai client. Le bot agit donc par touches et clics, comme un joueur.

## Transports

| Drapeau | Ce qu'il fait | Injection | Patterns mémoire |
|---|---|---|---|
| *(défaut)* | Trames synthétisées en interne, aucun jeu requis | non | non |
| `--pcap` | Lit le trafic TCP du vrai client via libpcap | non | **non** |
| `--attach` | Se lie au processus en mémoire (Windows x86) | oui | oui |

`--pcap` est la voie de calibration : il contourne entièrement le scan mémoire, qui est le point de
blocage sur un client modifié. Il **démarre toujours en lecture seule**, et pour deux raisons
différentes qu'il vaut mieux ne pas confondre. La boucle démarre en pause, et `P` la relance : c'est
ce qui décide si les touches partent. L'envoi de paquets, lui, n'est pas une question de pause —
`NosSmooth.Pcap` ne l'implémente pas du tout, et aucun réglage ne le rend possible.

Prérequis : Npcap sur Windows, et un processus élevé.

Publication en `.exe` autonome : `publier.bat`, ou à la main :

```bash
dotnet publish NosSmoothCustomClient.Gui -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o dist
```

## Structure

| Projet | Rôle |
|---|---|
| `NosSmoothCustomClient.Core` | Tout le moteur : paquets, responders, état, rotation, orchestration |
| `NosSmoothCustomClient` | Front-end console |
| `NosSmoothCustomClient.Gui` | Tableau de bord Avalonia |

Le bot Python à côté (lecture mémoire, `bot_gui.py`, espace-temps) a son propre guide :
`BOT_PYTHON.md` et `TUTO_ESPACE_TEMPS.md`.

Le câblage DI vit dans `Core/BotServiceRegistration.cs` et est appelé par les deux front-ends : un
comportement vérifié dans l'un est celui que l'on obtient dans l'autre.

## Corrections apportées à la spécification d'origine

Quatre éléments n'existent pas tels qu'écrits. Chacun a été résolu contre les paquets publiés et les
assemblies décompilées, pas deviné.

| Spécifié | Réalité | Utilisé |
|---|---|---|
| `NosSmooth.Local` 5.0.0 | Cet identifiant NuGet n'existe pas | **`NosSmooth.LocalClient` 2.2.0** — dépend de `NosSmooth.Core` 5.0.0 exactement |
| `NosSmooth.PacketSerializersGenerator` 2.2.7 | Plafonne à 1.1.1 ; **2.2.7 est `NosSmooth.PacketSerializer`** | Les deux, à leurs versions réelles |
| `AddManagedNosSmoothCore()` | N'existe pas | **`AddManagedNostaleCore()`** |
| `AddNosSmoothPackets()` | N'existe pas | **`AddPacketSerialization()`** |
| `services.AddPacketTypes(asm)` | Étend `IPacketTypesRepository`, pas `IServiceCollection` | Résolu après construction du conteneur |

Deux corrections de protocole :

- **Le déplacement sortant est `walk`, pas `mv`.** `mv` est un paquet *serveur → client* (une entité
  s'est déplacée). Le client envoie `walk <x> <y> <checksum> <speed>`. `mv` est consommé en entrée
  pour suivre la position.
- **L'attaque ciblée est `u_s`, pas `u_as`.** `u_as` est la compétence de *zone* et ne transporte
  aucun identifiant de cible.

### Le checksum de `walk`

NosSmooth modélise `WalkPacket.CheckSum` mais ne le calcule jamais — son client local se déplace via
la routine interne du jeu. La valeur dépend de la version du client. `WalkChecksumCalculator`
concentre la formule en un seul endroit et **doit être validée contre le serveur cible**. En mode
attaché, la question ne se pose pas : `CommandWalkStrategy` émet une `WalkCommand`.

## Architecture

```
trame entrante ─► ManagedPacketHandler ─► IPacketResponder<T>  ─┐
                  (désérialise)           (portée par paquet)   │ écrit
                                                                ▼
                                       ProtocolStateManager + SkillRotation  (singletons)
                                                                │ lit
        trame sortante ◄─ PacketDispatcher ◄─ OrchestrationBackgroundService (300 ms)
                          (sérialise)          P1 survie ▸ P2 combat ▸ P3 navigation
```

Les responders sont déclenchés par événement (la trame qui vient d'arriver) ; la boucle est
déclenchée par état (elle agit tant qu'une condition tient). Les deux passent par les mêmes verrous
de cooldown, donc une potion ou une frame d'attaque n'est jamais émise deux fois pour un même
événement.

`ProtocolStateManager` est le seul état inter-paquets : les responders sont résolus par paquet, donc
une instance ne voit jamais le paquet précédent. `Interlocked` pour les scalaires indépendants, un
verrou court pour les paires devant rester cohérentes (X/Y, id/PV de cible), un
`ConcurrentDictionary` pour la table d'entités, un `SemaphoreSlim` qui sérialise les cycles complets.

## Rotation de sorts

Priorité fixe : la boucle lance la première entrée hors cooldown et payable en PM, sinon l'attaque de
base. Les cooldowns sont pilotés par le paquet **`sr`** du serveur, qui est autoritaire et insensible
à la dérive ; un timer local tourne en parallèle comme filet de sécurité, de sorte que si `sr`
n'arrive jamais ou numérote ses sorts autrement que les cast ids, la rotation dégrade proprement en
ordonnanceur par cooldown au lieu de se désynchroniser.

Configurable dans `BotOptions.Skills` — les cast ids, coûts en PM et cooldowns par défaut sont des
**placeholders** à remplacer par ceux de la barre du personnage. Le paquet `ski` est journalisé au
démarrage pour permettre cette vérification.

## Écart délibéré

L'étape 5 du brief demande d'arrêter tout mouvement dès qu'une cible est verrouillée. Pris au pied de
la lettre, cela bloque la boucle sur tout monstre apparaissant hors de portée : elle ne s'approche
jamais et ne touche jamais. La boucle s'approche donc au-delà de `AttackRange`, puis se fige une fois
à portée. `ApproachTargetOutOfRange = false` rétablit la lecture littérale.

Aucun ramassage au sol, conformément au brief — auto-loot serveur supposé actif.

## Mode simulé

`--attach` exige Windows x86 et un processus NosTale vivant. Pour que le pipeline reste testable
partout, le transport par défaut est `SimulatedNostaleClient`, qui construit chaque trame entrante en
**sérialisant un vrai record de paquet** via le même `IPacketSerializer` que le chemin de production,
puis la réinjecte sous forme de chaîne. Une exécution est donc un véritable test d'aller-retour des
convertisseurs générés, du dépôt de types, de la distribution vers les responders et de la boucle —
ce n'est pas un mock.

Il scénarise apparition → combat → mort → patrouille → réapparition, consomme des PM à chaque sort,
renvoie les `sr` après cooldown et draine les PV pour que la priorité survie se déclenche d'elle-même.

`--selftest` (GUI) démarre le moteur, le laisse tourner, puis construit la fenêtre sous la plateforme
headless d'Avalonia et vérifie que l'UI reflète l'état réellement produit — utile en CI, et
indispensable pour valider une fenêtre écrite sur une machine sans écran.

Pour inspecter le code émis par le générateur de source, ajouter au csproj de `Core` :

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
<CompilerGeneratedFilesOutputPath>generated</CompilerGeneratedFilesOutputPath>
```

## Limites connues

- Pas de configuration externe : `BotOptions` n'est pas lié à un `appsettings.json`.
- Pas de pathfinding : déplacement en ligne droite, aucun contournement d'obstacle.
- Pas de gestion de la mort du personnage, du changement de carte, ni des familiers.
- Pas de lecture d'inventaire : les slots de consommables sont fixes.
- Les paquets `quim` / `quimtg` sont construits d'après la spécification, pas d'après des captures.
- Le mode `--attach` n'a jamais été exécuté contre un vrai client ; il dépend de patterns de sigscan
  spécifiques à la version du client, à re-dériver via `ConfigureHooks(...)`.

## Note

Automatiser un client de jeu commercial enfreint les CGU de NosTale et expose à un bannissement de
compte.
