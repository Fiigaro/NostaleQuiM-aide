@echo off
title Bot Farm NosTale
cd /d "%~dp0"

rem --- Droits administrateur, obligatoires pour lire la memoire du jeu ----
net session >nul 2>&1
if errorlevel 1 (
    echo Demande des droits administrateur...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
    if errorlevel 1 (
        echo.
        echo [Erreur] Elevation refusee. Fais un clic droit sur ce fichier
        echo          puis "Executer en tant qu'administrateur".
        echo.
        pause
    )
    exit /b
)
cd /d "%~dp0"

if not exist "%~dp0_trouver_python.bat" (
    echo [Erreur] Fichier _trouver_python.bat manquant a cote de ce script.
    pause
    exit /b 1
)
call "%~dp0_trouver_python.bat"

if not defined PYEXE (
    echo.
    echo ============================================================
    echo   Python n'est pas installe sur ce PC
    echo ============================================================
    echo.
    echo   1. Telecharge Python 3.12 sur https://www.python.org/downloads/
    echo   2. COCHE "Add python.exe to PATH" pendant l'installation.
    echo   3. Ferme cette fenetre, ouvre-en une nouvelle, relance ce script.
    echo.
    pause
    exit /b 1
)

"%PYEXE%" %PYARGS% -c "import pymem, pydirectinput" >nul 2>&1
if errorlevel 1 (
    echo Installation des dependances, patiente...
    "%PYEXE%" %PYARGS% -m pip install --upgrade pymem pydirectinput
    if errorlevel 1 (
        echo.
        echo [Erreur] Installation impossible. Verifie ta connexion Internet.
        echo.
        pause
        exit /b 1
    )
)

echo Lancement de l'interface...
"%PYEXE%" %PYARGS% bot_gui.py
if errorlevel 1 pause
