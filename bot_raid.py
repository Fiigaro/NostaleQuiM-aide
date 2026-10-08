#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bot_raid.py
===========

Bot de farm de raid, volontairement simple : aucune lecture mémoire, juste des
touches et des temporisations, jouées en boucle.

Un tour = la liste ETAPES ci-dessous, du début à la fin, puis on recommence.

Actions disponibles pour une étape :
    {"action": "touche",    "touche": "r"}                    appui simple
    {"action": "attendre",  "duree": 2.0}                     pause (secondes)
    {"action": "maintenir", "touche": "up", "duree": 2.0}     touche enfoncée
    {"action": "attaque",   "touche": "space", "duree": 60,   spam d'une touche
                            "delai": 0.3}                     pendant `duree` s
    {"action": "clic",      "x": 960, "y": 540}               clic souris

Lancement (Windows) :
    pip install pydirectinput
    python bot_raid.py            (en administrateur si le jeu l'est aussi)

Touche ÉCHAP = arrêt immédiat, à tout moment.
Les réglages sont lus dans 'config_raid.json' s'il existe (même format que
CONFIG_DEFAUT) ; sinon ce sont les valeurs ci-dessous.
"""

import copy
import json
import os
import sys
import threading
import time

try:
    import pydirectinput
except Exception:
    pydirectinput = None

from bot_farm_nostale import ArretDemande, demarrer_surveillance_echap, dossier_base


# ===========================================================================
#                       CONFIGURATION PAR DÉFAUT
#   À adapter : touches, durées, et surtout la durée du raid.
# ===========================================================================
CONFIG_DEFAUT = {
    "DELAI_DEMARRAGE": 5,     # Compte à rebours avant le 1er tour (mets NosTale au premier plan)
    "NB_TOURS": 0,            # 0 = boucle infinie
    "PAUSE_ENTRE_TOURS": 3.0, # Secondes d'attente entre la fin d'un tour et le suivant

    "ETAPES": [
        # 1. Touche qui ouvre / lance le raid
        {"action": "touche", "touche": "r"},
        {"action": "attendre", "duree": 1.0},
        # 2. Entrée dans le raid
        {"action": "touche", "touche": "enter"},
        {"action": "attendre", "duree": 4.0},          # chargement de la map
        # 3. On avance
        {"action": "maintenir", "touche": "up", "duree": 2.0},
        # 4. Auto-attaque pendant toute la durée du raid
        {"action": "attaque", "touche": "space", "duree": 60.0, "delai": 0.3},
        # 5. Fin du raid : on laisse l'écran de fin se fermer
        {"action": "attendre", "duree": 5.0},
        # 6. Retour au point de départ (à adapter : clic, touche, ou trajet inverse)
        {"action": "maintenir", "touche": "down", "duree": 2.0},
    ],
}


# ---------------------------------------------------------------------------
# CONFIGURATION
# ---------------------------------------------------------------------------
FICHIER_CONFIG = os.path.join(dossier_base(), "config_raid.json")


def charger_config(chemin=None):
    chemin = chemin or FICHIER_CONFIG
    config = copy.deepcopy(CONFIG_DEFAUT)
    if os.path.exists(chemin):
        with open(chemin, "r", encoding="utf-8") as fichier:
            config.update(json.load(fichier))
    return config


def sauver_config(config, chemin=None):
    chemin = chemin or FICHIER_CONFIG
    with open(chemin, "w", encoding="utf-8") as fichier:
        json.dump(config, fichier, indent=4, ensure_ascii=False)
    return chemin


# Format texte d'une étape, une par ligne (utilisé par l'interface graphique) :
#   touche r | attendre 1.5 | maintenir up 2 | attaque space 60 [0.3] | clic 960 540
_FORMATS = {
    "touche": (("touche", str),),
    "attendre": (("duree", float),),
    "maintenir": (("touche", str), ("duree", float)),
    "attaque": (("touche", str), ("duree", float), ("delai", float)),
    "clic": (("x", int), ("y", int)),
}


def texte_vers_etapes(texte):
    """Convertit le texte de l'éditeur en liste d'étapes. Lève ValueError."""
    etapes = []
    for numero, ligne in enumerate((texte or "").splitlines(), start=1):
        ligne = ligne.strip()
        if not ligne or ligne.startswith("#"):
            continue
        mots = ligne.split()
        action, args = mots[0].lower(), mots[1:]
        if action not in _FORMATS:
            raise ValueError("Ligne %d : action inconnue '%s' (touche, attendre, "
                             "maintenir, attaque, clic)." % (numero, mots[0]))
        champs = _FORMATS[action]
        obligatoires = len(champs) - (1 if action == "attaque" else 0)
        if not obligatoires <= len(args) <= len(champs):
            raise ValueError("Ligne %d ('%s') : nombre d'arguments incorrect." % (numero, ligne))
        etape = {"action": action}
        for (nom, type_), valeur in zip(champs, args):
            try:
                etape[nom] = type_(valeur.replace(",", ".") if type_ is float else valeur)
            except ValueError:
                raise ValueError("Ligne %d : '%s' n'est pas une valeur valide pour %s."
                                 % (numero, valeur, nom))
        etapes.append(etape)
    return etapes


def etapes_vers_texte(etapes):
    lignes = []
    for etape in etapes:
        valeurs = [etape[nom] for nom, _ in _FORMATS[etape["action"]] if nom in etape]
        lignes.append(" ".join([etape["action"]] + [
            "%g" % v if isinstance(v, float) else str(v) for v in valeurs]))
    return "\n".join(lignes)


def valider_config(config):
    """Retourne la liste des problèmes bloquants (vide si tout va bien)."""
    problemes = []
    if not config["ETAPES"]:
        problemes.append("ETAPES est vide.")
    for i, etape in enumerate(config["ETAPES"], 1):
        action = etape.get("action")
        if action in ("touche", "maintenir", "attaque") and not etape.get("touche"):
            problemes.append("Étape %d (%s) : 'touche' manquante." % (i, action))
        elif action in ("attendre", "maintenir", "attaque") and not etape.get("duree", 0) > 0:
            problemes.append("Étape %d (%s) : 'duree' doit être > 0." % (i, action))
        elif action == "clic" and ("x" not in etape or "y" not in etape):
            problemes.append("Étape %d (clic) : 'x' et 'y' manquants." % i)
        elif action not in ("touche", "attendre", "maintenir", "attaque", "clic"):
            problemes.append("Étape %d : action inconnue %r." % (i, action))
    if pydirectinput is None:
        problemes.append("Bibliothèque 'pydirectinput' introuvable : pip install pydirectinput")
    return problemes


# ---------------------------------------------------------------------------
# MOTEUR
# ---------------------------------------------------------------------------
class BotRaid:
    def __init__(self, config, journal=None, arret=None):
        self.cfg = config
        self.journal = journal or (lambda message: print(message, flush=True))
        self.arret = arret or threading.Event()

    def log(self, message):
        self.journal("[%s] %s" % (time.strftime("%H:%M:%S"), message))

    def pause(self, duree):
        """Sleep interruptible : réagit à l'arrêt immédiatement."""
        if self.arret.wait(duree):
            raise ArretDemande()

    def verifier_arret(self):
        if self.arret.is_set():
            raise ArretDemande()

    # --- Actions ------------------------------------------------------------
    def _touche(self, etape):
        self.verifier_arret()
        self.log("Touche '%s'" % etape["touche"])
        pydirectinput.press(etape["touche"])

    def _attendre(self, etape):
        self.log("Attente %.1f s" % etape["duree"])
        self.pause(etape["duree"])

    def _maintenir(self, etape):
        self.log("Maintien de '%s' pendant %.1f s" % (etape["touche"], etape["duree"]))
        pydirectinput.keyDown(etape["touche"])
        try:
            self.pause(etape["duree"])
        finally:
            pydirectinput.keyUp(etape["touche"])

    def _attaque(self, etape):
        self.log("Auto-attaque ('%s') pendant %.1f s" % (etape["touche"], etape["duree"]))
        fin = time.time() + etape["duree"]
        while time.time() < fin:
            self.verifier_arret()
            pydirectinput.press(etape["touche"])
            self.pause(min(etape.get("delai", 0.3), max(fin - time.time(), 0)))

    def _clic(self, etape):
        self.verifier_arret()
        self.log("Clic en (%d, %d)" % (etape["x"], etape["y"]))
        pydirectinput.moveTo(int(etape["x"]), int(etape["y"]))
        pydirectinput.click()

    def jouer_tour(self):
        actions = {
            "touche": self._touche,
            "attendre": self._attendre,
            "maintenir": self._maintenir,
            "attaque": self._attaque,
            "clic": self._clic,
        }
        for etape in self.cfg["ETAPES"]:
            actions[etape["action"]](etape)

    # --- Boucle principale -------------------------------------------------
    def boucle(self):
        tours_max = int(self.cfg["NB_TOURS"])
        tour = 0
        while tours_max == 0 or tour < tours_max:
            tour += 1
            debut = time.time()
            self.log("=== Raid n°%d%s ===" % (tour, "/%d" % tours_max if tours_max else ""))
            self.jouer_tour()
            self.log("Raid n°%d terminé en %.0f s." % (tour, time.time() - debut))
            if tours_max == 0 or tour < tours_max:
                self.pause(self.cfg["PAUSE_ENTRE_TOURS"])
        self.log("Nombre de tours demandé atteint - arrêt.")

    def executer(self):
        problemes = valider_config(self.cfg)
        if problemes:
            self.log("[Erreur] Configuration incomplète :")
            for probleme in problemes:
                self.log("         - %s" % probleme)
            return 1

        demarrer_surveillance_echap(self.arret)
        try:
            for restant in range(int(self.cfg["DELAI_DEMARRAGE"]), 0, -1):
                self.log("Démarrage dans %d s - mets la fenêtre NosTale au premier plan..." % restant)
                self.pause(1.0)
            self.boucle()
        except ArretDemande:
            self.log("Arrêt demandé (ÉCHAP) - bot coupé.")
        finally:
            self.arret.set()
        return 0


def main():
    print("=" * 60)
    print(" BOT RAID NOSTALE - touches + temporisations")
    print(" ÉCHAP = arrêt immédiat")
    print("=" * 60)
    bot = BotRaid(charger_config())
    try:
        return bot.executer()
    except KeyboardInterrupt:
        bot.arret.set()
        print("\nInterruption clavier (Ctrl+C) - arrêt du bot.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
