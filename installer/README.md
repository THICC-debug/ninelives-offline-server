# Nine Lives Offline installer

> [!NOTE]
> Made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

`Setup.exe` installs everything a player needs into their game folder and can take it all out
again. It runs on .NET Framework 4.8, which ships with Windows 10 and 11, so players install
nothing first.

## Build the release

```
python installer/build_release.py [--game "path\to\Ninelives"]
```

You need the .NET 8 SDK and a copy of the game (the loader and mods compile against its code;
nothing is written into it). The script:

1. builds the mod loader, and each mod's own release zip (`dotnet build -t:Package -p:NoInstall=true`),
2. downloads the official Python embeddable package from python.org (cached in `installer/.cache`,
   SHA-256 pinned in `python-embed.sha256`) and adds the server folder to its `._pth` file,
3. builds `Setup.exe` (Mono.Cecil is embedded in it),
4. refuses to package anything that looks like a game file or a save,
5. writes `installer/release/NineLivesOffline-<version>.zip`.

The mods in the release, their descriptions and which are ticked by default are the `MODS`
list at the top of `build_release.py`. Each mod stays its own zip, the same one released on
its own, so they can still be shipped separately.

## What's in the release zip

```
NineLivesOffline-1.0.0\
├── Setup.exe
├── README.txt                 for players
└── files\
    ├── loader\                NineLivesModLoader.dll, 0Harmony.dll
    ├── server\                ninelives_server.py, aes.py, recipes.json, docs
    ├── python\                Python 3.13 embeddable (32-bit, runs everywhere)
    ├── mods\                  <Mod>-<version>.zip for each mod
    └── mods.txt               id | version | zip | default | description | editor | editor script
```

## What Setup does to the game folder

| Install | Uninstall |
|---|---|
| Backs up and patches the two game DLLs (same code as `patcher/`, in `patcher/PatchCore.cs`) | Puts the originals back, removes the loader DLL |
| `OfflineServer\`: server, its Python, `version.txt`. Never touches `save.json` | Removes it; keeps `save*.json` unless the player picks "delete" |
| Unzips the ticked mods into `Mods\`, removes the DLLs of unticked ones | Removes the mods it installed and Harmony; keeps packs, settings and hand-added mods unless "delete" |
| `Play Nine Lives Offline.bat`, `Mods\Open <Editor>.bat`, desktop shortcut | Removes them |

On an update, example packs (`*.json` in `Mods\Quests`, `Mods\Content`, `Mods\NPCs`) are not
put back, so a player who deleted one doesn't get it again. Setup stops the offline server
and editors started from that game folder before it replaces their Python. It refuses to run
while the game is open.

## Command line

The window is for players. The same work runs headless, which is how it's tested:

```
Setup.exe --status    [--game DIR]
Setup.exe --install   [--game DIR] [--mods default|all|none|Id,Id] [--no-shortcut]
Setup.exe --uninstall [--game DIR] [--delete-data]
```

`--mods default` keeps the installed mods when updating, else uses the release defaults.

## Play launcher

`Play Nine Lives Offline.bat` starts the server minimized with `--exit-with-game` (the server
stops a few seconds after `Ninelives.exe` closes, or after 3 minutes if the game never starts),
then the game. If the server is already running, the new one exits and the game uses the old one.
