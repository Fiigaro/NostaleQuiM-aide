#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bot_farm_nostale.py
===================

Bot de farm pour serveur privé NosTale, basé sur la lecture de la mémoire vive
(pymem) et la simulation de touches bas niveau (pydirectinput).

Prérequis (Windows uniquement) :
    pip install pymem pydirectinput

Utilisation :
    1. Lance le client NosTale et connecte-toi.
    2. Renseigne les OFFSETS dans la section CONFIGURATION ci-dessous
       (Cheat Engine / ReClass sont les outils habituels pour les trouver).
    3. Lance le script en tant qu'ADMINISTRATEUR :  python bot_farm_nostale.py
    4. Bascule sur la fenêtre du jeu pendant le compte à rebours.
    5. Appuie sur ÉCHAP à tout moment pour couper le bot instantanément.

Note : le script doit tourner en 32 bits ou 64 bits selon le client visé.
Si pymem lève "Could not open process", lance la console en administrateur.
"""

import ctypes
import sys
import threading
import time

# ---------------------------------------------------------------------------
# IMPORTS DES BIBLIOTHÈQUES EXTERNES
# ---------------------------------------------------------------------------
try:
    import pymem
    import pymem.process
except ImportError:
    print("[Erreur] Bibliothèque 'pymem' introuvable. Installe-la avec : pip install pymem")
    sys.exit(1)

try:
    import pydirectinput
except ImportError:
    print("[Erreur] Bibliothèque 'pydirectinput' introuvable. Installe-la avec : pip install pydirectinput")
    sys.exit(1)


# ===========================================================================
#                            CONFIGURATION
#          >>> C'EST LA SEULE SECTION QUE TU DOIS MODIFIER <<<
# ===========================================================================

# --- Processus du client ---------------------------------------------------
PROCESS_NAME = "NostaleClientX.exe"   # Nom exact du .exe du serveur privé
MODULE_NAME = None                    # None = utilise le module principal (PROCESS_NAME)

# --- Offsets mémoire -------------------------------------------------------
# Deux formats acceptés pour chaque valeur :
#   * un offset simple      : 0x004B21C0            -> lu à [base_module + offset]
#   * une chaîne de pointeurs : [0x004B21C0, 0x1C, 0x8] -> pointeur statique + offsets
OFFSETS_POSITION = {
    'X': 0x0,
    'Y': 0x0,
}

OFFSETS_PLAYER_STATS = {
    'HP': 0x0,
    'MAX_HP': 0x0,
    'MP': 0x0,
    'MAX_MP': 0x0,
}

OFFSETS_TARGET_STATS = {
    'HP': 0x0,
    'MAX_HP': 0x0,
}

# Taille de lecture par groupe de valeurs : 'int' (4o), 'uint', 'short' (2o),
# 'ushort' ou 'byte'. Les coordonnées NosTale sont souvent des 'short'.
TYPES_LECTURE = {
    'POSITION': 'int',
    'PLAYER': 'int',
    'TARGET': 'int',
}

# --- Seuils de potions -----------------------------------------------------
HP_POTION_THRESHOLD = 0.50   # Boit une potion à moins de 50% de vie
MP_POTION_THRESHOLD = 0.30   # Boit une potion à moins de 30% de mana

KEY_HP_POTION = '7'
KEY_MP_POTION = '8'
POTION_COOLDOWN = 3.0        # Secondes minimum entre deux potions du même type

# --- Chemin de farm --------------------------------------------------------
PATH = [(50, 50), (70, 50), (70, 70)]   # Liste de coordonnées de ma route carrée
WAYPOINT_TOLERANCE = 2                  # Point considéré atteint à moins de 2 cases
BOUCLER_CHEMIN = True                   # True = repart au premier point à la fin

# --- Ciblage et combat -----------------------------------------------------
KEY_TARGET = 'space'                 # Barre Espace = cible le monstre le plus proche
ATTACK_KEYS = ['space', '1', '2', '3']   # Touches d'attaque jouées en boucle
ATTACK_DELAY = 0.3                   # Délai entre deux coups (secondes)
TARGET_DELAY = 0.35                  # Attente après le ciblage avant de relire la mémoire
COMBAT_TIMEOUT = 45.0                # Sécurité : abandonne un combat trop long

# --- Déplacement -----------------------------------------------------------
MOVEMENT_MODE = 'CLICK'              # 'CLICK' (clic souris, standard NosTale) ou 'KEYS'
MAX_STEP_TILES = 5                   # Nombre de cases max parcourues par clic
MOVE_DELAY = 0.6                     # Attente après un ordre de déplacement

# Mode CLICK : projection case -> pixel. À calibrer sur ta résolution.
CHARACTER_SCREEN_POS = (960, 540)    # Position du perso à l'écran (centre écran 1920x1080)
TILE_SIZE_PX = (24, 24)              # Taille d'une case en pixels (largeur, hauteur)

# Mode KEYS : touches directionnelles
MOVE_KEYS = {'HAUT': 'up', 'BAS': 'down', 'GAUCHE': 'left', 'DROITE': 'right'}
MOVE_KEY_DURATION = 0.35             # Durée d'appui sur la flèche

# --- Divers ----------------------------------------------------------------
DELAI_DEMARRAGE = 5                  # Compte à rebours avant le lancement (secondes)
BLOCAGE_MAX = 5                      # Itérations sans bouger avant déblocage
VK_ECHAP = 0x1B                      # Code virtuel de la touche Échap

# ===========================================================================
#                     FIN DE LA CONFIGURATION
# ===========================================================================


pydirectinput.FAILSAFE = False   # La sécurité, c'est la touche Échap
pydirectinput.PAUSE = 0.03

_arret = threading.Event()
_user32 = ctypes.windll.user32 if sys.platform == 'win32' else None


class ArretDemande(Exception):
    """Levée quand la touche Échap est pressée."""


# ---------------------------------------------------------------------------
# UTILITAIRES : LOGS, ARRÊT D'URGENCE, PAUSES
# ---------------------------------------------------------------------------
def log(message):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), message), flush=True)


def _echap_pressee():
    if _user32 is None:
        return False
    return bool(_user32.GetAsyncKeyState(VK_ECHAP) & 0x8000)


def _surveiller_echap():
    """Thread de fond : arme le drapeau d'arrêt dès que Échap est pressée."""
    while not _arret.is_set():
        if _echap_pressee():
            _arret.set()
            return
        time.sleep(0.05)


def verifier_arret():
    if _arret.is_set():
        raise ArretDemande()


def pause(duree):
    """Sleep interruptible : réagit à Échap immédiatement."""
    if _arret.wait(duree):
        raise ArretDemande()


def appuyer(touche):
    """Appui de touche bas niveau (DirectInput), avec contrôle d'arrêt."""
    verifier_arret()
    pydirectinput.press(touche)


# ---------------------------------------------------------------------------
# LECTURE MÉMOIRE
# ---------------------------------------------------------------------------
class MemoireNostale:
    def __init__(self, process_name, module_name=None):
        self.process_name = process_name
        self.module_name = module_name or process_name
        self.pm = None
        self.base = 0

    def connecter(self):
        log("[Init] Connexion au processus '%s'..." % self.process_name)
        self.pm = pymem.Pymem(self.process_name)
        module = pymem.process.module_from_name(self.pm.process_handle, self.module_name)
        if module is None:
            raise RuntimeError("Module '%s' introuvable dans le processus." % self.module_name)
        self.base = module.lpBaseOfDll
        log("[Init] Processus attaché - adresse de base du module : 0x%X" % self.base)

    def _adresse(self, spec):
        """Résout un offset simple ou une chaîne de pointeurs."""
        if isinstance(spec, (list, tuple)):
            if not spec:
                return None
            adresse = self.base + spec[0]
            for offset in spec[1:]:
                adresse = self.pm.read_uint(adresse) + offset
            return adresse
        return self.base + spec

    def lire(self, spec, type_lecture='int'):
        """Lit une valeur en mémoire. Retourne None en cas d'échec de lecture."""
        try:
            adresse = self._adresse(spec)
            if adresse is None:
                return None
            if type_lecture == 'short':
                return self.pm.read_short(adresse)
            if type_lecture == 'ushort':
                return self.pm.read_ushort(adresse)
            if type_lecture == 'uint':
                return self.pm.read_uint(adresse)
            if type_lecture == 'byte':
                return self.pm.read_bytes(adresse, 1)[0]
            return self.pm.read_int(adresse)
        except Exception:
            return None


# ---------------------------------------------------------------------------
# VÉRIFICATION DE LA CONFIGURATION
# ---------------------------------------------------------------------------
def verifier_configuration():
    manquants = []
    for nom, groupe in (('OFFSETS_POSITION', OFFSETS_POSITION),
                        ('OFFSETS_PLAYER_STATS', OFFSETS_PLAYER_STATS),
                        ('OFFSETS_TARGET_STATS', OFFSETS_TARGET_STATS)):
        for cle, valeur in groupe.items():
            if valeur == 0x0 or valeur == [] or valeur == ():
                manquants.append("%s['%s']" % (nom, cle))

    if manquants:
        log("[Erreur] Offsets mémoire non renseignés (encore à 0x0) :")
        for entree in manquants:
            log("         - %s" % entree)
        log("[Erreur] Renseigne-les dans la section CONFIGURATION avant de lancer le bot.")
        log("         Sans adresses valides, le bot lirait des données aléatoires.")
        return False

    if not PATH:
        log("[Erreur] La liste PATH est vide : ajoute au moins un point de passage.")
        return False

    if sys.platform != 'win32':
        log("[Erreur] Ce bot nécessite Windows (pymem et pydirectinput utilisent l'API Win32).")
        return False

    return True


# ---------------------------------------------------------------------------
# ÉTAPE 1 : SÉCURITÉ POTIONS
# ---------------------------------------------------------------------------
_dernieres_potions = {'HP': 0.0, 'MP': 0.0}


def gerer_potions(mem):
    """Lit PV/PM et boit une potion si un seuil est franchi.

    Retourne 'mort' si le personnage est à 0 PV, sinon None.
    """
    verifier_arret()
    type_joueur = TYPES_LECTURE['PLAYER']

    pv = mem.lire(OFFSETS_PLAYER_STATS['HP'], type_joueur)
    pv_max = mem.lire(OFFSETS_PLAYER_STATS['MAX_HP'], type_joueur)
    pm = mem.lire(OFFSETS_PLAYER_STATS['MP'], type_joueur)
    pm_max = mem.lire(OFFSETS_PLAYER_STATS['MAX_MP'], type_joueur)

    maintenant = time.time()

    if pv is not None and pv_max:
        if pv <= 0:
            log("[Sécurité] Personnage à 0 PV - le bot s'arrête.")
            return 'mort'
        ratio_pv = pv / float(pv_max)
        if ratio_pv < HP_POTION_THRESHOLD:
            if maintenant - _dernieres_potions['HP'] >= POTION_COOLDOWN:
                log("[Sécurité] Utilisation potion de Vie - PV : %d/%d (%.0f%%)"
                    % (pv, pv_max, ratio_pv * 100))
                appuyer(KEY_HP_POTION)
                _dernieres_potions['HP'] = maintenant
                pause(0.5)

    if pm is not None and pm_max:
        ratio_pm = pm / float(pm_max)
        if ratio_pm < MP_POTION_THRESHOLD:
            if maintenant - _dernieres_potions['MP'] >= POTION_COOLDOWN:
                log("[Sécurité] Utilisation potion de Mana - PM : %d/%d (%.0f%%)"
                    % (pm, pm_max, ratio_pm * 100))
                appuyer(KEY_MP_POTION)
                _dernieres_potions['MP'] = maintenant
                pause(0.5)

    return None


# ---------------------------------------------------------------------------
# ÉTAPE 2 : CIBLAGE DU MONSTRE
# ---------------------------------------------------------------------------
def cibler_monstre(mem):
    """Appuie sur la touche de ciblage et retourne les PV de la cible (ou None)."""
    appuyer(KEY_TARGET)
    pause(TARGET_DELAY)
    return mem.lire(OFFSETS_TARGET_STATS['HP'], TYPES_LECTURE['TARGET'])


# ---------------------------------------------------------------------------
# ÉTAPE 3 : BOUCLE DE COMBAT PRIORITAIRE
# ---------------------------------------------------------------------------
def boucle_combat(mem):
    """Frappe la cible jusqu'à sa mort. Le déplacement est totalement suspendu ici."""
    type_cible = TYPES_LECTURE['TARGET']
    pv_max_cible = mem.lire(OFFSETS_TARGET_STATS['MAX_HP'], type_cible)
    if pv_max_cible:
        log("[Combat] Monstre engagé - PV max : %d" % pv_max_cible)
    else:
        log("[Combat] Monstre engagé.")

    debut = time.time()
    index_touche = 0

    while True:
        verifier_arret()

        pv_cible = mem.lire(OFFSETS_TARGET_STATS['HP'], type_cible)
        if pv_cible is None or pv_cible <= 0:
            log("[Combat] Monstre éliminé - auto-loot du serveur, aucun ramassage.")
            return True

        if time.time() - debut > COMBAT_TIMEOUT:
            log("[Combat] Combat trop long (%.0fs) - abandon de la cible." % COMBAT_TIMEOUT)
            return False

        touche = ATTACK_KEYS[index_touche % len(ATTACK_KEYS)]
        index_touche += 1

        appuyer(touche)
        log("[Combat] Attaque du monstre (touche '%s') - PV restants : %d" % (touche, pv_cible))

        # Sécurité PV/PM maintenue pendant le combat : c'est là qu'on meurt.
        if gerer_potions(mem) == 'mort':
            return False

        pause(ATTACK_DELAY)


# ---------------------------------------------------------------------------
# ÉTAPE 5 : DÉPLACEMENT SUR LE CHEMIN
# ---------------------------------------------------------------------------
def _signe(valeur):
    return (valeur > 0) - (valeur < 0)


def _deplacer_par_clic(pas_x, pas_y):
    """Clic sur la case visée, projetée depuis la position du perso à l'écran."""
    ecran_x = int(CHARACTER_SCREEN_POS[0] + pas_x * TILE_SIZE_PX[0])
    ecran_y = int(CHARACTER_SCREEN_POS[1] + pas_y * TILE_SIZE_PX[1])
    pydirectinput.moveTo(ecran_x, ecran_y)
    pydirectinput.click()
    return "clic en (%d, %d) px" % (ecran_x, ecran_y)


def _deplacer_par_touches(dx, dy):
    """Appui sur la flèche correspondant à l'axe dominant."""
    if abs(dx) >= abs(dy):
        touche = MOVE_KEYS['DROITE'] if dx > 0 else MOVE_KEYS['GAUCHE']
    else:
        touche = MOVE_KEYS['BAS'] if dy > 0 else MOVE_KEYS['HAUT']
    pydirectinput.keyDown(touche)
    try:
        pause(MOVE_KEY_DURATION)
    finally:
        pydirectinput.keyUp(touche)
    return "touche '%s'" % touche


def avancer_vers(mem, point):
    """Avance d'un pas vers `point`.

    Retourne 'atteint' si le point de passage est validé, 'erreur' si la
    position est illisible, 'avance' sinon.
    """
    verifier_arret()
    type_position = TYPES_LECTURE['POSITION']
    x = mem.lire(OFFSETS_POSITION['X'], type_position)
    y = mem.lire(OFFSETS_POSITION['Y'], type_position)

    if x is None or y is None:
        log("[Déplacement] Lecture de la position impossible - vérifie OFFSETS_POSITION.")
        return 'erreur'

    dx = point[0] - x
    dy = point[1] - y
    distance = max(abs(dx), abs(dy))

    if distance <= WAYPOINT_TOLERANCE:
        log("[Déplacement] Point (%d, %d) atteint depuis (%d, %d)." % (point[0], point[1], x, y))
        return 'atteint'

    # On limite l'ordre de déplacement à MAX_STEP_TILES cases : un pas à la fois,
    # pour pouvoir re-vérifier les monstres et les PV entre chaque avancée.
    if distance > MAX_STEP_TILES:
        facteur = MAX_STEP_TILES / float(distance)
        pas_x = int(round(dx * facteur)) or _signe(dx)
        pas_y = int(round(dy * facteur)) or _signe(dy)
    else:
        pas_x, pas_y = dx, dy

    if MOVEMENT_MODE == 'KEYS':
        detail = _deplacer_par_touches(dx, dy)
    else:
        detail = _deplacer_par_clic(pas_x, pas_y)

    log("[Déplacement] Position (%d, %d) -> objectif (%d, %d) | distance %d cases | %s"
        % (x, y, point[0], point[1], distance, detail))

    pause(MOVE_DELAY)
    return 'avance'


def debloquer():
    """Petit mouvement latéral quand la position ne change plus."""
    log("[Déplacement] Personnage bloqué - tentative de déblocage.")
    if MOVEMENT_MODE == 'KEYS':
        _deplacer_par_touches(-MAX_STEP_TILES, 0)
    else:
        _deplacer_par_clic(-MAX_STEP_TILES, -MAX_STEP_TILES)
    pause(MOVE_DELAY)


# ---------------------------------------------------------------------------
# BOUCLE PRINCIPALE
# ---------------------------------------------------------------------------
def boucle_principale(mem):
    index_point = 0
    derniere_position = None
    compteur_blocage = 0

    log("[Init] Bot lancé. Chemin de %d point(s). Appuie sur ÉCHAP pour tout couper."
        % len(PATH))

    while True:
        verifier_arret()

        # --- ÉTAPE 1 : SÉCURITÉ POTIONS -----------------------------------
        if gerer_potions(mem) == 'mort':
            return

        # --- ÉTAPE 2 : CIBLAGE MONSTRE ------------------------------------
        pv_cible = cibler_monstre(mem)

        # --- ÉTAPE 3 : COMBAT PRIORITAIRE ---------------------------------
        if pv_cible is not None and pv_cible > 0:
            # ÉTAPE 4 : aucun ramassage, l'auto-loot du serveur s'en charge.
            # On repart directement au ciblage pour vider la zone.
            boucle_combat(mem)
            continue

        # --- ÉTAPE 5 : DÉPLACEMENT SUR LE CHEMIN --------------------------
        if index_point >= len(PATH):
            if not BOUCLER_CHEMIN:
                log("[Déplacement] Fin du chemin atteinte - arrêt du bot.")
                return
            index_point = 0
            log("[Déplacement] Chemin terminé - reprise au premier point.")

        point = PATH[index_point]
        log("[Recherche] Aucun monstre à proximité - direction le point %d/%d (%d, %d)."
            % (index_point + 1, len(PATH), point[0], point[1]))

        resultat = avancer_vers(mem, point)

        if resultat == 'atteint':
            index_point += 1
            compteur_blocage = 0
            continue

        if resultat == 'erreur':
            pause(1.0)
            continue

        # Détection de blocage : la position ne bouge plus entre deux ordres.
        position = (mem.lire(OFFSETS_POSITION['X'], TYPES_LECTURE['POSITION']),
                    mem.lire(OFFSETS_POSITION['Y'], TYPES_LECTURE['POSITION']))
        if position == derniere_position:
            compteur_blocage += 1
            if compteur_blocage >= BLOCAGE_MAX:
                debloquer()
                compteur_blocage = 0
        else:
            compteur_blocage = 0
        derniere_position = position


def main():
    print("=" * 68)
    print(" BOT DE FARM NOSTALE - lecture mémoire + envoi de touches")
    print(" Sécurité : touche ÉCHAP = arrêt immédiat")
    print("=" * 68)

    if not verifier_configuration():
        return 1

    mem = MemoireNostale(PROCESS_NAME, MODULE_NAME)
    try:
        mem.connecter()
    except pymem.exception.ProcessNotFound:
        log("[Erreur] Processus '%s' introuvable. Le jeu est-il lancé ?" % PROCESS_NAME)
        return 1
    except Exception as erreur:
        log("[Erreur] Impossible de s'attacher au processus : %s" % erreur)
        log("         Essaie de lancer la console en tant qu'administrateur.")
        return 1

    threading.Thread(target=_surveiller_echap, daemon=True).start()

    try:
        for restant in range(DELAI_DEMARRAGE, 0, -1):
            log("[Init] Démarrage dans %d s - mets la fenêtre NosTale au premier plan..." % restant)
            pause(1.0)
        boucle_principale(mem)
    except ArretDemande:
        log("[Sécurité] Touche ÉCHAP détectée - arrêt immédiat du bot.")
    except KeyboardInterrupt:
        log("[Sécurité] Interruption clavier (Ctrl+C) - arrêt du bot.")
    except Exception as erreur:
        log("[Erreur] Arrêt sur exception : %s" % erreur)
        return 1
    finally:
        _arret.set()
        log("[Init] Bot arrêté proprement.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
