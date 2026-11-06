# TravelMod for Nine Lives

**Version 1.0.0** · a mod for *Ninelives* 0.24.3 (offline)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

## What it does

- **Remembers where you were.** The original game always started you at the zone's spawn
  point. TravelMod saves your position whenever the game saves (about every 20 seconds) and
  puts you back there when you log in.
- **F9:** teleport to the next spawn point / binding stone in the zone. **Shift+F9** goes back.
- **F10:** teleport to named places (currently: Crimson Crest's binding stone).

The place you jumped to shows at the top of the screen for a few seconds.

## Requirements

- *Ninelives* 0.24.3, patched with the
  [Nine Lives offline server](https://github.com/THICC-debug/ninelives-offline-server)'s
  `Patch Game.bat`, which installs the mod loader.

## Install

Unzip into your game folder (the one with `Ninelives.exe`), so you get:

```
Ninelives\Mods\TravelMod.dll
Ninelives\Mods\lib\0Harmony.dll
```

To remove it, delete `Mods\TravelMod.dll`. Saved positions are in `Mods\TravelMod\positions.txt`.

## Known limits

- Spawn points don't have names in the game data, so F9 shows "spawn point" for most of them.
  Named places for F10 are added by hand (`Places` in `TravelMod.cs`).
- A position is only restored in the zone where it was saved.

## Build from source

```
dotnet build -c Release              # build and install into the game
dotnet build -c Release -t:Package   # make release\TravelMod-1.0.0.zip
```

License: MIT (code only; the game belongs to SmokymonkeyS).
