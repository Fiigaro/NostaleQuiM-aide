@echo off
chcp 65001 >nul
title Compilation du Bot Farm NosTale
cd /d "%~dp0"

echo ============================================================
echo   Compilation de BotFarmNostale.exe
echo ============================================================
echo.

where python >nul 2>&1
if errorlevel 1 (
    echo [Erreur] Python est introuvable dans le PATH.
    echo          Installe Python depuis https://www.python.org/downloads/
    echo          en cochant "Add Python to PATH", puis relance ce script.
    echo.
    pause
    exit /b 1
)

echo [1/2] Installation des dependances...
python -m pip install --upgrade pip >nul
python -m pip install --upgrade pyinstaller pymem pydirectinput
if errorlevel 1 (
    echo.
    echo [Erreur] L'installation des dependances a echoue.
    pause
    exit /b 1
)

echo.
echo [2/2] Compilation en cours ^(cela peut prendre une minute^)...
python -m PyInstaller --noconfirm --onefile --windowed --uac-admin ^
    --name BotFarmNostale ^
    --collect-all pymem ^
    --collect-all pydirectinput ^
    bot_gui.py
if errorlevel 1 (
    echo.
    echo [Erreur] La compilation a echoue.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   Termine : dist\BotFarmNostale.exe
echo ============================================================
echo.
echo   L'executable demande automatiquement les droits
echo   administrateur au lancement ^(necessaire pour lire la
echo   memoire du jeu^).
echo.
echo   Ton antivirus peut le mettre en quarantaine : un programme
echo   qui lit la memoire d'un autre processus et envoie des
echo   touches a la meme signature qu'un logiciel malveillant.
echo   Ajoute une exclusion si besoin.
echo.
pause
