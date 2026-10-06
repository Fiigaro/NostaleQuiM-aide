#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
outil_coordonnees.py
====================

Affiche en direct la position de la souris et la couleur du pixel dessous,
pour relever les coordonnées à saisir dans l'onglet « Espace-temps ».

    python outil_coordonnees.py

Place la souris sur le bouton / la zone voulue, lis la ligne affichée.
Ctrl+C pour quitter. Windows uniquement.
"""

import sys
import time

from bot_farm_nostale import pixel_ecran, position_souris


def main():
    if sys.platform != "win32":
        print("Cet outil nécessite Windows.")
        return 1
    print("Survole la cible avec la souris. Ctrl+C pour quitter.\n")
    try:
        while True:
            x, y = position_souris()
            couleur = pixel_ecran(x, y)
            rvb = "%d, %d, %d" % couleur if couleur else "illisible"
            print("\r  X = %-5d Y = %-5d | couleur R,G,B = %-16s" % (x, y, rvb), end="", flush=True)
            time.sleep(0.15)
    except KeyboardInterrupt:
        print("\nTerminé.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
