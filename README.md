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
