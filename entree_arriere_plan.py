#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
entree_arriere_plan.py
======================

Envoi de touches et de clics directement à la fenêtre d'un processus (messages
Windows PostMessage), sans qu'elle ait besoin d'être au premier plan.

La fenêtre est retrouvée à partir du nom du processus (ex. NostaleClientX.exe),
comme le fait le bot de farm pour la lecture mémoire. Aucune dépendance externe.

Limite : le jeu doit lire ses entrées via les messages de fenêtre
(WM_KEYDOWN / WM_LBUTTONDOWN). Un jeu qui interroge le clavier ou la souris
directement (GetAsyncKeyState, DirectInput, Raw Input) ignorera ces messages ;
dans ce cas, utilise le mode « premier plan ».

Les coordonnées de clic sont relatives à la zone cliente de la fenêtre du jeu
(son coin haut-gauche = 0, 0), donc indépendantes de sa position à l'écran.
"""

import os
import string
import sys
import time

WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
MK_LBUTTON = 0x0001

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
        u.GetWindow.argtypes = [wt.HWND, wt.UINT]
        u.GetWindow.restype = wt.HWND
        u.GetWindowTextLengthW.argtypes = [wt.HWND]
        u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
        u.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
        u.ScreenToClient.argtypes = [wt.HWND, ctypes.POINTER(wt.POINT)]
        k.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
        k.OpenProcess.restype = wt.HANDLE
        k.QueryFullProcessImageNameW.argtypes = [
            wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
        k.CloseHandle.argtypes = [wt.HANDLE]

    def post(self, hwnd, message, wparam, lparam):
        return bool(self.user32.PostMessageW(hwnd, message, wparam, lparam))

    def scan(self, vk):
        return self.user32.MapVirtualKeyW(vk, 0)   # MAPVK_VK_TO_VSC

    def fenetre_valide(self, hwnd):
        return bool(self.user32.IsWindow(hwnd))

    def ecran_vers_client(self, hwnd, x, y):
        point = self._wt.POINT(int(x), int(y))
        self.user32.ScreenToClient(hwnd, self._ctypes.byref(point))
        return point.x, point.y

    def _nom_processus(self, pid):
        ctypes, wt = self._ctypes, self._wt
        poignee = self.kernel32.OpenProcess(0x1000, False, pid)   # QUERY_LIMITED_INFORMATION
        if not poignee:
            return ""
        try:
            tampon = ctypes.create_unicode_buffer(1024)
            taille = wt.DWORD(1024)
            if self.kernel32.QueryFullProcessImageNameW(poignee, 0, tampon, ctypes.byref(taille)):
                return os.path.basename(tampon.value).lower()
            return ""
        finally:
            self.kernel32.CloseHandle(poignee)

    def trouver_fenetre(self, nom_processus):
        """Fenêtre principale visible du processus (la plus grande), ou None."""
        ctypes, wt, u = self._ctypes, self._wt, self.user32
        cible = os.path.basename(nom_processus).lower()
        trouvees, noms = [], {}

        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def rappel(hwnd, _):
            if not u.IsWindowVisible(hwnd) or u.GetWindow(hwnd, 4):   # GW_OWNER
                return True
            if u.GetWindowTextLengthW(hwnd) == 0:
                return True
            pid = wt.DWORD()
            u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value not in noms:
                noms[pid.value] = self._nom_processus(pid.value)
            if noms[pid.value] == cible:
                zone = wt.RECT()
                u.GetClientRect(hwnd, ctypes.byref(zone))
                trouvees.append((zone.right * zone.bottom, hwnd))
            return True

        u.EnumWindows(rappel, 0)
        return max(trouvees)[1] if trouvees else None


# ---------------------------------------------------------------------------
# ENTRÉE VERS UNE FENÊTRE
# ---------------------------------------------------------------------------
class EntreeFenetre:
    def __init__(self, nom_processus, api=None):
        self.nom_processus = nom_processus
        self.api = api or ApiWin32()
        self.hwnd = None

    def connecter(self):
        self.hwnd = self.api.trouver_fenetre(self.nom_processus)
        if not self.hwnd:
            raise RuntimeError("Fenêtre du processus '%s' introuvable. Le jeu est-il lancé "
                               "(et visible, pas réduit dans la barre des tâches) ?"
                               % self.nom_processus)
        return self.hwnd

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
        """Clic gauche en (x, y), relatif à la zone cliente de la fenêtre."""
        hwnd, position = self._fenetre(), _lparam_souris(x, y)
        self.api.post(hwnd, WM_MOUSEMOVE, 0, position)
        self.api.post(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, position)
        time.sleep(duree)
        self.api.post(hwnd, WM_LBUTTONUP, 0, position)
