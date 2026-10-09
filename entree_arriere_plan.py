#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
entree_arriere_plan.py
======================

Envoi de touches et de clics directement à la fenêtre du jeu (messages Windows
PostMessage), sans qu'elle ait besoin d'être au premier plan.

Détection du jeu : mêmes règles que le bot NosSmooth (NosSmoothCustomClient) :

  * Le processus se trouve par son nom (.exe) si on en donne un ; sinon, ou si
    ce nom ne correspond à rien, par son dossier : un .exe hors du dossier
    Windows qui a un dossier « NostaleData » à côté de lui est un client NosTale.
    Le nom d'un .exe varie d'un serveur privé à l'autre, le dossier non.
  * La fenêtre visée est celle de classe « TNosTaleMainF » (formulaire Delphi du
    jeu), pas la « fenêtre principale » de Windows, qui peut être une coque qui
    ignore le clavier.

Limites : le jeu doit lire ses entrées via les messages de fenêtre
(WM_KEYDOWN / WM_LBUTTONDOWN). Un jeu qui interroge le clavier ou la souris
directement les ignorera. Pour la souris, le mode « curseur réel » déplace le
vrai curseur sur le point puis clique : il marche même si le jeu ignore les
messages de souris, mais la fenêtre du jeu doit alors être visible à cet endroit.

Les coordonnées de clic sont relatives à la zone cliente de la fenêtre du jeu
(son coin haut-gauche = 0, 0), donc indépendantes de sa position à l'écran.
"""

import ntpath
import os
import string
import sys
import time
from collections import namedtuple

WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
MK_LBUTTON = 0x0001

CLASSE_FENETRE_JEU = "TNosTaleMainF"
DOSSIER_DONNEES = "NostaleData"

_VK = {
    "enter": 0x0D, "return": 0x0D, "space": 0x20, "esc": 0x1B, "escape": 0x1B,
    "tab": 0x09, "backspace": 0x08, "shift": 0x10, "ctrl": 0x11, "alt": 0x12,
    "left": 0x25, "up": 0x26, "right": 0x27, "down": 0x28,
    "delete": 0x2E, "insert": 0x2D, "home": 0x24, "end": 0x23,
    "pageup": 0x21, "pagedown": 0x22,
}
_ETENDUES = {0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E}


def code_touche(nom):
    """Nom de touche (style pydirectinput) -> code virtuel Windows."""
    nom = (nom or "").strip().lower()
    if nom in _VK:
        return _VK[nom]
    if len(nom) == 1 and nom in string.ascii_lowercase + string.digits:
        return ord(nom.upper())
    if nom.startswith("f") and nom[1:].isdigit() and 1 <= int(nom[1:]) <= 12:
        return 0x6F + int(nom[1:])
    if nom.startswith("num") and len(nom) == 4 and nom[3].isdigit():
        return 0x60 + int(nom[3])
    raise ValueError("Touche inconnue : '%s'" % nom)


def _lparam_touche(scan, vk, relache, repetition=False):
    valeur = 1 | (scan << 16)
    if vk in _ETENDUES:
        valeur |= 1 << 24
    if relache:
        valeur |= (1 << 30) | (1 << 31)
    elif repetition:
        valeur |= 1 << 30
    return valeur


def _lparam_souris(x, y):
    return (int(x) & 0xFFFF) | ((int(y) & 0xFFFF) << 16)


# ---------------------------------------------------------------------------
# DÉTECTION DU JEU (logique pure, testable hors Windows)
# ---------------------------------------------------------------------------
# Une fenêtre de haut niveau et le processus qui la possède.
FenetreInfo = namedtuple("FenetreInfo", "hwnd pid classe titre visible surface chemin")
Selection = namedtuple("Selection", "fenetre pids methode repli")


def est_chemin_client(chemin, dossier_windows="", existe=os.path.isdir):
    """Un .exe est un client NosTale s'il a un dossier NostaleData à côté de lui.

    Le chemin doit être absolu (sinon on testerait le dossier courant, et tout
    processus finirait par correspondre) et hors du dossier Windows (un jeu n'y
    vit jamais : cette règle empêche de viser un processus système par erreur).
    """
    if not chemin or not ntpath.isabs(chemin):
        return False
    dossier = ntpath.dirname(chemin)
    if dossier_windows and dossier.lower().startswith(dossier_windows.lower()):
        return False
    return bool(existe(ntpath.join(dossier, DOSSIER_DONNEES)))


def _rang_fenetre(fenetre):
    """Plus petit = meilleur. None = fenêtre inutilisable pour le jeu."""
    if fenetre.classe == CLASSE_FENETRE_JEU:
        return (0, -fenetre.surface)
    if "nostale" in fenetre.classe.lower():
        return (1, -fenetre.surface)
    if fenetre.visible and fenetre.titre:
        return (2, -fenetre.surface)
    return None


def selectionner(fenetres, nom_processus="", pid=0, dossier_windows="", existe=os.path.isdir):
    """Choisit la fenêtre du jeu parmi toutes les fenêtres de haut niveau.

    Ordre : PID imposé ; sinon nom du .exe ; sinon (ou si ce nom ne correspond à
    rien) dossier NostaleData. `repli` est vrai quand un nom avait été donné
    mais que seule la détection par dossier a trouvé le jeu.
    """
    nom = ntpath.basename(nom_processus or "").lower()
    candidates, methode, repli = [], "", False

    if pid:
        candidates, methode = [f for f in fenetres if f.pid == pid], "pid"
    else:
        if nom:
            candidates = [f for f in fenetres if ntpath.basename(f.chemin).lower() == nom]
            methode = "nom"
        if not candidates:
            repli = bool(nom)
            candidates = [f for f in fenetres if est_chemin_client(f.chemin, dossier_windows, existe)]
            methode = "dossier"

    utilisables = [f for f in candidates if _rang_fenetre(f) is not None]
    if not utilisables:
        return Selection(None, sorted({f.pid for f in candidates}), methode, repli)
    choisie = min(utilisables, key=lambda f: (_rang_fenetre(f), f.pid))
    return Selection(choisie, sorted({f.pid for f in utilisables}), methode, repli)


# ---------------------------------------------------------------------------
# API WIN32 (remplaçable dans les tests)
# ---------------------------------------------------------------------------
class ApiWin32:
    def __init__(self):
        if sys.platform != "win32":
            raise RuntimeError("Le mode arrière-plan nécessite Windows.")
        import ctypes
        import ctypes.wintypes as wt
        self._ctypes, self._wt = ctypes, wt
        u = self.user32 = ctypes.windll.user32
        k = self.kernel32 = ctypes.windll.kernel32

        u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
        u.PostMessageW.restype = wt.BOOL
        u.MapVirtualKeyW.argtypes = [wt.UINT, wt.UINT]
        u.MapVirtualKeyW.restype = wt.UINT
        u.IsWindow.argtypes = [wt.HWND]
        u.IsWindowVisible.argtypes = [wt.HWND]
        u.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
        u.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
        u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
        u.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
        u.ScreenToClient.argtypes = [wt.HWND, ctypes.POINTER(wt.POINT)]
        u.ClientToScreen.argtypes = [wt.HWND, ctypes.POINTER(wt.POINT)]
        u.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
        u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
        u.mouse_event.argtypes = [wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
        k.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
        k.OpenProcess.restype = wt.HANDLE
        k.QueryFullProcessImageNameW.argtypes = [
            wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
        k.CloseHandle.argtypes = [wt.HANDLE]

        self.dossier_windows = os.environ.get("WINDIR", "C:\\Windows")
        self.existe = os.path.isdir

    # --- messages -----------------------------------------------------------
    def post(self, hwnd, message, wparam, lparam):
        return bool(self.user32.PostMessageW(hwnd, message, wparam, lparam))

    def scan(self, vk):
        return self.user32.MapVirtualKeyW(vk, 0)   # MAPVK_VK_TO_VSC

    def fenetre_valide(self, hwnd):
        return bool(self.user32.IsWindow(hwnd))

    # --- coordonnées et curseur ------------------------------------------------
    def ecran_vers_client(self, hwnd, x, y):
        point = self._wt.POINT(int(x), int(y))
        self.user32.ScreenToClient(hwnd, self._ctypes.byref(point))
        return point.x, point.y

    def client_vers_ecran(self, hwnd, x, y):
        point = self._wt.POINT(int(x), int(y))
        self.user32.ClientToScreen(hwnd, self._ctypes.byref(point))
        return point.x, point.y

    def curseur(self):
        point = self._wt.POINT()
        if not self.user32.GetCursorPos(self._ctypes.byref(point)):
            return None
        return point.x, point.y

    def deplacer_curseur(self, x, y):
        return bool(self.user32.SetCursorPos(int(x), int(y)))

    def bouton_gauche(self, enfonce):
        self.user32.mouse_event(0x0002 if enfonce else 0x0004, 0, 0, 0, 0)

    # --- recensement des fenêtres -------------------------------------------------
    def _chemin_processus(self, pid):
        ctypes, wt = self._ctypes, self._wt
        poignee = self.kernel32.OpenProcess(0x1000, False, pid)   # QUERY_LIMITED_INFORMATION
        if not poignee:
            return ""
        try:
            tampon = ctypes.create_unicode_buffer(1024)
            taille = wt.DWORD(1024)
            if self.kernel32.QueryFullProcessImageNameW(poignee, 0, tampon, ctypes.byref(taille)):
                return tampon.value
            return ""
        finally:
            self.kernel32.CloseHandle(poignee)

    def lister_fenetres(self):
        """Toutes les fenêtres de haut niveau dont le processus est lisible."""
        ctypes, wt, u = self._ctypes, self._wt, self.user32
        fenetres, chemins = [], {}

        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def rappel(hwnd, _):
            pid = wt.DWORD()
            u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value not in chemins:
                chemins[pid.value] = self._chemin_processus(pid.value)
            if not chemins[pid.value]:
                return True
            classe = ctypes.create_unicode_buffer(256)
            titre = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(hwnd, classe, 256)
            u.GetWindowTextW(hwnd, titre, 256)
            zone = wt.RECT()
            u.GetClientRect(hwnd, ctypes.byref(zone))
            fenetres.append(FenetreInfo(
                hwnd, pid.value, classe.value, titre.value, bool(u.IsWindowVisible(hwnd)),
                zone.right * zone.bottom, chemins[pid.value]))
            return True

        u.EnumWindows(rappel, 0)
        return fenetres


# ---------------------------------------------------------------------------
# ENTRÉE VERS UNE FENÊTRE
# ---------------------------------------------------------------------------
class EntreeFenetre:
    def __init__(self, nom_processus="", api=None, pid=0):
        self.nom_processus = (nom_processus or "").strip()
        self.pid = int(pid or 0)
        self.api = api or ApiWin32()
        self.hwnd = None
        self.rapport = []   # Lignes décrivant ce qui a été trouvé, et ce qui mérite attention

    def connecter(self):
        api = self.api
        selection = selectionner(api.lister_fenetres(), self.nom_processus, self.pid,
                                 api.dossier_windows, api.existe)
        fenetre = selection.fenetre
        if fenetre is None:
            raise RuntimeError(self._message_introuvable(selection))

        self.hwnd = fenetre.hwnd
        self.rapport = [
            "Jeu trouvé : %s (pid %d), fenêtre classe \"%s\"%s - trouvé par %s." % (
                ntpath.basename(fenetre.chemin), fenetre.pid, fenetre.classe,
                " \"%s\"" % fenetre.titre if fenetre.titre else "",
                {"pid": "le PID", "nom": "le nom du processus",
                 "dossier": "le dossier NostaleData"}[selection.methode])]
        if selection.repli:
            self.rapport.append(
                "Le nom '%s' ne correspond à aucun processus ; le client a été trouvé grâce à son "
                "dossier NostaleData. Mets '%s' dans le champ Processus."
                % (self.nom_processus, ntpath.basename(fenetre.chemin)))
        if len(selection.pids) > 1:
            self.rapport.append(
                "%d clients trouvés (pids %s) : le pid %d est utilisé. Renseigne le PID pour en "
                "choisir un autre." % (len(selection.pids), ", ".join(map(str, selection.pids)),
                                       fenetre.pid))
        if fenetre.classe != CLASSE_FENETRE_JEU:
            self.rapport.append(
                "Ce n'est pas la classe attendue \"%s\" : si rien ne bouge dans le jeu, "
                "c'est la première piste." % CLASSE_FENETRE_JEU)
        return self.hwnd

    def _message_introuvable(self, selection):
        if self.pid:
            return ("Aucune fenêtre utilisable pour le PID %d. Le jeu est-il lancé, et ce PID "
                    "est-il le bon (Gestionnaire des tâches, onglet Détails) ?" % self.pid)
        if selection.pids:
            return ("Le processus du jeu (pid %s) est trouvé mais n'a aucune fenêtre utilisable. "
                    "Est-il réduit dans la barre des tâches ?" % ", ".join(map(str, selection.pids)))
        cherche = ("le processus '%s', puis " % self.nom_processus) if self.nom_processus else ""
        return ("Aucun client NosTale trouvé : le bot cherche %sun .exe qui a un dossier "
                "« %s » à côté de lui. Le jeu est-il lancé (et pas dans le dossier Windows) ? "
                "Sinon renseigne le PID du jeu." % (cherche, DOSSIER_DONNEES))

    def _fenetre(self):
        """Fenêtre courante ; la retrouve si le jeu a été relancé entre-temps."""
        if not self.hwnd or not self.api.fenetre_valide(self.hwnd):
            self.connecter()
        return self.hwnd

    def ecran_vers_client(self, x, y):
        return self.api.ecran_vers_client(self._fenetre(), x, y)

    def key_down(self, touche, repetition=False):
        vk = code_touche(touche)
        self.api.post(self._fenetre(), WM_KEYDOWN, vk,
                      _lparam_touche(self.api.scan(vk), vk, False, repetition))

    def key_up(self, touche):
        vk = code_touche(touche)
        self.api.post(self._fenetre(), WM_KEYUP, vk,
                      _lparam_touche(self.api.scan(vk), vk, True))

    def press(self, touche, duree=0.05):
        self.key_down(touche)
        time.sleep(duree)
        self.key_up(touche)

    def click(self, x, y, duree=0.05):
        """Clic gauche en (x, y), relatif à la zone cliente, par messages de fenêtre."""
        hwnd, position = self._fenetre(), _lparam_souris(x, y)
        self.api.post(hwnd, WM_MOUSEMOVE, 0, position)
        self.api.post(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, position)
        time.sleep(duree)
        self.api.post(hwnd, WM_LBUTTONUP, 0, position)

    def click_reel(self, x, y, duree=0.05):
        """Clic avec le vrai curseur, remis ensuite où il était.

        Pour un jeu qui ignore les messages de souris. Le curseur saute un
        instant et la fenêtre du jeu doit être visible au point visé.
        """
        api, hwnd = self.api, self._fenetre()
        ecran = api.client_vers_ecran(hwnd, x, y)
        precedent = api.curseur()
        api.deplacer_curseur(*ecran)
        api.bouton_gauche(True)
        time.sleep(duree)
        api.bouton_gauche(False)
        if precedent:
            api.deplacer_curseur(*precedent)
