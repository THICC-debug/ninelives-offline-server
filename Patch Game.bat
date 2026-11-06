@echo off
rem Patches Nine Lives to use the offline server. Run it once.
rem Needs the .NET 8 SDK: https://dotnet.microsoft.com/download
rem To undo, run:  "Patch Game.bat" restore
set "GAME_DIR=%~dp0..\Ninelives"

cd /d "%~dp0"
if not exist "%GAME_DIR%\Ninelives.exe" (
  echo Can't find Ninelives.exe in "%GAME_DIR%".
  echo Open this file in Notepad and set GAME_DIR to your game folder.
  pause
  exit /b
)

if /I "%~1"=="restore" (
  dotnet run -c Release --project patcher -- "%GAME_DIR%" --restore
) else (
  dotnet run -c Release --project patcher -- "%GAME_DIR%"
)
pause
