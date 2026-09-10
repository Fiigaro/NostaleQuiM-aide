#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bot_gui.py
==========

Interface graphique du bot de farm NosTale : tous les réglages (offsets,
seuils, touches, chemin) sont modifiables dans des cases, puis un bouton
« Lancer » démarre le bot.

Lancement :
    python bot_gui.py

Les réglages sont enregistrés dans 'config_bot.json', à côté du programme,
et rechargés automatiquement au démarrage suivant.

Sécurité : la touche ÉCHAP coupe le bot instantanément, même si la fenêtre
du jeu est au premier plan. Le bouton « Stop » fait la même chose.
"""

import copy
import queue
import threading
import tkinter as tk
from tkinter import messagebox, scrolledtext, ttk

from bot_farm_nostale import (
    CONFIG_DEFAUT,
    BotFarm,
    charger_config,
    est_candidat_jeu,
    lister_processus,
    sauver_config,
    valider_config,
)


# ---------------------------------------------------------------------------
# CONVERSIONS CHAMP DE SAISIE <-> CONFIGURATION
# ---------------------------------------------------------------------------
def parser_offset(texte):
    """'0x4B21C0' -> int ; '0x4B21C0, 0x1C, 0x8' -> chaîne de pointeurs."""
    texte = (texte or "").strip()
    if not texte:
        return 0
    morceaux = [m.strip() for m in texte.split(",") if m.strip()]
    valeurs = []
    for morceau in morceaux:
        try:
            valeurs.append(int(morceau, 16))
        except ValueError:
            raise ValueError("'%s' n'est pas un offset hexadécimal valide." % morceau)
    return valeurs[0] if len(valeurs) == 1 else valeurs


def formater_offset(valeur):
    if isinstance(valeur, (list, tuple)):
        return ", ".join("0x%X" % v for v in valeur)
    return "0x%X" % (valeur or 0)


def parser_chemin(texte):
    """Une ligne = un point : '50, 50'. Lignes vides et # ignorés."""
    points = []
    for numero, ligne in enumerate((texte or "").splitlines(), start=1):
        ligne = ligne.strip()
        if not ligne or ligne.startswith("#"):
            continue
        morceaux = [m for m in ligne.replace(";", ",").replace(" ", ",").split(",") if m]
        if len(morceaux) != 2:
            raise ValueError("Ligne %d ('%s') : attendu deux nombres, ex. 50, 50." % (numero, ligne))
        try:
            points.append([int(morceaux[0]), int(morceaux[1])])
        except ValueError:
            raise ValueError("Ligne %d ('%s') : coordonnées non numériques." % (numero, ligne))
    return points


def formater_chemin(points):
    return "\n".join("%d, %d" % (point[0], point[1]) for point in points)


def parser_touches(texte):
    return [t.strip() for t in (texte or "").split(",") if t.strip()]


def parser_nombre(texte, libelle, entier=False, mini=None, maxi=None):
    texte = (texte or "").strip().replace(",", ".")
    try:
        valeur = int(float(texte)) if entier else float(texte)
    except ValueError:
        raise ValueError("%s : '%s' n'est pas un nombre valide." % (libelle, texte))
    if mini is not None and valeur < mini:
        raise ValueError("%s : la valeur doit être ≥ %s." % (libelle, mini))
    if maxi is not None and valeur > maxi:
        raise ValueError("%s : la valeur doit être ≤ %s." % (libelle, maxi))
    return valeur


# ---------------------------------------------------------------------------
# SÉLECTION DU PROCESSUS DU JEU
# ---------------------------------------------------------------------------
class SelecteurProcessus(tk.Toplevel):
    """Liste les processus en cours pour éviter d'avoir à deviner le nom du .exe."""

    def __init__(self, parent):
        super().__init__(parent)
        self.title("Choisir le processus du jeu")
        self.geometry("460x500")
        self.minsize(400, 380)
        self.transient(parent)
        self.resultat = None
        self.processus = []

        ttk.Label(self, text="Lance le jeu, puis choisis son processus dans la liste.\n"
                             "Les candidats probables sont marqués d'une étoile et "
                             "affichés en premier.",
                  justify="left", wraplength=420).pack(anchor="w", padx=10, pady=(10, 6))

        recherche = ttk.Frame(self)
        recherche.pack(fill="x", padx=10)
        ttk.Label(recherche, text="Filtrer :").pack(side="left")
        self.filtre = tk.StringVar()
        self.filtre.trace_add("write", lambda *_: self._remplir())
        ttk.Entry(recherche, textvariable=self.filtre).pack(side="left", fill="x",
                                                            expand=True, padx=6)
        ttk.Button(recherche, text="Actualiser", command=self.actualiser).pack(side="left")

        colonnes = ("nom", "pid")
        self.liste = ttk.Treeview(self, columns=colonnes, show="headings", height=16)
        self.liste.heading("nom", text="Processus")
        self.liste.heading("pid", text="PID")
        self.liste.column("nom", width=320, anchor="w")
        self.liste.column("pid", width=80, anchor="center")
        self.liste.pack(fill="both", expand=True, padx=10, pady=8)
        self.liste.bind("<Double-1>", lambda _: self._choisir())
        self.liste.tag_configure("candidat", foreground="#0A7D28")

        boutons = ttk.Frame(self)
        boutons.pack(fill="x", padx=10, pady=(0, 10))
        ttk.Button(boutons, text="Annuler", command=self.destroy).pack(side="right")
        ttk.Button(boutons, text="Choisir", command=self._choisir).pack(side="right", padx=6)

        self.actualiser()
        self.grab_set()

    def actualiser(self):
        self.processus = lister_processus()
        self._remplir()

    def _remplir(self):
        self.liste.delete(*self.liste.get_children())

        if not self.processus:
            self.liste.insert("", "end", values=("Aucun processus listé "
                                                 "(Windows requis)", ""))
            return

        filtre = self.filtre.get().strip().lower()
        visibles = [p for p in self.processus if filtre in p[0].lower()]
        # Les candidats plausibles d'abord, le reste ensuite.
        visibles.sort(key=lambda p: (not est_candidat_jeu(p[0]), p[0].lower()))

        for nom, pid in visibles:
            candidat = est_candidat_jeu(nom)
            self.liste.insert("", "end",
                              values=(("★  " if candidat else "     ") + nom, pid),
                              tags=("candidat",) if candidat else ())

    def _choisir(self):
        selection = self.liste.selection()
        if not selection:
            return
        valeur = self.liste.item(selection[0], "values")[0]
        nom = valeur.replace("★", "").strip()
        if nom.endswith(".exe") or nom:
            self.resultat = nom
        self.destroy()


# ---------------------------------------------------------------------------
# FENÊTRE PRINCIPALE
# ---------------------------------------------------------------------------
class InterfaceBot(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Bot Farm NosTale")
        self.geometry("940x800")
        self.minsize(860, 660)

        self.vars = {}
        self.file_logs = queue.Queue()
        self.arret = None
        self.thread = None

        self._construire()
        self.appliquer_config(charger_config())
        self.after(100, self._vider_file_logs)
        self.protocol("WM_DELETE_WINDOW", self._fermer)

        self.journal("[Init] Interface prête. Renseigne tes offsets puis clique sur « Lancer ».")
        self.journal("[Init] Rappel : lance ce programme en tant qu'administrateur.")

    # --- Construction des widgets ----------------------------------------
    def _champ(self, parent, ligne, colonne, libelle, cle, largeur=20):
        ttk.Label(parent, text=libelle).grid(
            row=ligne, column=colonne * 2, sticky="w", padx=(8, 4), pady=3)
        var = tk.StringVar()
        self.vars[cle] = var
        entree = ttk.Entry(parent, textvariable=var, width=largeur)
        entree.grid(row=ligne, column=colonne * 2 + 1, sticky="w", padx=(0, 8), pady=3)
        return entree

    def _liste(self, parent, ligne, colonne, libelle, cle, valeurs, largeur=12):
        ttk.Label(parent, text=libelle).grid(
            row=ligne, column=colonne * 2, sticky="w", padx=(8, 4), pady=3)
        var = tk.StringVar()
        self.vars[cle] = var
        combo = ttk.Combobox(parent, textvariable=var, values=valeurs,
                             width=largeur, state="readonly")
        combo.grid(row=ligne, column=colonne * 2 + 1, sticky="w", padx=(0, 8), pady=3)
        return combo

    def _construire(self):
        onglets = ttk.Notebook(self)
        onglets.pack(fill="both", expand=True, padx=10, pady=(10, 4))

        self._onglet_offsets(onglets)
        self._onglet_combat(onglets)
        self._onglet_chemin(onglets)

        # --- Barre de boutons --------------------------------------------
        barre = ttk.Frame(self)
        barre.pack(fill="x", padx=10, pady=4)

        ttk.Button(barre, text="Sauvegarder", command=self.sauvegarder).pack(side="left")
        ttk.Button(barre, text="Recharger", command=self.recharger).pack(side="left", padx=4)
        ttk.Button(barre, text="Valeurs par défaut", command=self.reinitialiser).pack(side="left")

        self.bouton_stop = ttk.Button(barre, text="■  Stop", command=self.arreter, state="disabled")
        self.bouton_stop.pack(side="right")
        self.bouton_lancer = ttk.Button(barre, text="▶  Lancer", command=self.lancer)
        self.bouton_lancer.pack(side="right", padx=6)

        self.etat = ttk.Label(barre, text="● Arrêté", foreground="#999999")
        self.etat.pack(side="right", padx=10)

        ttk.Label(self, text="Sécurité : appuie sur ÉCHAP à tout moment pour couper le bot.",
                  foreground="#B00020").pack(anchor="w", padx=12, pady=(2, 0))

        # --- Journal ------------------------------------------------------
        cadre_log = ttk.LabelFrame(self, text="Journal")
        cadre_log.pack(fill="both", expand=True, padx=10, pady=(4, 10))
        self.log = scrolledtext.ScrolledText(cadre_log, height=13, state="disabled",
                                             wrap="word", font=("Consolas", 9))
        self.log.pack(fill="both", expand=True, padx=6, pady=6)

    def _onglet_offsets(self, onglets):
        cadre = ttk.Frame(onglets)
        onglets.add(cadre, text="  Processus & Offsets  ")

        haut = ttk.Frame(cadre)
        haut.pack(fill="x", pady=(10, 4))
        self._champ(haut, 0, 0, "Nom du processus :", "PROCESS_NAME", largeur=26)
        ttk.Button(haut, text="Détecter...", command=self.detecter_processus).grid(
            row=0, column=2, sticky="w", padx=(0, 12))
        self._champ(haut, 0, 2, "Module (optionnel) :", "MODULE_NAME", largeur=22)

        aide = ("Offsets en hexadécimal. Offset simple : 0x004B21C0   |   "
                "Chaîne de pointeurs : 0x004B21C0, 0x1C, 0x8")
        ttk.Label(cadre, text=aide, foreground="#555555").pack(anchor="w", padx=8, pady=(4, 8))

        groupes = ttk.Frame(cadre)
        groupes.pack(fill="x")

        position = ttk.LabelFrame(groupes, text="Position du personnage")
        position.grid(row=0, column=0, sticky="nsew", padx=(8, 4), pady=4)
        self._champ(position, 0, 0, "X :", "OFF_POS_X")
        self._champ(position, 1, 0, "Y :", "OFF_POS_Y")
        self._liste(position, 2, 0, "Type :", "TYPE_POSITION",
                    ["int", "uint", "short", "ushort", "byte"])

        joueur = ttk.LabelFrame(groupes, text="Statistiques du personnage")
        joueur.grid(row=0, column=1, sticky="nsew", padx=4, pady=4)
        self._champ(joueur, 0, 0, "PV :", "OFF_PV")
        self._champ(joueur, 1, 0, "PV max :", "OFF_PV_MAX")
        self._champ(joueur, 2, 0, "PM :", "OFF_PM")
        self._champ(joueur, 3, 0, "PM max :", "OFF_PM_MAX")
        self._liste(joueur, 4, 0, "Type :", "TYPE_PLAYER",
                    ["int", "uint", "short", "ushort", "byte"])

        cible = ttk.LabelFrame(groupes, text="Statistiques de la cible")
        cible.grid(row=0, column=2, sticky="nsew", padx=(4, 8), pady=4)
        self._champ(cible, 0, 0, "PV :", "OFF_CIBLE_PV")
        self._champ(cible, 1, 0, "PV max :", "OFF_CIBLE_PV_MAX")
        self._liste(cible, 2, 0, "Type :", "TYPE_TARGET",
                    ["int", "uint", "short", "ushort", "byte"])

        for colonne in range(3):
            groupes.columnconfigure(colonne, weight=1)

        test = ttk.Frame(cadre)
        test.pack(fill="x", pady=10)
        ttk.Button(test, text="Tester la lecture mémoire",
                   command=self.tester_lecture).pack(side="left", padx=8)
        ttk.Label(test, text="Affiche dans le journal les valeurs lues maintenant : "
                             "pratique pour vérifier tes offsets sans lancer le farm.",
                  foreground="#555555", wraplength=620, justify="left").pack(side="left", padx=6)

    def _onglet_combat(self, onglets):
        cadre = ttk.Frame(onglets)
        onglets.add(cadre, text="  Combat & Potions  ")

        potions = ttk.LabelFrame(cadre, text="Potions")
        potions.pack(fill="x", padx=8, pady=(10, 4))
        self._champ(potions, 0, 0, "Seuil PV (%) :", "SEUIL_PV", largeur=8)
        self._champ(potions, 0, 1, "Touche potion Vie :", "KEY_HP_POTION", largeur=8)
        self._champ(potions, 1, 0, "Seuil PM (%) :", "SEUIL_PM", largeur=8)
        self._champ(potions, 1, 1, "Touche potion Mana :", "KEY_MP_POTION", largeur=8)
        self._champ(potions, 2, 0, "Anti-spam potion (s) :", "POTION_COOLDOWN", largeur=8)

        combat = ttk.LabelFrame(cadre, text="Ciblage et combat")
        combat.pack(fill="x", padx=8, pady=4)
        self._champ(combat, 0, 0, "Touche de ciblage :", "KEY_TARGET", largeur=10)
        self._champ(combat, 0, 1, "Touches d'attaque :", "ATTACK_KEYS", largeur=24)
        self._champ(combat, 1, 0, "Délai entre coups (s) :", "ATTACK_DELAY", largeur=10)
        self._champ(combat, 1, 1, "Délai après ciblage (s) :", "TARGET_DELAY", largeur=10)
        self._champ(combat, 2, 0, "Timeout combat (s) :", "COMBAT_TIMEOUT", largeur=10)

        ttk.Label(cadre, foreground="#555555",
                  text=("Touches d'attaque séparées par des virgules, jouées en boucle. "
                        "'space' = barre espace.\n"
                        "Aucun ramassage n'est généré : l'auto-loot du serveur s'en charge.")
                  ).pack(anchor="w", padx=12, pady=8)

    def _onglet_chemin(self, onglets):
        cadre = ttk.Frame(onglets)
        onglets.add(cadre, text="  Chemin & Déplacement  ")

        gauche = ttk.LabelFrame(cadre, text="Points de passage (un par ligne : X, Y)")
        gauche.pack(side="left", fill="both", expand=True, padx=(8, 4), pady=10)
        self.champ_chemin = scrolledtext.ScrolledText(gauche, width=22, height=14,
                                                      wrap="none", font=("Consolas", 10))
        self.champ_chemin.pack(fill="both", expand=True, padx=6, pady=6)

        droite = ttk.Frame(cadre)
        droite.pack(side="left", fill="both", expand=True, padx=(4, 8), pady=10)

        route = ttk.LabelFrame(droite, text="Parcours")
        route.pack(fill="x")
        self._champ(route, 0, 0, "Tolérance (cases) :", "WAYPOINT_TOLERANCE", largeur=8)
        self.vars["BOUCLER_CHEMIN"] = tk.BooleanVar(value=True)
        ttk.Checkbutton(route, text="Boucler le chemin en fin de parcours",
                        variable=self.vars["BOUCLER_CHEMIN"]).grid(
            row=1, column=0, columnspan=2, sticky="w", padx=8, pady=4)

        deplacement = ttk.LabelFrame(droite, text="Déplacement")
        deplacement.pack(fill="x", pady=8)
        self._liste(deplacement, 0, 0, "Mode :", "MOVEMENT_MODE", ["CLICK", "KEYS"], largeur=8)
        self._champ(deplacement, 1, 0, "Cases max par pas :", "MAX_STEP_TILES", largeur=8)
        self._champ(deplacement, 2, 0, "Délai après pas (s) :", "MOVE_DELAY", largeur=8)
        self._champ(deplacement, 3, 0, "Durée appui flèche (s) :", "MOVE_KEY_DURATION", largeur=8)

        ecran = ttk.LabelFrame(droite, text="Calibrage écran (mode CLICK)")
        ecran.pack(fill="x")
        self._champ(ecran, 0, 0, "Perso à l'écran X :", "ECRAN_X", largeur=8)
        self._champ(ecran, 1, 0, "Perso à l'écran Y :", "ECRAN_Y", largeur=8)
        self._champ(ecran, 2, 0, "Largeur case (px) :", "CASE_L", largeur=8)
        self._champ(ecran, 3, 0, "Hauteur case (px) :", "CASE_H", largeur=8)

        divers = ttk.LabelFrame(droite, text="Divers")
        divers.pack(fill="x", pady=8)
        self._champ(divers, 0, 0, "Compte à rebours (s) :", "DELAI_DEMARRAGE", largeur=8)
        self._champ(divers, 1, 0, "Seuil anti-blocage :", "BLOCAGE_MAX", largeur=8)

    # --- Configuration <-> widgets ---------------------------------------
    def appliquer_config(self, config):
        """Remplit tous les champs depuis une configuration."""
        valeurs = {
            "PROCESS_NAME": config["PROCESS_NAME"],
            "MODULE_NAME": config["MODULE_NAME"],
            "OFF_POS_X": formater_offset(config["OFFSETS_POSITION"]["X"]),
            "OFF_POS_Y": formater_offset(config["OFFSETS_POSITION"]["Y"]),
            "OFF_PV": formater_offset(config["OFFSETS_PLAYER_STATS"]["HP"]),
            "OFF_PV_MAX": formater_offset(config["OFFSETS_PLAYER_STATS"]["MAX_HP"]),
            "OFF_PM": formater_offset(config["OFFSETS_PLAYER_STATS"]["MP"]),
            "OFF_PM_MAX": formater_offset(config["OFFSETS_PLAYER_STATS"]["MAX_MP"]),
            "OFF_CIBLE_PV": formater_offset(config["OFFSETS_TARGET_STATS"]["HP"]),
            "OFF_CIBLE_PV_MAX": formater_offset(config["OFFSETS_TARGET_STATS"]["MAX_HP"]),
            "TYPE_POSITION": config["TYPES_LECTURE"]["POSITION"],
            "TYPE_PLAYER": config["TYPES_LECTURE"]["PLAYER"],
            "TYPE_TARGET": config["TYPES_LECTURE"]["TARGET"],
            "SEUIL_PV": "%g" % (config["HP_POTION_THRESHOLD"] * 100),
            "SEUIL_PM": "%g" % (config["MP_POTION_THRESHOLD"] * 100),
            "KEY_HP_POTION": config["KEY_HP_POTION"],
            "KEY_MP_POTION": config["KEY_MP_POTION"],
            "POTION_COOLDOWN": "%g" % config["POTION_COOLDOWN"],
            "KEY_TARGET": config["KEY_TARGET"],
            "ATTACK_KEYS": ", ".join(config["ATTACK_KEYS"]),
            "ATTACK_DELAY": "%g" % config["ATTACK_DELAY"],
            "TARGET_DELAY": "%g" % config["TARGET_DELAY"],
            "COMBAT_TIMEOUT": "%g" % config["COMBAT_TIMEOUT"],
            "WAYPOINT_TOLERANCE": "%d" % config["WAYPOINT_TOLERANCE"],
            "MOVEMENT_MODE": config["MOVEMENT_MODE"],
            "MAX_STEP_TILES": "%d" % config["MAX_STEP_TILES"],
            "MOVE_DELAY": "%g" % config["MOVE_DELAY"],
            "MOVE_KEY_DURATION": "%g" % config["MOVE_KEY_DURATION"],
            "ECRAN_X": "%d" % config["CHARACTER_SCREEN_POS"][0],
            "ECRAN_Y": "%d" % config["CHARACTER_SCREEN_POS"][1],
            "CASE_L": "%d" % config["TILE_SIZE_PX"][0],
            "CASE_H": "%d" % config["TILE_SIZE_PX"][1],
            "DELAI_DEMARRAGE": "%d" % config["DELAI_DEMARRAGE"],
            "BLOCAGE_MAX": "%d" % config["BLOCAGE_MAX"],
        }
        for cle, valeur in valeurs.items():
            self.vars[cle].set(valeur)

        self.vars["BOUCLER_CHEMIN"].set(bool(config["BOUCLER_CHEMIN"]))
        self.champ_chemin.delete("1.0", "end")
        self.champ_chemin.insert("1.0", formater_chemin(config["PATH"]))

    def collecter_config(self):
        """Lit tous les champs. Retourne (config, liste d'erreurs)."""
        erreurs = []
        config = copy.deepcopy(CONFIG_DEFAUT)

        def offset(cle, libelle):
            try:
                return parser_offset(self.vars[cle].get())
            except ValueError as erreur:
                erreurs.append("%s : %s" % (libelle, erreur))
                return 0

        def nombre(cle, libelle, entier=False, mini=None, maxi=None):
            try:
                return parser_nombre(self.vars[cle].get(), libelle, entier, mini, maxi)
            except ValueError as erreur:
                erreurs.append(str(erreur))
                return 0

        config["PROCESS_NAME"] = self.vars["PROCESS_NAME"].get().strip()
        config["MODULE_NAME"] = self.vars["MODULE_NAME"].get().strip()

        if not config["PROCESS_NAME"]:
            erreurs.append("Le nom du processus est vide.")

        config["OFFSETS_POSITION"] = {
            "X": offset("OFF_POS_X", "Offset Position X"),
            "Y": offset("OFF_POS_Y", "Offset Position Y"),
        }
        config["OFFSETS_PLAYER_STATS"] = {
            "HP": offset("OFF_PV", "Offset PV"),
            "MAX_HP": offset("OFF_PV_MAX", "Offset PV max"),
            "MP": offset("OFF_PM", "Offset PM"),
            "MAX_MP": offset("OFF_PM_MAX", "Offset PM max"),
        }
        config["OFFSETS_TARGET_STATS"] = {
            "HP": offset("OFF_CIBLE_PV", "Offset PV cible"),
            "MAX_HP": offset("OFF_CIBLE_PV_MAX", "Offset PV max cible"),
        }
        config["TYPES_LECTURE"] = {
            "POSITION": self.vars["TYPE_POSITION"].get(),
            "PLAYER": self.vars["TYPE_PLAYER"].get(),
            "TARGET": self.vars["TYPE_TARGET"].get(),
        }

        config["HP_POTION_THRESHOLD"] = nombre("SEUIL_PV", "Seuil PV", mini=0, maxi=100) / 100.0
        config["MP_POTION_THRESHOLD"] = nombre("SEUIL_PM", "Seuil PM", mini=0, maxi=100) / 100.0
        config["KEY_HP_POTION"] = self.vars["KEY_HP_POTION"].get().strip()
        config["KEY_MP_POTION"] = self.vars["KEY_MP_POTION"].get().strip()
        config["POTION_COOLDOWN"] = nombre("POTION_COOLDOWN", "Anti-spam potion", mini=0)

        config["KEY_TARGET"] = self.vars["KEY_TARGET"].get().strip()
        config["ATTACK_KEYS"] = parser_touches(self.vars["ATTACK_KEYS"].get())
        if not config["ATTACK_KEYS"]:
            erreurs.append("Aucune touche d'attaque renseignée.")
        config["ATTACK_DELAY"] = nombre("ATTACK_DELAY", "Délai entre coups", mini=0.01)
        config["TARGET_DELAY"] = nombre("TARGET_DELAY", "Délai après ciblage", mini=0.01)
        config["COMBAT_TIMEOUT"] = nombre("COMBAT_TIMEOUT", "Timeout combat", mini=1)

        try:
            config["PATH"] = parser_chemin(self.champ_chemin.get("1.0", "end"))
            if not config["PATH"]:
                erreurs.append("Le chemin est vide : ajoute au moins un point.")
        except ValueError as erreur:
            erreurs.append("Chemin - %s" % erreur)

        config["WAYPOINT_TOLERANCE"] = nombre("WAYPOINT_TOLERANCE", "Tolérance", entier=True, mini=0)
        config["BOUCLER_CHEMIN"] = bool(self.vars["BOUCLER_CHEMIN"].get())
        config["MOVEMENT_MODE"] = self.vars["MOVEMENT_MODE"].get()
        config["MAX_STEP_TILES"] = nombre("MAX_STEP_TILES", "Cases max par pas", entier=True, mini=1)
        config["MOVE_DELAY"] = nombre("MOVE_DELAY", "Délai après pas", mini=0)
        config["MOVE_KEY_DURATION"] = nombre("MOVE_KEY_DURATION", "Durée appui flèche", mini=0.01)
        config["CHARACTER_SCREEN_POS"] = [nombre("ECRAN_X", "Perso écran X", entier=True, mini=0),
                                          nombre("ECRAN_Y", "Perso écran Y", entier=True, mini=0)]
        config["TILE_SIZE_PX"] = [nombre("CASE_L", "Largeur case", entier=True, mini=1),
                                  nombre("CASE_H", "Hauteur case", entier=True, mini=1)]
        config["DELAI_DEMARRAGE"] = nombre("DELAI_DEMARRAGE", "Compte à rebours", entier=True, mini=0)
        config["BLOCAGE_MAX"] = nombre("BLOCAGE_MAX", "Seuil anti-blocage", entier=True, mini=1)

        return config, erreurs

    # --- Journal ----------------------------------------------------------
    def journal(self, message):
        self.file_logs.put(message)

    def _vider_file_logs(self):
        """Transfère les messages du bot (autre thread) vers la zone de texte."""
        try:
            while True:
                message = self.file_logs.get_nowait()
                self.log.configure(state="normal")
                self.log.insert("end", message + "\n")
                self.log.see("end")
                self.log.configure(state="disabled")
        except queue.Empty:
            pass
        self.after(100, self._vider_file_logs)

    # --- Actions ----------------------------------------------------------
    def sauvegarder(self):
        config, erreurs = self.collecter_config()
        if erreurs:
            messagebox.showerror("Réglages invalides", "\n".join("• " + e for e in erreurs))
            return
        chemin = sauver_config(config)
        self.journal("[Config] Réglages enregistrés dans %s" % chemin)

    def recharger(self):
        self.appliquer_config(charger_config())
        self.journal("[Config] Réglages rechargés depuis le fichier.")

    def reinitialiser(self):
        if messagebox.askyesno("Valeurs par défaut",
                               "Remettre tous les réglages à leurs valeurs par défaut ?"):
            self.appliquer_config(copy.deepcopy(CONFIG_DEFAUT))
            self.journal("[Config] Valeurs par défaut restaurées (non enregistrées).")

    def detecter_processus(self):
        """Ouvre la liste des processus en cours et reprend celui choisi."""
        selecteur = SelecteurProcessus(self)
        self.wait_window(selecteur)
        if selecteur.resultat:
            self.vars["PROCESS_NAME"].set(selecteur.resultat)
            self.journal("[Config] Processus sélectionné : %s" % selecteur.resultat)

    def tester_lecture(self):
        """Lit une fois toutes les valeurs suivies et les affiche dans le journal."""
        config, erreurs = self.collecter_config()
        if erreurs:
            messagebox.showerror("Réglages invalides", "\n".join("• " + e for e in erreurs))
            return

        def travail():
            bot = BotFarm(config, journal=self.journal, arret=threading.Event())
            try:
                bot.connecter()
            except Exception as erreur:
                self.journal("[Test] Connexion impossible : %s" % erreur)
                self.journal("[Test] Le jeu est-il lancé, et ce programme en administrateur ?")
                return
            etat = bot.lire_etat()
            self.journal("[Test] Position : X=%s  Y=%s" % (etat["X"], etat["Y"]))
            self.journal("[Test] Personnage : PV=%s/%s  PM=%s/%s"
                         % (etat["PV"], etat["PV_MAX"], etat["PM"], etat["PM_MAX"]))
            self.journal("[Test] Cible : PV=%s/%s" % (etat["CIBLE_PV"], etat["CIBLE_PV_MAX"]))
            if any(valeur is None for valeur in etat.values()):
                self.journal("[Test] Les valeurs à 'None' correspondent à des offsets invalides.")
            else:
                self.journal("[Test] Toutes les lectures ont abouti. "
                             "Compare ces valeurs avec ton écran de jeu.")

        threading.Thread(target=travail, daemon=True).start()

    def lancer(self):
        if self.thread and self.thread.is_alive():
            return

        config, erreurs = self.collecter_config()
        if erreurs:
            messagebox.showerror("Réglages invalides", "\n".join("• " + e for e in erreurs))
            return

        problemes = valider_config(config)
        if problemes:
            messagebox.showerror("Impossible de lancer le bot",
                                 "\n".join("• " + p for p in problemes))
            return

        sauver_config(config)
        self.arret = threading.Event()
        bot = BotFarm(config, journal=self.journal, arret=self.arret)

        def travail():
            try:
                bot.executer()
            finally:
                self.after(0, self._fin_execution)

        self.thread = threading.Thread(target=travail, daemon=True)
        self.thread.start()

        self.bouton_lancer.configure(state="disabled")
        self.bouton_stop.configure(state="normal")
        self.etat.configure(text="● En cours", foreground="#0A7D28")

    def arreter(self):
        if self.arret:
            self.arret.set()
            self.journal("[Sécurité] Arrêt demandé depuis l'interface.")

    def _fin_execution(self):
        self.bouton_lancer.configure(state="normal")
        self.bouton_stop.configure(state="disabled")
        self.etat.configure(text="● Arrêté", foreground="#999999")

    def _fermer(self):
        if self.thread and self.thread.is_alive():
            if not messagebox.askyesno("Quitter", "Le bot tourne encore. Quitter quand même ?"):
                return
            self.arret.set()
        self.destroy()


def main():
    InterfaceBot().mainloop()
    return 0


if __name__ == "__main__":
    main()
