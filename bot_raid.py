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

Deux modes d'envoi (réglage "MODE") :
    "arriere_plan"  (défaut) la fenêtre du jeu reçoit les touches/clics
                    directement : elle peut rester derrière d'autres fenêtres.
                    Le jeu est retrouvé comme le fait le bot NosSmooth : par le
                    nom du .exe (PROCESS_NAME) puis, à défaut, par le dossier
                    « NostaleData » à côté du .exe ; PROCESS_NAME peut rester
                    vide, et PID force un client précis (0 = auto). La fenêtre
                    visée est celle de classe TNosTaleMainF. Les coordonnées
                    `clic` sont relatives à sa zone cliente (coin haut-gauche
                    = 0, 0). CLIC = "messages" (défaut) ou "curseur_reel" (vrai
                    curseur, pour un jeu qui ignore les clics postés ; la
                    fenêtre doit alors être visible au point cliqué).
    "premier_plan"  touches et clics globaux (pydirectinput) : la fenêtre du jeu
                    doit être au premier plan. Coordonnées `clic` = écran.

Lancement (Windows) :
    pip install pydirectinput        (seulement pour le mode "premier_plan")
    python bot_raid.py            (en administrateur si le jeu l'est aussi)

Touche ÉCHAP = arrêt immédiat, à tout moment.
Les réglages sont lus dans 'config_raid.json' s'il existe (même format que
CONFIG_DEFAUT) ; sinon ce sont les valeurs ci-dessous.
"""

import copy
import ctypes
import json
import os
import sys
import threading
import time

try:
    import pydirectinput
except Exception:
    pydirectinput = None

import entree_arriere_plan
from bot_farm_nostale import (ArretDemande, demarrer_surveillance_echap, dossier_base,
                              echap_pressee)


# ===========================================================================
#                       CONFIGURATION PAR DÉFAUT
#   À adapter : touches, durées, et surtout la durée du raid.
# ===========================================================================
CONFIG_DEFAUT = {
    "MODE": "arriere_plan",   # "arriere_plan" (fenêtre ciblée par processus) ou "premier_plan"
    "PROCESS_NAME": "NostaleClientX.exe",   # Même réglage que le bot de farm ; vide = détection auto
    "PID": 0,                 # Force un client précis (Gestionnaire des tâches > Détails) ; 0 = auto
    "CLIC": "messages",       # "messages" ou "curseur_reel" (mode arrière-plan uniquement)
    "DELAI_DEMARRAGE": 5,     # Compte à rebours avant le 1er tour (secondes)
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


# ---------------------------------------------------------------------------
# CAPTURE D'UN POINT À L'ÉCRAN
# ---------------------------------------------------------------------------
VK_CLIC_GAUCHE = 0x01


def _souris_windows():
    """Retourne (bouton_gauche_enfonce, position) via l'API Win32."""
    if sys.platform != "win32":
        raise RuntimeError("La capture de position nécessite Windows.")
    user32 = ctypes.windll.user32

    class Point(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    def bouton():
        return bool(user32.GetAsyncKeyState(VK_CLIC_GAUCHE) & 0x8000)

    def position():
        point = Point()
        user32.GetCursorPos(ctypes.byref(point))
        return point.x, point.y

    return bouton, position


def capturer_clic(arret=None, timeout=30.0, souris=None, echap=echap_pressee):
    """Attend un clic gauche n'importe où à l'écran et retourne (x, y).

    Retourne None si l'attente est annulée (Échap, `arret` armé) ou expire.
    Le clic en cours au moment de l'appel (celui du bouton de l'interface) est
    ignoré : on attend d'abord que le bouton soit relâché.
    """
    bouton, position = souris or _souris_windows()
    arret = arret or threading.Event()
    fin = time.time() + timeout
    while bouton():
        if arret.is_set() or time.time() > fin:
            return None
        time.sleep(0.01)
    while time.time() < fin:
        if arret.is_set() or echap():
            return None
        if bouton():
            return position()
        time.sleep(0.01)
    return None


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
    mode = config.get("MODE", "arriere_plan")
    if mode == "arriere_plan":
        if config.get("CLIC", "messages") not in ("messages", "curseur_reel"):
            problemes.append("CLIC inconnu %r (messages ou curseur_reel)." % config.get("CLIC"))
        try:
            if int(config.get("PID", 0) or 0) < 0:
                raise ValueError
        except (TypeError, ValueError):
            problemes.append("PID doit être un entier >= 0 (0 = détection automatique).")
    elif mode == "premier_plan":
        if pydirectinput is None:
            problemes.append("Bibliothèque 'pydirectinput' introuvable : pip install pydirectinput")
    else:
        problemes.append("MODE inconnu %r (arriere_plan ou premier_plan)." % mode)
    return problemes


# ---------------------------------------------------------------------------
# MODES D'ENVOI
# ---------------------------------------------------------------------------
class EntreePremierPlan:
    """Touches et clics globaux (pydirectinput) : le jeu doit être au premier plan."""

    def connecter(self):
        return None

    def press(self, touche):
        pydirectinput.press(touche)

    def maintenir(self, touche, duree, pause):
        pydirectinput.keyDown(touche)
        try:
            pause(duree)
        finally:
            pydirectinput.keyUp(touche)

    def click(self, x, y):
        pydirectinput.moveTo(int(x), int(y))
        pydirectinput.click()


class EntreeArrierePlan:
    """Messages envoyés directement à la fenêtre du processus du jeu."""

    PAS_REPETITION = 0.1   # Un clavier réel répète la touche maintenue : on l'imite

    def __init__(self, nom_processus="", pid=0, clic_reel=False, fenetre=None):
        self.fenetre = fenetre or entree_arriere_plan.EntreeFenetre(nom_processus, pid=pid)
        self.clic_reel = clic_reel

    @property
    def rapport(self):
        return self.fenetre.rapport

    def connecter(self):
        return self.fenetre.connecter()

    def press(self, touche):
        self.fenetre.press(touche)

    def maintenir(self, touche, duree, pause):
        self.fenetre.key_down(touche)
        try:
            restant = duree
            while restant > 1e-9:
                pas = min(self.PAS_REPETITION, restant)
                pause(pas)
                restant -= pas
                if restant > 1e-9:
                    self.fenetre.key_down(touche, repetition=True)
        finally:
            self.fenetre.key_up(touche)

    def click(self, x, y):
        if self.clic_reel:
            self.fenetre.click_reel(x, y)
        else:
            self.fenetre.click(x, y)


def construire_entree(config):
    if config.get("MODE", "arriere_plan") == "premier_plan":
        return EntreePremierPlan()
    return EntreeArrierePlan((config.get("PROCESS_NAME") or "").strip(),
                             pid=int(config.get("PID", 0) or 0),
                             clic_reel=config.get("CLIC", "messages") == "curseur_reel")


def ecran_vers_client(nom_processus, point, pid=0, api=None):
    """Convertit un point écran (x, y) en coordonnées de la fenêtre du jeu."""
    fenetre = entree_arriere_plan.EntreeFenetre(nom_processus, api=api, pid=pid)
    fenetre.connecter()
    return fenetre.ecran_vers_client(*point)


# ---------------------------------------------------------------------------
# MOTEUR
# ---------------------------------------------------------------------------
class BotRaid:
    def __init__(self, config, journal=None, arret=None, entree=None):
        self.cfg = config
        self.journal = journal or (lambda message: print(message, flush=True))
        self.arret = arret or threading.Event()
        self.entree = entree

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
        self.entree.press(etape["touche"])

    def _attendre(self, etape):
        self.log("Attente %.1f s" % etape["duree"])
        self.pause(etape["duree"])

    def _maintenir(self, etape):
        self.log("Maintien de '%s' pendant %.1f s" % (etape["touche"], etape["duree"]))
        self.entree.maintenir(etape["touche"], etape["duree"], self.pause)

    def _attaque(self, etape):
        self.log("Auto-attaque ('%s') pendant %.1f s" % (etape["touche"], etape["duree"]))
        fin = time.time() + etape["duree"]
        while time.time() < fin:
            self.verifier_arret()
            self.entree.press(etape["touche"])
            self.pause(min(etape.get("delai", 0.3), max(fin - time.time(), 0)))

    def _clic(self, etape):
        self.verifier_arret()
        self.log("Clic en (%d, %d)" % (etape["x"], etape["y"]))
        self.entree.click(etape["x"], etape["y"])

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

        try:
            self.entree = self.entree or construire_entree(self.cfg)
            self.entree.connecter()
        except Exception as erreur:
            self.log("[Erreur] %s" % erreur)
            return 1
        arriere_plan = self.cfg.get("MODE", "arriere_plan") == "arriere_plan"
        for ligne in getattr(self.entree, "rapport", []):
            self.log(ligne)
        if arriere_plan:
            self.log("Envoi en arrière-plan.")

        demarrer_surveillance_echap(self.arret)
        try:
            for restant in range(int(self.cfg["DELAI_DEMARRAGE"]), 0, -1):
                self.log("Démarrage dans %d s%s" % (
                    restant, "" if arriere_plan else " - mets la fenêtre NosTale au premier plan..."))
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
