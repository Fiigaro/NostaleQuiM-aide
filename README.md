# nqa — noyau d'automatisation NosTale

Cœur d'automatisation modulaire pour les **émulateurs de serveur NosTale que
vous hébergez vous-même** (OpenNos et ses forks).

## Portée

Ce projet est conçu et testé pour une cible que vous contrôlez : votre propre
serveur, en local. Ce choix n'est pas cosmétique, il change l'architecture — le
pilotage se fait **par le protocole**, pas par capture d'écran et injection de
clavier. Il n'y a donc ici aucune vision par ordinateur, aucune lecture mémoire,
aucun code d'injection d'entrées, et rien qui touche à un anti-cheat.

Ce que ça apporte concrètement :

| | Pixels + entrées clavier | Protocole (ici) |
|---|---|---|
| Fiabilité | Cassé par la résolution, l'UI, les effets | Déterministe |
| Arrière-plan | Le problème central de l'approche | Sans objet : aucune fenêtre |
| État du jeu | Deviné par OCR / template matching | Exact |
| Cooldowns | Estimés au chronomètre | Réels, depuis le serveur |
| Multi-instance | 1 fenêtre = 1 bot | Trivial |

## Démarrage rapide

```bash
pip install -e ".[dev]"
python -m nqa.cli --profile profiles/example.yaml --duration 30
```

Ça tourne contre le **simulateur intégré** : un monde factice en mémoire, sans
serveur ni réseau. Vous verrez le personnage patrouiller, engager les monstres,
boire des potions et maintenir ses buffs.

```
[   6.0s] navigation pos=(17,20) hp= 98.1% mp= 94.0% mobs= 6 buffs=[skill:250:114s,...]
stopped after 100 ticks
  behaviours: {'buffs': 2, 'navigation': 39, 'combat': 12}
```

## Architecture

```
adaptateur ──événements──▶ WorldState ──▶ arbitre ──action──▶ exécuteur ──▶ adaptateur
 (perception)                            (comportements)                    (commandes)
```

```
nqa/
├── core/           modèles, bus d'événements, état, cooldowns, A*
├── adapters/
│   ├── simulated.py    monde factice — le banc de test
│   └── protocol/       client TCP + carte de paquets déclarative
├── behaviors/      survie, buffs, combat, navigation
├── brain/          arbitre par priorité, exécuteur, boucle de tick
├── config/         chargement et validation des profils
└── app.py          racine de composition
```

**Les comportements ne font aucune E/S.** Ils reçoivent un `Context` et
renvoient une `Action` ou `None`. C'est ce qui permet de tous les tester avec un
`WorldState` construit à la main et une horloge manuelle, sans réseau ni boucle
d'événements.

### Arbitrage par priorité, pas machine à états

L'arbitre interroge les comportements par priorité décroissante et prend la
première réponse non nulle :

| Comportement | Priorité |
|---|---|
| survie | 100 |
| buffs | 50 |
| combat | 30 |
| navigation | 10 |

Une FSM exigerait une transition explicite pour chaque interruption —
combat→potion→combat, marche→potion→marche, buff→potion→… — et **la transition
que vous oubliez est celle qui vous tue**. Ici la survie prime, point : la
préemption est une propriété de l'ordre, pas quelque chose à énumérer.

### Décisions non évidentes

- **La portée d'approche est la plus *petite* portée de sort, pas la plus
  grande.** S'arrêter à portée max semble plus malin mais provoque un blocage :
  arrêté à 5 cases alors que seule l'attaque de portée 1 est disponible, le
  combat ne se déclenche jamais et la navigation se croit arrivée.
- **Les buffs ont leur propre clé de cooldown** (`buff:skill:250` et non
  `skill:250`), pour qu'un sort utilisé à la fois en attaque et en buff ne
  partage pas un seul timer.
- **Le déplacement ne consomme pas le cooldown global**, sinon marcher
  empêcherait de lancer un sort.
- **Les diagonales ne coupent pas les coins** : un serveur en cases refuse ce
  mouvement, et un pas refusé désynchronise le chemin.
- **Une déconnexion vide l'état volatil** (entités, buffs, cooldowns). Ce qu'on
  croyait savoir est périmé.
- **Les buffs expirent aussi localement**, sans attendre le paquet du serveur,
  qui peut se perdre.

## Brancher un vrai serveur

`nqa/adapters/protocol/` fournit le transport TCP, le découpage en trames, le
dispatch et les commandes. **Trois choses restent à fournir**, toutes issues du
code source de votre émulateur :

1. **Le chiffrement** (`crypto.py`). Le chiffrement NosTale est propre à chaque
   canal (login / monde) et diffère selon les forks. `NullEncryption` ne
   fonctionne que contre un serveur en clair — c'est le bon point de départ en
   mode dev.
2. **La carte de paquets** (copiez `packets.example.yaml`). En-têtes et indices
   de champs, en lisant les handlers de paquets de votre serveur.
3. **La séquence de login**, spécifique au serveur, non modélisée ici.

La carte est déclarative — du remplissage de données, pas du code :

```yaml
incoming:
  stats:
    header: "st"
    fields: {hp: 1, hp_max: 2, mp: 3, mp_max: 4}
  entity_moved:
    header: "mv"
    when: {1: "3"}        # discrimine quand un en-tête a plusieurs sens
    fields: {id: 2, x: 3, y: 4}
outgoing:
  move: "walk {x} {y} {speed}"
```

> **Un indice erroné échoue silencieusement.** Vous obtenez une valeur
> plausible dans le mauvais champ, pas une erreur. C'est pourquoi le gabarit
> livré ne contient que des `REPLACE_ME` plutôt que des suppositions
> crédibles — et pourquoi un test documente explicitement ce mode de panne
> (`test_a_wrong_field_index_yields_a_wrong_value_not_an_error`).

Ensuite, il suffit d'échanger la fabrique d'adaptateur :

```python
bot = build_bot(profile, lambda bus, clock: ProtocolAdapter(
    bus, clock, PacketSpec.load("packets.yaml"), host="127.0.0.1", port=4000,
))
```

Rien au-dessus de cette ligne ne change.

## Profils

Tout ce qui est réglable est en YAML (voir `profiles/example.yaml`) : sorts,
cooldowns, coûts en mana, portées, seuils de potions, vnums d'objets, buffs,
waypoints. La validation est stricte et nomme le chemin exact de la clé fautive
(`combat.skills[1].id`) — un profil mal tapé qui ne se manifeste que par une
inaction silencieuse trois minutes plus tard est pénible à déboguer.

Un piège : gardez un sort de repli **gratuit et de portée 1 en priorité 0**,
sinon la rotation se bloque dès que les bons sorts sont en cooldown.

## Tests

```bash
python -m pytest          # 124 tests, < 1 s
```

Les tests d'intégration font tourner la pile complète contre le simulateur avec
une horloge manuelle avancée depuis le hook de sommeil du runner : une
« minute » de jeu s'exécute en millisecondes et se rejoue à l'identique. Ils
vérifient des propriétés réelles — des monstres meurent, les buffs ne tombent
jamais, le personnage survit sous pression tant qu'il lui reste des potions, et
décroche quand il n'en a plus.

Les tests du protocole tournent sur une vraie socket loopback avec une carte de
paquets **fictive** : c'est la machinerie qui est validée, pas des données de
jeu devinées.

## Non inclus

Vision par ordinateur, injection d'entrées Win32, lecture mémoire, contournement
d'anti-cheat, et la séquence de login d'un serveur donné.
