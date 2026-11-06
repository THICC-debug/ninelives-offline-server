@echo off
rem Starts the offline server in its own window, then the game.
rem If your game is somewhere else, change GAME_DIR to the folder that holds Ninelives.exe.
set "GAME_DIR=%~dp0..\Ninelives"

cd /d "%~dp0"
if not exist "%GAME_DIR%\Ninelives.exe" (
  echo Can't find Ninelives.exe in "%GAME_DIR%".
  echo Open this file in Notepad and set GAME_DIR to your game folder.
  pause
  exit /b
)

tasklist /FI "IMAGENAME eq Ninelives.exe" | find /I "Ninelives.exe" >nul
if not errorlevel 1 (
  echo Nine Lives is already running. Close the game first, then run this again.
  pause
  exit /b
)

rem The server window stays open so you can see it working (or any error).
start "Nine Lives offline server" cmd /k python ninelives_server.py
timeout /t 2 /nobreak >nul
start "" /d "%GAME_DIR%" "%GAME_DIR%\Ninelives.exe"
