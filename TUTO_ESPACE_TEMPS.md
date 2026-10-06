# Tuto : mettre en place le bot Espace-temps (TS)

Ce que fait le bot, du début à la fin :

1. **Entrée** : le perso principal clique sur Start, les 2 alliés font `C C` + `Entrée`,
   retour sur le principal, il marche jusqu'à un point précis et appuie sur `Entrée`.
2. **Clear de la map** : farm habituel (ciblage, combat, potions, déplacement sur le chemin).
3. **Récompenses** : dès que l'écran de récompense apparaît, le principal clique sa
   récompense, puis chaque allié clique la sienne.
4. Fin du run : le bot s'arrête, ou rejoue l'entrée si tu as coché « Relancer ».

---

## 1. Installation

1. Windows uniquement. Installe Python (cocher *Add Python to PATH*).
2. Dans un terminal :
   ```bat
   pip install pymem pydirectinput
   ```
3. Lance toujours le bot **en administrateur** (clic droit sur le terminal → *Exécuter en tant qu'administrateur*).
4. Lance l'interface : `python bot_gui.py`

## 2. Préparer le jeu

- Ouvre les **3 clients NosTale** (principal + 2 alliés).
- Mets-les en **mode fenêtré ou fenêtré sans bordure** (pas plein écran exclusif) :
  sinon la lecture de couleur de pixel renvoie du noir.
- Place les 3 fenêtres **à la même résolution et au même endroit** : les clics sont
  des coordonnées d'écran, elles doivent donc viser la même zone dans chaque fenêtre
  (sauf si tu règles des coordonnées différentes par perso).
- Donne à chaque fenêtre un **titre distinct** (le nom du personnage dans la barre
  de titre fait très bien l'affaire). Le bot retrouve les fenêtres par ce titre.

## 3. Régler les offsets mémoire (perso principal)

Onglet **Processus & Offsets** : renseigne le nom du processus, les offsets de
position X/Y, PV/PM et PV de la cible (voir le README, section « Régler les
offsets »). Clique **Tester la lecture mémoire** : les valeurs doivent coller à ton
écran de jeu. Sans ça, le déplacement et les potions ne marchent pas.

## 4. Relever les coordonnées et la couleur

Lance l'outil (en administrateur) :

```bat
python outil_coordonnees.py
```

Il affiche en direct `X = ... Y = ... | couleur R,G,B = ...` sous la souris.
Survole chaque cible, **note la ligne** :

| À relever | Où survoler | Champ dans l'interface |
|---|---|---|
| Bouton bleu **Start** | le centre du bouton, sur le perso principal | Bouton bleu Start - X / Y |
| **Récompense principal** | la récompense à choisir, dans la fenêtre du principal | Récompense principal - X / Y |
| **Récompense allié 1** | la récompense à choisir, dans la fenêtre de l'allié 1 | Récompense allié 1 - X / Y |
| **Récompense allié 2** | idem, allié 2 | Récompense allié 2 - X / Y |
| **Pixel de détection** | un point **fixe** de la fenêtre de récompense (bordure, titre, fond uni), pas sur du texte ni une animation | Pixel - X / Y, et Couleur R, G, B |
| **Point d'arrivée** | pas la souris : place le perso sur le point voulu et lis X / Y avec « Tester la lecture mémoire » | onglet Espace-temps, Point d'arrivée |

Pour le pixel de détection : ouvre l'écran de récompense (fais un run à la main),
survole le pixel choisi, note ses coordonnées **et** sa couleur. Vérifie que ce
pixel a une **autre couleur** quand l'écran de récompense n'est pas affiché.
Tolérance conseillée : 12.

## 5. Remplir l'onglet « Espace-temps »

1. Coche **Activer la séquence d'entrée**.
2. **Fenêtres** : titre (ou partie du titre) de l'allié 1 et de l'allié 2.
   « Perso principal » peut rester vide : le bot prend la fenêtre qui est au
   premier plan quand le compte à rebours se termine.
3. **Touches** : `c` pressée 2 fois, intervalle `0.2` (doit rester < 0,5 s),
   validation `enter`. Les délais par défaut conviennent pour commencer.
4. **Clics souris** : colle les coordonnées relevées à l'étape 4.
5. **Détection de l'écran de récompense** : pixel, couleur, tolérance.
6. **Point d'arrivée** : X, Y du point où le principal appuie sur `Entrée`.
7. Coche **Relancer l'entrée en TS après les récompenses** seulement si, après les
   récompenses, tu te retrouves dans un état où le bouton Start est de nouveau
   cliquable. Sinon, laisse décoché : le bot s'arrête proprement.
8. **Sauvegarder**.

Dans l'onglet **Chemin & Déplacement**, mets le **chemin de farm** qui parcourt la
map à clear (points X, Y), et règle le calibrage écran (voir le README).

## 6. Tester par étapes

Ne lance pas tout d'un coup la première fois.

1. **Entrée** : place les persos devant l'entrée, clique **Lancer**, mets la
   fenêtre du principal au premier plan pendant le compte à rebours (5 s). Regarde
   le journal : il décrit chaque étape. Corrige les coordonnées si un clic tombe à côté.
2. **Récompenses** : une fois la map clear, vérifie que le journal affiche
   `Écran de récompense détecté`. Si rien ne se passe, le pixel/la couleur de
   détection est mauvais : revérifie avec `outil_coordonnees.py` pendant que l'écran
   de récompense est affiché.
3. **Run complet**, avec « Relancer » coché si tu veux l'enchaînement.

**Arrêt d'urgence : ÉCHAP** (ou le bouton Stop) coupe tout à tout moment.

## 7. Si ça ne marche pas

| Symptôme | Cause probable |
|---|---|
| `Fenêtre alliée '...' introuvable` | le titre saisi n'apparaît pas dans la barre de titre de la fenêtre, ou la fenêtre est sur un autre bureau virtuel |
| `Impossible d'amener la fenêtre ... au premier plan` | lance en administrateur ; ne touche pas au clavier/souris pendant la séquence |
| Les clics tombent à côté | mise à l'échelle Windows ou résolution différente : refais les relevés avec `outil_coordonnees.py` sur la configuration définitive |
| Le double `C` n'est pas pris | baisse l'intervalle (ex. `0.12`) ; il est mesuré et affiché dans le journal |
| `Point non atteint` | offsets de position faux, ou calibrage écran (case en pixels) à régler ; augmente le timeout |
| `Écran de récompense détecté` n'apparaît jamais | mode plein écran exclusif (pixel noir), ou pixel choisi sur un élément animé |
| Le bot s'arrête juste après le clear | normal si « Relancer » est décoché : il s'arrête après les récompenses |

> Ce bot lit la mémoire du jeu et simule des touches : vérifie que c'est autorisé
> par le règlement de ton serveur privé.
