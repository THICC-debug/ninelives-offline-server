NINE LIVES OFFLINE
==================

Play Nine Lives 0.24.3 without the official servers (they're gone), with mod support.
Made with AI (Anthropic's Claude) and tested by a human player. A fan project, not
affiliated with SmokymonkeyS. You need your own copy of the game.

INSTALL
  1. Unzip this whole folder anywhere (don't run Setup.exe from inside the zip).
  2. Run Setup.exe. It finds the game, or click Browse and pick the folder with Ninelives.exe.
  3. Tick the mods you want and click Install.
  4. Play with "Play Nine Lives Offline.bat" in the game folder, or the desktop shortcut.

Nothing else to install: Setup brings its own copy of Python for the server. It works on
Windows 10 and 11.

WHAT SETUP CHANGES
  - Two game files (Ninelives_Data\Managed\Assembly-UnityScript.dll and
    Assembly-CSharp-firstpass.dll) are patched to talk to the server on your own PC.
    The originals are kept in Ninelives_Data\OfflineBackup.
  - The offline server goes in <game>\OfflineServer. Your save is OfflineServer\save.json.
  - The mod loader and your mods go in <game>\Mods.

UPDATE OR CHANGE MODS
  Run Setup.exe again, tick or untick mods, click Update. Your save and packs are kept.

UNINSTALL
  Run Setup.exe and click Uninstall. The original game files go back. You choose whether
  to keep your save and mod packs.

EDITORS
  QuestLoader and ContentLoader come with editors that open in your browser:
  <game>\Mods\Open Quest Editor.bat and <game>\Mods\Open Content Editor.bat
  Play the game once with the mod installed first (it exports the game's data for the editor).

HELP, SOURCE, MAKING MODS
  https://github.com/THICC-debug/ninelives-offline-server

The code is MIT licensed. Python is from python.org (PSF license, see files\python\LICENSE.txt).
The game belongs to SmokymonkeyS; none of its files are included.
