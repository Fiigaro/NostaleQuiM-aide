@echo off
rem Cherche un vrai interpreteur Python et renseigne PYEXE / PYARGS.
rem Le faux python.exe du Microsoft Store est ecarte : seul un vrai
rem interpreteur repond 81 a la question posee.

set "PYEXE="
set "PYARGS="

call :tester "py" "-3"
call :tester "python" ""
call :tester "python3" ""
for /d %%D in ("%LOCALAPPDATA%\Programs\Python\Python3*") do call :tester "%%D\python.exe" ""
for /d %%D in ("%ProgramFiles%\Python3*") do call :tester "%%D\python.exe" ""
for /d %%D in ("%ProgramFiles(x86)%\Python3*") do call :tester "%%D\python.exe" ""
for /d %%D in ("C:\Python3*") do call :tester "%%D\python.exe" ""
goto :eof

:tester
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
