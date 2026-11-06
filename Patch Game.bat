@echo off
rem Patches Nine Lives to use the offline server and installs the mod loader. Run it once.
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
  pause
  exit /b
)

echo Building the mod loader...
dotnet build -c Release -v q modloader\NineLivesModLoader -p:GameDir="%GAME_DIR%" || goto :fail
dotnet run -c Release --project patcher -- "%GAME_DIR%" --loader modloader\NineLivesModLoader\bin\Release\net35\NineLivesModLoader.dll || goto :fail
pause
exit /b

:fail
echo Something went wrong - see the messages above.
pause
