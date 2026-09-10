@echo off
setlocal
title Compilation du Bot Farm NosTale
cd /d "%~dp0"
set "LOG=%~dp0build_log.txt"

echo ============================================================
echo   Compilation de BotFarmNostale.exe
echo ============================================================
echo.
echo [1/4] Recherche d'un Python utilisable...

if not exist "%~dp0_trouver_python.bat" (
    echo [Erreur] Fichier _trouver_python.bat manquant a cote de ce script.
    pause
    exit /b 1
)
call "%~dp0_trouver_python.bat"

if not defined PYEXE goto :pas_de_python

set "INFOS="
"%PYEXE%" %PYARGS% -c "import sys,struct;print(sys.version.split()[0]+' '+str(struct.calcsize('P')*8)+' bits')" > "%TEMP%\infos_python_bot.txt" 2>nul
if exist "%TEMP%\infos_python_bot.txt" set /p INFOS=<"%TEMP%\infos_python_bot.txt"
del "%TEMP%\infos_python_bot.txt" >nul 2>&1
echo       Trouve : %PYEXE% %PYARGS%  ^(Python %INFOS%^)
echo.

echo [2/4] Verification de pip...
"%PYEXE%" %PYARGS% -m pip --version >nul 2>&1
if errorlevel 1 (
    echo       pip absent, installation...
    "%PYEXE%" %PYARGS% -m ensurepip --upgrade >nul 2>&1
    "%PYEXE%" %PYARGS% -m pip --version >nul 2>&1
    if errorlevel 1 (
        echo.
        echo [Erreur] pip est introuvable et n'a pas pu etre installe.
        echo          Reinstalle Python depuis https://www.python.org/downloads/
        echo.
        pause
        exit /b 1
    )
)
echo       OK.
echo.

echo [3/4] Installation des dependances ^(patiente, environ une minute^)...
echo       Journal detaille : build_log.txt
"%PYEXE%" %PYARGS% -m pip install --upgrade pyinstaller pymem pydirectinput > "%LOG%" 2>&1
if errorlevel 1 (
    echo       Echec, nouvelle tentative en mode utilisateur...
    "%PYEXE%" %PYARGS% -m pip install --user --upgrade pyinstaller pymem pydirectinput >> "%LOG%" 2>&1
    if errorlevel 1 goto :echec_pip
)

"%PYEXE%" %PYARGS% -c "import pymem, pydirectinput, PyInstaller" >> "%LOG%" 2>&1
if errorlevel 1 goto :echec_pip
echo       OK.
echo.

echo [4/4] Compilation ^(cela peut prendre une minute^)...
"%PYEXE%" %PYARGS% -m PyInstaller --noconfirm --onefile --windowed --uac-admin ^
    --name BotFarmNostale ^
    --collect-all pymem ^
    --collect-all pydirectinput ^
    bot_gui.py >> "%LOG%" 2>&1
if errorlevel 1 (
    echo.
    echo [Erreur] La compilation a echoue. Dernieres lignes du journal :
    echo ------------------------------------------------------------
    powershell -NoProfile -Command "Get-Content -Tail 25 '%LOG%'" 2>nul
    echo ------------------------------------------------------------
    echo          Le journal complet est dans build_log.txt
    echo.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   Termine : dist\BotFarmNostale.exe
echo ============================================================
echo.
echo   L'executable demande les droits administrateur au
echo   lancement, necessaires pour lire la memoire du jeu.
echo.
echo   Ton antivirus peut le mettre en quarantaine : un programme
echo   qui lit la memoire d'un autre processus et envoie des
echo   touches a la meme signature qu'un logiciel malveillant.
echo   Ajoute une exclusion si besoin.
echo.
pause
exit /b 0


:pas_de_python

set "INFOS="
"%PYEXE%" %PYARGS% -c "import sys,struct;print(sys.version.split()[0]+' '+str(struct.calcsize('P')*8)+' bits')" > "%TEMP%\infos_python_bot.txt" 2>nul
if exist "%TEMP%\infos_python_bot.txt" set /p INFOS=<"%TEMP%\infos_python_bot.txt"
del "%TEMP%\infos_python_bot.txt" >nul 2>&1
echo       Trouve : %PYEXE% %PYARGS%  ^(Python %INFOS%^)
echo.

echo [2/4] Verification de pip...
"%PYEXE%" %PYARGS% -m pip --version >nul 2>&1
if errorlevel 1 (
    echo       pip absent, installation...
    "%PYEXE%" %PYARGS% -m ensurepip --upgrade >nul 2>&1
    "%PYEXE%" %PYARGS% -m pip --version >nul 2>&1
    if errorlevel 1 (
        echo.
        echo [Erreur] pip est introuvable et n'a pas pu etre installe.
        echo          Reinstalle Python depuis https://www.python.org/downloads/
        echo.
        pause
        exit /b 1
    )
)
echo       OK.
echo.

echo [3/4] Installation des dependances ^(patiente, environ une minute^)...
echo       Journal detaille : build_log.txt
"%PYEXE%" %PYARGS% -m pip install --upgrade pyinstaller pymem pydirectinput > "%LOG%" 2>&1
if errorlevel 1 (
    echo       Echec, nouvelle tentative en mode utilisateur...
    "%PYEXE%" %PYARGS% -m pip install --user --upgrade pyinstaller pymem pydirectinput >> "%LOG%" 2>&1
    if errorlevel 1 goto :echec_pip
)

"%PYEXE%" %PYARGS% -c "import pymem, pydirectinput, PyInstaller" >> "%LOG%" 2>&1
if errorlevel 1 goto :echec_pip
echo       OK.
echo.

echo [4/4] Compilation ^(cela peut prendre une minute^)...
"%PYEXE%" %PYARGS% -m PyInstaller --noconfirm --onefile --windowed --uac-admin ^
    --name BotFarmNostale ^
    --collect-all pymem ^
    --collect-all pydirectinput ^
    bot_gui.py >> "%LOG%" 2>&1
if errorlevel 1 (
    echo.
    echo [Erreur] La compilation a echoue. Dernieres lignes du journal :
    echo ------------------------------------------------------------
    powershell -NoProfile -Command "Get-Content -Tail 25 '%LOG%'" 2>nul
    echo ------------------------------------------------------------
    echo          Le journal complet est dans build_log.txt
    echo.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   Termine : dist\BotFarmNostale.exe
echo ============================================================
echo.
echo   L'executable demande les droits administrateur au
echo   lancement, necessaires pour lire la memoire du jeu.
echo.
echo   Ton antivirus peut le mettre en quarantaine : un programme
echo   qui lit la memoire d'un autre processus et envoie des
echo   touches a la meme signature qu'un logiciel malveillant.
echo   Ajoute une exclusion si besoin.
echo.
pause
exit /b 0


:tester
rem Valide un candidat Python : seul un vrai interpreteur repond 81.
if defined PYEXE goto :eof
set "CAND=%~1"
set "CARG=%~2"
set "REPONSE="
"%CAND%" %CARG% -c "print(9*9)" > "%TEMP%\test_python_bot.txt" 2>nul
if exist "%TEMP%\test_python_bot.txt" set /p REPONSE=<"%TEMP%\test_python_bot.txt"
del "%TEMP%\test_python_bot.txt" >nul 2>&1
if "%REPONSE%"=="81" (
    set "PYEXE=%CAND%"
    set "PYARGS=%CARG%"
)
goto :eof


:pas_de_python
echo.
echo ============================================================
echo   Python n'est pas installe sur ce PC
echo ============================================================
echo.
echo   Le "python.exe" present dans le PATH est le faux raccourci
echo   du Microsoft Store : il ne fait qu'afficher un message.
echo.
echo   A FAIRE :
echo.
echo   1. Telecharge Python 3.12 sur :
echo        https://www.python.org/downloads/
echo.
echo   2. Pendant l'installation, COCHE la case
echo        "Add python.exe to PATH"
echo      en bas de la premiere fenetre. C'est l'etape a ne pas
echo      rater.
echo.
echo   3. Ferme cette fenetre, ouvre-en une nouvelle, et relance
echo      build.bat.
echo.
echo   Si le probleme persiste, desactive les faux raccourcis :
echo     Parametres ^> Applications ^> Parametres avances des
echo     applications ^> Alias d'execution d'application
echo     puis desactive python.exe et python3.exe
echo.
pause
exit /b 1


:echec_pip
echo.
echo [Erreur] L'installation des dependances a echoue.
echo          Dernieres lignes du journal :
echo ------------------------------------------------------------
powershell -NoProfile -Command "Get-Content -Tail 20 '%LOG%'" 2>nul
echo ------------------------------------------------------------
echo          Causes frequentes : pas de connexion Internet, ou un
echo          antivirus qui bloque pip. Le journal complet est dans
echo          build_log.txt
echo.
pause
exit /b 1
