# Bot de farm NosTale

Bot de farm pour serveur privé NosTale, basé sur la lecture de la mémoire vive
et l'envoi de touches. Interface graphique pour régler les offsets sans toucher
au code.

**Windows uniquement** (pymem et pydirectinput utilisent l'API Win32).

## Fichiers

| Fichier | Rôle |
|---|---|
| `bot_gui.py` | Interface graphique : les cases de réglage + le bouton Lancer |
| `bot_farm_nostale.py` | Moteur du bot (utilisable seul en ligne de commande) |
| `bot_raid.py` | Bot de raid simple : touches + temporisations, sans lecture mémoire |
| `entree_arriere_plan.py` | Envoi des touches/clics directement à la fenêtre du jeu (mode arrière-plan du raid) |
| `build.bat` | Compile `BotFarmNostale.exe` |
| `config_bot.json` | Tes réglages, créé au premier enregistrement |

## Utilisation rapide

```bat
pip install pymem pydirectinput
python bot_gui.py
```

À lancer **en administrateur**, sinon Windows refuse l'accès à la mémoire du jeu.

## Créer l'exécutable

Double-clique sur `build.bat`. L'exécutable apparaît dans `dist\BotFarmNostale.exe`
et demande automatiquement les droits administrateur au lancement.

Ton antivirus peut le mettre en quarantaine : lire la mémoire d'un autre processus
et injecter des frappes clavier, c'est la signature comportementale d'un trojan.
Ajoute une exclusion si nécessaire.

## Régler les offsets

Les offsets se trouvent avec Cheat Engine (recherche de valeur, filtrage après
variation) ou ReClass. Deux formats acceptés dans l'interface :

- offset simple : `0x004B21C0` → lu à `base_module + offset`
- chaîne de pointeurs : `0x004B21C0, 0x1C, 0x8`

Le bouton **Tester la lecture mémoire** affiche les valeurs lues à l'instant
(position, PV/PM, PV de la cible) sans lancer le farm : compare-les à ton écran
de jeu pour valider chaque offset.

Le champ **Type** définit la taille de lecture. Les coordonnées NosTale sont
souvent des `short` (2 octets) plutôt que des `int`.

## Logique du bot

À chaque tour de boucle, dans cet ordre strict :

1. **Potions** — lit PV/PM ; sous les seuils, appuie sur la touche de potion.
   La vérification continue pendant les combats.
2. **Ciblage** — barre Espace pour cibler le monstre le plus proche.
3. **Combat** — si la cible a des PV, le déplacement est suspendu et le bot
   frappe en boucle (touches jouées à tour de rôle), en relisant les PV du
   monstre avant chaque coup.
4. **Pas de ramassage** — l'auto-loot du serveur s'en charge ; à la mort du
   monstre, retour immédiat au ciblage.
5. **Déplacement** — uniquement si aucune cible : avance d'un pas vers le point
   courant du chemin. Point validé à moins de N cases, puis point suivant.

## Calibrage du déplacement

En mode `CLICK` (défaut, standard NosTale), la case visée est projetée en pixels
depuis **Perso à l'écran X/Y** (centre de l'écran : 960, 540 en 1920×1080) et la
**taille d'une case** en pixels. Ce sont les deux réglages à ajuster à ta
résolution — la lecture mémoire ne peut pas les deviner.

Si ton client accepte les flèches directionnelles, le mode `KEYS` évite
complètement ce calibrage.

## Arrêt d'urgence

La touche **ÉCHAP** coupe le bot instantanément, même si la fenêtre du jeu est au
premier plan. Le bouton **Stop** fait la même chose.

## Bot de raid (`bot_raid.py`)

Version minimaliste pour farmer les raids : pas d'offsets, pas de mémoire, juste
une séquence de touches rejouée en boucle (touche du raid → Entrée → avancer →
auto-attaque pendant X secondes → retour au point de départ).

```bat
python bot_raid.py
pip install pydirectinput    (seulement si tu utilises le mode premier_plan)
```

### Mode d'envoi : arrière-plan ou premier plan

- **`arriere_plan`** (défaut) : le bot retrouve la fenêtre du jeu à partir du **nom du
  processus** (`NostaleClientX.exe`, le même réglage que le bot de farm) et lui envoie
  les touches et les clics directement. NosTale peut rester derrière d'autres fenêtres,
  mais **pas réduite dans la barre des tâches**. Les coordonnées `clic X Y` sont alors
  relatives à la fenêtre du jeu (coin haut-gauche = 0, 0).
- **`premier_plan`** : touches et clics globaux (`pydirectinput`), le jeu doit être
  devant. Coordonnées `clic` = écran.

Le mode arrière-plan ne marche que si le jeu lit ses entrées via les messages de fenêtre.
Si rien ne se passe dans le jeu, passe en `premier_plan`.

**Depuis l'interface graphique** (`python bot_gui.py`) : onglet **Raid** pour écrire
la séquence (une étape par ligne, ex. `touche r`, `attendre 1.5`, `maintenir up 2`,
`attaque space 60 0.3`, `clic 960 540`), le nombre de raids et la pause entre deux,
puis bouton **▶ Lancer le raid** en bas. Le bouton **Stop** (ou Échap) coupe le raid.
Réglages enregistrés dans `config_raid.json`.

Pour savoir où cliquer, utilise **◎ Capturer un point** (onglet Raid) : la fenêtre se
réduit, tu cliques à l'endroit voulu dans le jeu, et une ligne `clic X Y` est ajoutée
à la fin de la séquence (Échap annule, 30 s max). Le clic est aussi transmis au jeu.
En `premier_plan`, les coordonnées sont celles de l'écran : garde la fenêtre du jeu au
même endroit et à la même résolution. En `arriere_plan`, elles sont relatives à la
fenêtre du jeu : refais la capture si tu changes de mode ou de taille de fenêtre.

En script, la séquence est la liste `ETAPES` en haut du fichier (ou dans `config_raid.json`,
même format). Actions : `touche`, `attendre`, `maintenir`, `attaque`, `clic`.
Les valeurs par défaut sont des exemples : règle les touches, la durée de
l'`attaque` (= durée du raid) et l'étape de retour selon ton jeu.
`NB_TOURS = 0` boucle à l'infini. **ÉCHAP** coupe tout immédiatement.
