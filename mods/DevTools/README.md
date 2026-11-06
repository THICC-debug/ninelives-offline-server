# DevTools for Nine Lives

**Version 1.1.0** · a mod for *Ninelives* 0.24.3 (offline)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

An in-game command box for testing mods and quests. Press **F7**, type a command, press Enter.

## Commands

| Command | What it does |
|---|---|
| `give <item> [count]` | Put items in your bag, by name or id: `give heartberry 4`, `give 630000050` |
| `find <text>` | List items whose name contains the text: `find potion` |
| `gold <amount>` | Add gold (a negative number takes it away) |
| `exp <amount>` | Add experience |
| `pos` | Show your zone and position (also written to `Mods\ModLoader.log`) |
| `tp <x> <y> <z>` | Teleport to a position |
| `flag <n> [on/off]` | Show or set a progress flag. Mod quests use 9000–9999. |
| `clear` | Clear the box |
| `help` | List the commands, including ones other mods add |

Other mods can add their own commands (the mod API's `Commands.Register`), and they run here too. For example, [QuestLoader](../QuestLoader) adds `reloadquests`.

Up and Down recall earlier commands. Esc (or F7) closes the box. Your character doesn't move
while the box is open.

## Requirements

- *Ninelives* 0.24.3 patched with the
  [Nine Lives offline server](https://github.com/THICC-debug/ninelives-offline-server)'s
  `Patch Game.bat` (mod loader 2.1 or newer).

## Install

Unzip into your game folder (the one with `Ninelives.exe`), so you get `Ninelives\Mods\DevTools.dll`.
The key can be changed in `Mods\config\devtools.cfg`.

## Build from source

```
dotnet build -c Release              # build and install into the game
dotnet build -c Release -t:Package   # make release\DevTools-1.1.0.zip
```

License: MIT (code only; the game belongs to SmokymonkeyS).
