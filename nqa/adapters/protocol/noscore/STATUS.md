# Cible NosCore — état d'avancement

Cette couche connecte le bot à un serveur **NosCore que tu héberges toi-même**
(github.com/NosCoreIO/NosCore, MIT). Tout ce qui suit est dérivé directement du
source de l'émulateur, pas deviné. Les chemins de fichiers renvoient aux dépôts
`NosCore`, `NosCore.Networking`, `NosCore.Packets`, `NosCore.Shared`.

## ✅ Fait et prouvé — le chiffrement (`crypto.py`)

Porté à l'identique depuis `NosCore.Networking/Encoding/*.cs`, côté client :

| Fonction | Inverse de | Rôle |
|---|---|---|
| `login_encrypt` | `LoginDecoder` | envoi client→login |
| `LoginRecvStream` | `LoginEncoder` | réception login→client (délimiteur 25) |
| `world_encrypt_session` | `DecryptCustomParameter` | 1er paquet monde = session id (délim 0x0E) |
| `world_encrypt` | `WorldDecoder` (4 cas) | envoi client→monde |
| `WorldRecvStream` | `WorldEncoder` | réception monde→client (délim 0xFF) |
| `frame_delimiter` | `FrameDelimiter` | terminateur de trame monde |

**Vérifié sans serveur.** `tests/test_noscore_crypto.py` contient une
transcription fidèle des *décodeurs serveur* de NosCore comme oracle, et prouve
`serveur_décode(client_encode(x)) == x` — login, session, les **4 cas** de
session monde, et monde→client — plus le streaming (trames coupées/groupées) et
les gros paquets multi-chunks. 40 tests. L'invariant clé (le délimiteur = la
forme chiffrée de 0xFF) est asserté, pas supposé.

## ✅ Fait — la carte de paquets entrants (`packets.noscore.yaml`)

Confirmée depuis `NosCore.Packets/ServerPackets` et validée par
`tests/test_noscore_packets.py` sur de vraies lignes :

| Paquet | Source | Événement |
|---|---|---|
| `st` (Type=1) | `Entities/StPacket.cs` | stats perso (HP/MP absolus) |
| `st` (Type=3) | idem | HP% d'un monstre |
| `mv` (Type=1/3) | `Entities/MovePacket.cs` | déplacement perso / entité |
| `out` | `Entities/OutPacket.cs` | despawn |
| `in` (Type=3) | `Visibility/InPacket.cs` | spawn monstre (champs plats) |

> Les indices du YAML sont en base 1 après l'en-tête (ma convention) ; le
> `[PacketIndex(n)]` de NosCore est en base 0, donc index_YAML = n + 1.

## 🚧 Reste à faire — bien cerné, avec les références

### 1. Commandes sortantes à champ calculé
Un template statique ne suffit pas pour trois commandes (elles lèvent
volontairement une `SpecError` tant qu'elles ne sont pas implémentées dans un
sous-adaptateur NosCore) :

| Commande | Forme | Champ calculé (source) |
|---|---|---|
| `walk` | `walk {x} {y} {checksum} {speed}` | `checksum = (x+y) % 3 % 2` — `WalkPacketHandler.cs:56` |
| `u_i` | `u_i 1 {self_id} 1 {slot} 0` | `slot` = emplacement d'inventaire, **pas le vnum** — `UseItemPacket.cs` |
| `u_s` (buff sur soi) | `u_s {skill_id} 1 {self_id}` | `self_id` = id de notre perso |

Conséquence pour les comportements : la survie/les buffs doivent viser un
**emplacement d'inventaire**, pas un vnum. Il faut donc tenir une table
vnum→slot alimentée par les paquets d'inventaire (`ivn`/`pinit`).

### 2. Paquets à sous-champs (parsing imbriqué)
- `bf` (`Battle/BfPacket.cs`) : `bf {Type} {Id} {charge.buffId.duration} {level}`
  — le champ buff est joint par `.`. Porte la durée exacte des buffs.
- `in` sous-paquets : niveau et HP% du monstre sont dans
  `InNonPlayerSubPacket` (imbriqué). Le spawn les met par défaut à 100 % ;
  `st`/`bf` corrigent en direct.

Le modèle de spec actuel lit un champ = un token. Ajouter un séparateur
secondaire (`.`) et un accès sous-champ couvre les deux cas.

### 3. Séquence de handshake (à implémenter dans un `NosCoreAdapter`)
Reconstituée depuis le source :

```
LOGIN (LoginEncoder/Decoder, sans délimiteur)
  client → NoS0575 {sessionId} {user} {pass} {hash} {version}   (NoS0575Packet.cs)
  serveur → NsTeST … {SessionId} … {liste serveurs monde ip:port}  (NsTeSTPacket.cs, SessionId = idx 11)

MONDE (WorldEncoder/Decoder, avec délimiteur)
  connexion au ip:port du NsTeST
  1er paquet  : world_encrypt_session(str(SessionId))     → fixe la clé de session
  puis (chiffré par session) : {user} puis {pass}         → validation
  serveur → clist (liste des persos)
  client → select {slot}                                   (SelectPacket.cs)
  client → game_start                                      (GameStartPacket.cs)
EN JEU : le serveur pousse in/st/mv/… ; le client envoie walk/u_s/u_i/dir/pulse
```

### 4. Détail de sérialisation
Le `walk` porte un `CheckSum`, plusieurs paquets ont des enums (`VisualType`,
`PocketType`) et des champs optionnels dont l'omission décale les index. Pour
les paquets non listés ici, lire la classe correspondante avant d'ajouter une
entrée — un index faux échoue **silencieusement**.

## Prochaine étape recommandée
Un `NosCoreAdapter(GameAdapter)` qui : ouvre les deux sockets (login puis
monde), enchaîne le handshake ci-dessus, branche `crypto.py` sur le flux, et
surcharge `move_to`/`use_item`/`use_skill(self)` pour les champs calculés. Le
cœur du bot (comportements, arbitre, A*) ne bouge pas — seul l'adaptateur
change.
