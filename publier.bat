@echo off
chcp 65001 >nul
title Compilation de NosSmoothCustomClient.exe
cd /d "%~dp0"

echo ============================================================
echo   Compilation de NosSmoothCustomClient.exe
echo ============================================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [Erreur] Le SDK .NET 8 est introuvable.
    echo          Installe-le depuis https://dotnet.microsoft.com/download/dotnet/8.0
    echo          ^(colonne "SDK", Windows x64^), puis relance ce script.
    echo.
    pause
    exit /b 1
)

echo Compilation en cours ^(la premiere fois, cela peut prendre quelques minutes^)...
echo.
dotnet publish NosSmoothCustomClient.Gui -c Release -r win-x86 --self-contained true ^
    -p:PublishSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 (
    echo.
    echo [Erreur] La compilation a echoue.
    pause
    exit /b 1
)

move /y "dist\NosSmoothCustomClient.Gui.exe" "dist\NosSmoothCustomClient.exe" >nul

echo.
echo ============================================================
echo   Termine : dist\NosSmoothCustomClient.exe
echo ============================================================
echo.
echo   Double-clique dessus : Windows demande les droits
echo   administrateur, puis une fenetre te laisse choisir ton
echo   NosTale et le mode. Plus aucune commande a taper.
echo.
echo   Garde appsettings.json a cote de l'exe : ce sont tes
echo   reglages de depart.
echo.
echo   Ton antivirus peut le mettre en quarantaine : un programme
echo   qui lit le trafic du jeu et envoie des touches ressemble a
echo   un logiciel malveillant. Ajoute une exclusion si besoin.
echo.
explorer dist
pause
