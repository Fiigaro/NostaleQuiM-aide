#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bot_farm_nostale.py
===================

Moteur du bot de farm NosTale : lecture de la mémoire vive (pymem) et
simulation de touches bas niveau (pydirectinput).

Ce fichier peut être utilisé de deux façons :

  1. Via l'interface graphique (recommandé) :   python bot_gui.py
  2. En ligne de commande :                     python bot_farm_nostale.py

Dans les deux cas, les réglages sont lus depuis 'config_bot.json' s'il existe,
sinon depuis CONFIG_DEFAUT ci-dessous.

Prérequis (Windows uniquement) :
    pip install pymem pydirectinput

Lance TOUJOURS le programme en tant qu'ADMINISTRATEUR, sinon Windows refuse
l'ouverture du processus du jeu.
"""

import copy
import ctypes
import json
import os
import sys
import threading
import time

# --- Bibliothèques externes (absentes ou inopérantes hors Windows) ---------
try:
    import pymem
    import pymem.process
except Exception:
    pymem = None

try:
    import pydirectinput
except Exception:
    pydirectinput = None


# ===========================================================================
#                       CONFIGURATION PAR DÉFAUT
#   Modifiable ici, ou bien dans l'interface graphique (bot_gui.py), qui
#   enregistre tes réglages dans config_bot.json.
# ===========================================================================
CONFIG_DEFAUT = {
    # --- Processus du client -------------------------------------------
    "PROCESS_NAME": "NostaleClientX.exe",   # Nom exact du .exe du serveur privé
    "MODULE_NAME": "",                      # Vide = module principal du processus

    # --- Offsets mémoire ------------------------------------------------
    # Deux formats acceptés :
    #   * offset simple        : 0x004B21C0        -> [base_module + offset]
    #   * chaîne de pointeurs  : [0x004B21C0, 0x1C, 0x8]
    "OFFSETS_POSITION": {"X": 0x0, "Y": 0x0},
    "OFFSETS_PLAYER_STATS": {"HP": 0x0, "MAX_HP": 0x0, "MP": 0x0, "MAX_MP": 0x0},
    "OFFSETS_TARGET_STATS": {"HP": 0x0, "MAX_HP": 0x0},

    # Taille de lecture : 'int' (4o), 'uint', 'short' (2o), 'ushort', 'byte'.
    # Les coordonnées NosTale sont souvent des 'short'.
    "TYPES_LECTURE": {"POSITION": "int", "PLAYER": "int", "TARGET": "int"},

    # --- Seuils de potions ----------------------------------------------
    "HP_POTION_THRESHOLD": 0.50,   # Boit une potion à moins de 50% de vie
    "MP_POTION_THRESHOLD": 0.30,   # Boit une potion à moins de 30% de mana
    "KEY_HP_POTION": "7",
    "KEY_MP_POTION": "8",
    "POTION_COOLDOWN": 3.0,        # Secondes minimum entre deux potions identiques

    # --- Chemin de farm --------------------------------------------------
    "PATH": [[50, 50], [70, 50], [70, 70]],
    "WAYPOINT_TOLERANCE": 2,       # Point validé à moins de 2 cases
    "BOUCLER_CHEMIN": True,        # Repart au premier point une fois la route finie

    # --- Ciblage et combat ------------------------------------------------
    "KEY_TARGET": "space",                     # Barre Espace = monstre le plus proche
    "ATTACK_KEYS": ["space", "1", "2", "3"],   # Touches jouées en boucle
    "ATTACK_DELAY": 0.3,                       # Délai entre deux coups
    "TARGET_DELAY": 0.35,                      # Attente après ciblage avant relecture
    "COMBAT_TIMEOUT": 45.0,                    # Abandon d'un combat trop long

    # --- Déplacement ------------------------------------------------------
    "MOVEMENT_MODE": "CLICK",              # 'CLICK' (souris) ou 'KEYS' (flèches)
    "MAX_STEP_TILES": 5,                   # Cases max parcourues par ordre
    "MOVE_DELAY": 0.6,                     # Attente après un ordre de déplacement
    "CHARACTER_SCREEN_POS": [960, 540],    # Position du perso à l'écran (1920x1080)
    "TILE_SIZE_PX": [24, 24],              # Taille d'une case en pixels (L, H)
    "MOVE_KEYS": {"HAUT": "up", "BAS": "down", "GAUCHE": "left", "DROITE": "right"},
    "MOVE_KEY_DURATION": 0.35,             # Durée d'appui sur une flèche

    # --- Divers -----------------------------------------------------------
    "DELAI_DEMARRAGE": 5,   # Compte à rebours avant lancement (secondes)
    "BLOCAGE_MAX": 5,       # Itérations sans bouger avant tentative de déblocage
}

VK_ECHAP = 0x1B   # Code virtuel de la touche Échap
_user32 = ctypes.windll.user32 if sys.platform == "win32" else None


class ArretDemande(Exception):
    """Levée quand la touche Échap est pressée ou que l'arrêt est demandé."""


# ---------------------------------------------------------------------------
# FICHIER DE CONFIGURATION
# ---------------------------------------------------------------------------
def dossier_base():
    """Dossier de travail : à côté du .exe une fois compilé, sinon du script."""
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


FICHIER_CONFIG = os.path.join(dossier_base(), "config_bot.json")


def _fusionner(base, ajout):
    """Fusion récursive : les clés absentes gardent leur valeur par défaut."""
    for cle, valeur in ajout.items():
        if cle in base and isinstance(base[cle], dict) and isinstance(valeur, dict):
            _fusionner(base[cle], valeur)
        else:
            base[cle] = valeur
    return base


def charger_config(chemin=None):
    """Charge config_bot.json par-dessus la configuration par défaut."""
    chemin = chemin or FICHIER_CONFIG
    config = copy.deepcopy(CONFIG_DEFAUT)
    if os.path.exists(chemin):
        with open(chemin, "r", encoding="utf-8") as fichier:
            _fusionner(config, json.load(fichier))
    return config


def sauver_config(config, chemin=None):
    """Enregistre la configuration au format JSON."""
    chemin = chemin or FICHIER_CONFIG
    with open(chemin, "w", encoding="utf-8") as fichier:
        json.dump(config, fichier, indent=4, ensure_ascii=False)
    return chemin


def valider_config(config):
    """Retourne la liste des problèmes bloquants (vide si tout va bien)."""
    problemes = []

    for nom in ("OFFSETS_POSITION", "OFFSETS_PLAYER_STATS", "OFFSETS_TARGET_STATS"):
        for cle, valeur in config[nom].items():
            if not valeur:
                problemes.append("Offset non renseigné : %s > %s" % (nom, cle))

    if not config["PATH"]:
        problemes.append("Le chemin (PATH) est vide : ajoute au moins un point.")

    if not config["ATTACK_KEYS"]:
        problemes.append("Aucune touche d'attaque définie.")

    if sys.platform != "win32":
        problemes.append("Ce bot nécessite Windows (pymem et pydirectinput utilisent l'API Win32).")
    else:
        if pymem is None:
            problemes.append("Bibliothèque 'pymem' introuvable : pip install pymem")
        if pydirectinput is None:
            problemes.append("Bibliothèque 'pydirectinput' introuvable : pip install pydirectinput")

    return problemes


# ---------------------------------------------------------------------------
# LISTE DES PROCESSUS EN COURS
# ---------------------------------------------------------------------------
# Mots-clés servant à remonter les candidats plausibles en haut de la liste.
INDICES_JEU = ("nos", "tale", "client", "game", "launcher")


def lister_processus():
    """Retourne [(nom_exe, pid), ...] trié, sans doublon.

    pymem.process.list_processes() réutilise la même structure à chaque
    itération : les champs doivent être lus dans la boucle, pas après.
    """
    if pymem is None:
        return []

    vus = set()
    processus = []
    try:
        for entree in pymem.process.list_processes():
            try:
                nom = entree.szExeFile.decode("utf-8", errors="ignore")
                pid = int(entree.th32ProcessID)
            except Exception:
                continue
            if not nom or (nom, pid) in vus:
                continue
            vus.add((nom, pid))
            processus.append((nom, pid))
    except Exception:
        # API Windows indisponible : la liste reste vide, l'appelant le signale.
        return processus

    processus.sort(key=lambda p: (p[0].lower(), p[1]))
    return processus


def est_candidat_jeu(nom):
    """Vrai si le nom du processus ressemble à un client de jeu."""
    minuscules = nom.lower()
    return any(indice in minuscules for indice in INDICES_JEU)


# ---------------------------------------------------------------------------
# LECTURE MÉMOIRE
# ---------------------------------------------------------------------------
class MemoireNostale:
    def __init__(self, process_name, module_name=None):
        self.process_name = process_name
        self.module_name = module_name or process_name
        self.pm = None
        self.base = 0
        # Les clients NosTale sont en 32 bits ; corrigé à la connexion.
        self.taille_pointeur = 4

    def connecter(self):
        if pymem is None:
            raise RuntimeError("Bibliothèque 'pymem' indisponible sur ce système.")
        self.pm = pymem.Pymem(self.process_name)
        module = pymem.process.module_from_name(self.pm.process_handle, self.module_name)
        if module is None:
            raise RuntimeError("Module '%s' introuvable dans le processus." % self.module_name)
        self.base = module.lpBaseOfDll
        try:
            self.taille_pointeur = 8 if pymem.process.is_64_bit(self.pm.process_handle) else 4
        except Exception:
            self.taille_pointeur = 4
        return self.base

    def _lire_pointeur(self, adresse):
        """Déréférence un pointeur, à la taille du processus visé."""
        if self.taille_pointeur == 8:
            return self.pm.read_ulonglong(adresse)
        return self.pm.read_uint(adresse)

    def _adresse(self, spec):
        """Résout un offset simple ou une chaîne de pointeurs."""
        if isinstance(spec, (list, tuple)):
            if not spec:
                return None
            adresse = self.base + spec[0]
            for offset in spec[1:]:
                adresse = self._lire_pointeur(adresse) + offset
            return adresse
        return self.base + spec

    def lire(self, spec, type_lecture="int"):
        """Lit une valeur en mémoire. Retourne None si la lecture échoue."""
        try:
            adresse = self._adresse(spec)
            if adresse is None:
                return None
            if type_lecture == "short":
                return self.pm.read_short(adresse)
            if type_lecture == "ushort":
                return self.pm.read_ushort(adresse)
            if type_lecture == "uint":
                return self.pm.read_uint(adresse)
            if type_lecture == "byte":
                return self.pm.read_bytes(adresse, 1)[0]
            return self.pm.read_int(adresse)
        except Exception:
            return None


# ---------------------------------------------------------------------------
# MOTEUR DU BOT
# ---------------------------------------------------------------------------
class BotFarm:
    """Bot de farm. `journal` reçoit chaque ligne de log, `arret` coupe le bot."""

    def __init__(self, config, journal=None, arret=None):
        self.cfg = config
        self.journal = journal or (lambda message: print(message, flush=True))
        self.arret = arret or threading.Event()
        self.mem = MemoireNostale(config["PROCESS_NAME"], config["MODULE_NAME"] or None)
        self._dernieres_potions = {"HP": 0.0, "MP": 0.0}

    # --- Utilitaires ------------------------------------------------------
    def log(self, message):
        self.journal("[%s] %s" % (time.strftime("%H:%M:%S"), message))

    def verifier_arret(self):
        if self.arret.is_set():
            raise ArretDemande()

    def pause(self, duree):
        """Sleep interruptible : réagit à l'arrêt immédiatement."""
        if self.arret.wait(duree):
            raise ArretDemande()

    def appuyer(self, touche):
        """Appui de touche bas niveau (DirectInput), avec contrôle d'arrêt."""
        self.verifier_arret()
        pydirectinput.press(touche)

    def connecter(self):
        self.log("[Init] Connexion au processus '%s'..." % self.cfg["PROCESS_NAME"])
        base = self.mem.connecter()
        self.log("[Init] Processus attaché - adresse de base du module : 0x%X" % base)
        self.log("[Init] Client %d bits - chaînes de pointeurs lues sur %d octets."
                 % (self.mem.taille_pointeur * 8, self.mem.taille_pointeur))

    def lire_etat(self):
        """Lecture ponctuelle de toutes les valeurs suivies (bouton de test)."""
        cfg = self.cfg
        pos, joueur, cible = cfg["OFFSETS_POSITION"], cfg["OFFSETS_PLAYER_STATS"], cfg["OFFSETS_TARGET_STATS"]
        types = cfg["TYPES_LECTURE"]
        return {
            "X": self.mem.lire(pos["X"], types["POSITION"]),
            "Y": self.mem.lire(pos["Y"], types["POSITION"]),
            "PV": self.mem.lire(joueur["HP"], types["PLAYER"]),
            "PV_MAX": self.mem.lire(joueur["MAX_HP"], types["PLAYER"]),
            "PM": self.mem.lire(joueur["MP"], types["PLAYER"]),
            "PM_MAX": self.mem.lire(joueur["MAX_MP"], types["PLAYER"]),
            "CIBLE_PV": self.mem.lire(cible["HP"], types["TARGET"]),
            "CIBLE_PV_MAX": self.mem.lire(cible["MAX_HP"], types["TARGET"]),
        }

    # --- ÉTAPE 1 : SÉCURITÉ POTIONS --------------------------------------
    def gerer_potions(self):
        """Boit une potion si un seuil est franchi. Retourne 'mort' à 0 PV."""
        self.verifier_arret()
        cfg = self.cfg
        offsets = cfg["OFFSETS_PLAYER_STATS"]
        type_joueur = cfg["TYPES_LECTURE"]["PLAYER"]

        pv = self.mem.lire(offsets["HP"], type_joueur)
        pv_max = self.mem.lire(offsets["MAX_HP"], type_joueur)
        pm = self.mem.lire(offsets["MP"], type_joueur)
        pm_max = self.mem.lire(offsets["MAX_MP"], type_joueur)
        maintenant = time.time()

        if pv is not None and pv_max:
            if pv <= 0:
                self.log("[Sécurité] Personnage à 0 PV - le bot s'arrête.")
                return "mort"
            ratio = pv / float(pv_max)
            if ratio < cfg["HP_POTION_THRESHOLD"]:
                if maintenant - self._dernieres_potions["HP"] >= cfg["POTION_COOLDOWN"]:
                    self.log("[Sécurité] Utilisation potion de Vie - PV : %d/%d (%.0f%%)"
                             % (pv, pv_max, ratio * 100))
                    self.appuyer(cfg["KEY_HP_POTION"])
                    self._dernieres_potions["HP"] = maintenant
                    self.pause(0.5)

        if pm is not None and pm_max:
            ratio = pm / float(pm_max)
            if ratio < cfg["MP_POTION_THRESHOLD"]:
                if maintenant - self._dernieres_potions["MP"] >= cfg["POTION_COOLDOWN"]:
                    self.log("[Sécurité] Utilisation potion de Mana - PM : %d/%d (%.0f%%)"
                             % (pm, pm_max, ratio * 100))
                    self.appuyer(cfg["KEY_MP_POTION"])
                    self._dernieres_potions["MP"] = maintenant
                    self.pause(0.5)

        return None

    # --- ÉTAPE 2 : CIBLAGE MONSTRE ---------------------------------------
    def cibler_monstre(self):
        """Appuie sur la touche de ciblage et retourne les PV de la cible."""
        self.appuyer(self.cfg["KEY_TARGET"])
        self.pause(self.cfg["TARGET_DELAY"])
        return self.mem.lire(self.cfg["OFFSETS_TARGET_STATS"]["HP"],
                             self.cfg["TYPES_LECTURE"]["TARGET"])

    # --- ÉTAPE 3 : BOUCLE DE COMBAT PRIORITAIRE ---------------------------
    def boucle_combat(self):
        """Frappe la cible jusqu'à sa mort. Le déplacement est suspendu ici."""
        cfg = self.cfg
        type_cible = cfg["TYPES_LECTURE"]["TARGET"]
        offset_pv = cfg["OFFSETS_TARGET_STATS"]["HP"]

        pv_max = self.mem.lire(cfg["OFFSETS_TARGET_STATS"]["MAX_HP"], type_cible)
        if pv_max:
            self.log("[Combat] Monstre engagé - PV max : %d" % pv_max)
        else:
            self.log("[Combat] Monstre engagé.")

        debut = time.time()
        index_touche = 0

        while True:
            self.verifier_arret()

            pv_cible = self.mem.lire(offset_pv, type_cible)
            if pv_cible is None or pv_cible <= 0:
                self.log("[Combat] Monstre éliminé - auto-loot du serveur, aucun ramassage.")
                return True

            if time.time() - debut > cfg["COMBAT_TIMEOUT"]:
                self.log("[Combat] Combat trop long (%.0fs) - abandon de la cible."
                         % cfg["COMBAT_TIMEOUT"])
                return False

            touche = cfg["ATTACK_KEYS"][index_touche % len(cfg["ATTACK_KEYS"])]
            index_touche += 1

            self.appuyer(touche)
            self.log("[Combat] Attaque du monstre (touche '%s') - PV restants : %d"
                     % (touche, pv_cible))

            # Sécurité PV/PM maintenue pendant le combat : c'est là qu'on meurt.
            if self.gerer_potions() == "mort":
                return False

            self.pause(cfg["ATTACK_DELAY"])

    # --- ÉTAPE 5 : DÉPLACEMENT SUR LE CHEMIN ------------------------------
    @staticmethod
    def _signe(valeur):
        return (valeur > 0) - (valeur < 0)

    def _deplacer_par_clic(self, pas_x, pas_y):
        """Clic sur la case visée, projetée depuis la position du perso à l'écran."""
        centre = self.cfg["CHARACTER_SCREEN_POS"]
        case = self.cfg["TILE_SIZE_PX"]
        ecran_x = int(centre[0] + pas_x * case[0])
        ecran_y = int(centre[1] + pas_y * case[1])
        pydirectinput.moveTo(ecran_x, ecran_y)
        pydirectinput.click()
        return "clic en (%d, %d) px" % (ecran_x, ecran_y)

    def _deplacer_par_touches(self, dx, dy):
        """Appui sur la flèche correspondant à l'axe dominant."""
        touches = self.cfg["MOVE_KEYS"]
        if abs(dx) >= abs(dy):
            touche = touches["DROITE"] if dx > 0 else touches["GAUCHE"]
        else:
            touche = touches["BAS"] if dy > 0 else touches["HAUT"]
        pydirectinput.keyDown(touche)
        try:
            self.pause(self.cfg["MOVE_KEY_DURATION"])
        finally:
            pydirectinput.keyUp(touche)
        return "touche '%s'" % touche

    def avancer_vers(self, point):
        """Avance d'un pas vers `point`.

        Retourne 'atteint', 'erreur' (position illisible) ou 'avance'.
        """
        self.verifier_arret()
        cfg = self.cfg
        type_position = cfg["TYPES_LECTURE"]["POSITION"]
        x = self.mem.lire(cfg["OFFSETS_POSITION"]["X"], type_position)
        y = self.mem.lire(cfg["OFFSETS_POSITION"]["Y"], type_position)

        if x is None or y is None:
            self.log("[Déplacement] Lecture de la position impossible - vérifie les offsets.")
            return "erreur"

        dx = point[0] - x
        dy = point[1] - y
        distance = max(abs(dx), abs(dy))

        if distance <= cfg["WAYPOINT_TOLERANCE"]:
            self.log("[Déplacement] Point (%d, %d) atteint depuis (%d, %d)."
                     % (point[0], point[1], x, y))
            return "atteint"

        # Un ordre de déplacement est limité à MAX_STEP_TILES cases : on avance
        # pas à pas pour re-vérifier monstres et PV entre chaque avancée.
        maximum = cfg["MAX_STEP_TILES"]
        if distance > maximum:
            facteur = maximum / float(distance)
            pas_x = int(round(dx * facteur)) or self._signe(dx)
            pas_y = int(round(dy * facteur)) or self._signe(dy)
        else:
            pas_x, pas_y = dx, dy

        if cfg["MOVEMENT_MODE"] == "KEYS":
            detail = self._deplacer_par_touches(dx, dy)
        else:
            detail = self._deplacer_par_clic(pas_x, pas_y)

        self.log("[Déplacement] Position (%d, %d) -> objectif (%d, %d) | distance %d cases | %s"
                 % (x, y, point[0], point[1], distance, detail))

        self.pause(cfg["MOVE_DELAY"])
        return "avance"

    def debloquer(self):
        """Petit mouvement latéral quand la position ne change plus."""
        self.log("[Déplacement] Personnage bloqué - tentative de déblocage.")
        maximum = self.cfg["MAX_STEP_TILES"]
        if self.cfg["MOVEMENT_MODE"] == "KEYS":
            self._deplacer_par_touches(-maximum, 0)
        else:
            self._deplacer_par_clic(-maximum, -maximum)
        self.pause(self.cfg["MOVE_DELAY"])

    # --- BOUCLE PRINCIPALE -------------------------------------------------
    def boucle_principale(self):
        cfg = self.cfg
        index_point = 0
        derniere_position = None
        compteur_blocage = 0

        self.log("[Init] Bot lancé. Chemin de %d point(s). Appuie sur ÉCHAP pour tout couper."
                 % len(cfg["PATH"]))

        while True:
            self.verifier_arret()

            # --- ÉTAPE 1 : SÉCURITÉ POTIONS -------------------------------
            if self.gerer_potions() == "mort":
                return

            # --- ÉTAPE 2 : CIBLAGE MONSTRE --------------------------------
            pv_cible = self.cibler_monstre()

            # --- ÉTAPE 3 : COMBAT PRIORITAIRE -----------------------------
            if pv_cible is not None and pv_cible > 0:
                # ÉTAPE 4 : aucun ramassage, l'auto-loot du serveur s'en charge.
                # On repart directement au ciblage pour vider la zone.
                self.boucle_combat()
                continue

            # --- ÉTAPE 5 : DÉPLACEMENT SUR LE CHEMIN ----------------------
            if index_point >= len(cfg["PATH"]):
                if not cfg["BOUCLER_CHEMIN"]:
                    self.log("[Déplacement] Fin du chemin atteinte - arrêt du bot.")
                    return
                index_point = 0
                self.log("[Déplacement] Chemin terminé - reprise au premier point.")

            point = cfg["PATH"][index_point]
            self.log("[Recherche] Aucun monstre à proximité - direction le point %d/%d (%d, %d)."
                     % (index_point + 1, len(cfg["PATH"]), point[0], point[1]))

            resultat = self.avancer_vers(point)

            if resultat == "atteint":
                index_point += 1
                compteur_blocage = 0
                continue

            if resultat == "erreur":
                self.pause(1.0)
                continue

            # Détection de blocage : la position ne bouge plus entre deux ordres.
            type_position = cfg["TYPES_LECTURE"]["POSITION"]
            position = (self.mem.lire(cfg["OFFSETS_POSITION"]["X"], type_position),
                        self.mem.lire(cfg["OFFSETS_POSITION"]["Y"], type_position))
            if position == derniere_position:
                compteur_blocage += 1
                if compteur_blocage >= cfg["BLOCAGE_MAX"]:
                    self.debloquer()
                    compteur_blocage = 0
            else:
                compteur_blocage = 0
            derniere_position = position

    def executer(self):
        """Point d'entrée complet : validation, connexion, compte à rebours, farm."""
        problemes = valider_config(self.cfg)
        if problemes:
            self.log("[Erreur] Configuration incomplète :")
            for probleme in problemes:
                self.log("         - %s" % probleme)
            return 1

        try:
            self.connecter()
        except Exception as erreur:
            if "ProcessNotFound" in type(erreur).__name__:
                self.log("[Erreur] Processus '%s' introuvable. Le jeu est-il lancé ?"
                         % self.cfg["PROCESS_NAME"])
                self.log("         Utilise le bouton « Détecter... » pour choisir le bon "
                         "processus dans la liste.")
            else:
                self.log("[Erreur] Impossible de s'attacher au processus : %s" % erreur)
                self.log("         Lance le programme en tant qu'administrateur.")
            return 1

        demarrer_surveillance_echap(self.arret)

        try:
            for restant in range(int(self.cfg["DELAI_DEMARRAGE"]), 0, -1):
                self.log("[Init] Démarrage dans %d s - mets la fenêtre NosTale au premier plan..."
                         % restant)
                self.pause(1.0)
            self.boucle_principale()
        except ArretDemande:
            self.log("[Sécurité] Arrêt demandé (touche ÉCHAP ou bouton Stop) - bot coupé.")
        except Exception as erreur:
            self.log("[Erreur] Arrêt sur exception : %s" % erreur)
            return 1
        finally:
            self.arret.set()
            self.log("[Init] Bot arrêté proprement.")

        return 0


# ---------------------------------------------------------------------------
# SURVEILLANCE DE LA TOUCHE ÉCHAP
# ---------------------------------------------------------------------------
def echap_pressee():
    if _user32 is None:
        return False
    return bool(_user32.GetAsyncKeyState(VK_ECHAP) & 0x8000)


def demarrer_surveillance_echap(arret):
    """Thread de fond : arme le drapeau d'arrêt dès que Échap est pressée."""
    def surveiller():
        while not arret.is_set():
            if echap_pressee():
                arret.set()
                return
            time.sleep(0.05)

    threading.Thread(target=surveiller, daemon=True).start()


# ---------------------------------------------------------------------------
# MODE LIGNE DE COMMANDE
# ---------------------------------------------------------------------------
def main():
    print("=" * 68)
    print(" BOT DE FARM NOSTALE - lecture mémoire + envoi de touches")
    print(" Sécurité : touche ÉCHAP = arrêt immédiat")
    print(" Interface graphique : python bot_gui.py")
    print("=" * 68)

    config = charger_config()
    bot = BotFarm(config)
    try:
        return bot.executer()
    except KeyboardInterrupt:
        bot.arret.set()
        print("\n[Sécurité] Interruption clavier (Ctrl+C) - arrêt du bot.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
