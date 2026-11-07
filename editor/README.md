# Nine Lives Editor

> [!NOTE]
> Made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

One app for everything the mods let you change: `Nine Lives Editor.exe`. It runs on your own PC,
nothing goes online, and it needs nothing else installed (no Python).

| Tab | What you do there | Needs |
|---|---|---|
| **Map** | Every zone from above (taken by the game the first time you're there): NPCs, your things, script areas, you. Drag to move, click a spot to put something there | mod loader 2.3 |
| **Home** | "What do you want to do?": one click to each task, plus where you stand | |
| **Quests** | Make quests, or change the game's own (copy one of the 345 as a starting point) | [QuestLoader](../mods/QuestLoader) |
| **Items** | Prices and stats (damage, armor, health, stack size…), names and descriptions | [ContentLoader](../mods/ContentLoader) |
| **Monster loot** | What monsters drop and how often, with a "roll 10 kills" preview | ContentLoader |
| **Shops** | What each vendor sells | ContentLoader |
| **Dialogue** | Conversations with answers: each answer leads on, and can give items, start quests, set flags | [ScriptLoader](../mods/ScriptLoader) 1.1 |
| **Scripts** | Rules (when this happens, then do that) and Lua scripts, with a console that runs in the game | ScriptLoader |
| **World** | Copy houses, fences, trees and props and place them, or hide things | [WorldLoader](../mods/WorldLoader) |
| **NPCs** | Add people to the world: look, place, job, their quests and shop stock | [NPCLoader](../mods/NPCLoader) |
| **Packs & mods** | See which mods are installed, switch mods and packs on and off, start new packs | |

**Find anything:** the search box at the top (Ctrl+K) finds quests, items, monsters, shops and
NPCs by name and opens them. Type *gnoll* to get to what Gnolls drop, *knife* for the Knife item.
A dot next to a tab means it has unsaved changes.

The tabs work together: on an NPC, **New quest for …** opens the Quests tab with that NPC as the
giver, and **Edit the stock** opens its shop in the Shops tab.

## Open it

- **Installed with Setup.exe:** `Nine Lives Editor.exe` in the game folder, or the desktop shortcut.
- **From source:** `dotnet build -c Release editor/NineLivesEditor`, then run
  `editor/NineLivesEditor/bin/Release/net48/NineLivesEditor.exe`. It finds the game next to the
  repo, or pass `--game` with the game folder.

It opens in its own window (Microsoft Edge WebView2, part of Windows 10 and 11). Without WebView2
it opens in your web browser instead; `--browser` does that on purpose.

Play once with the mods installed (enter the world) before the first edit: the mods export the
game's quests, items, loot and NPCs for the editor. NPCs are listed as you visit their zones.

## Packs

Your changes are saved as packs:

| Kind | Folder |
|---|---|
| Quest packs | `Mods\Quests\<name>.json` |
| Content packs (items, loot, shops) | `Mods\Content\<name>.json` |
| NPC packs | `Mods\NPCs\<name>.json` |
| World packs (placed and hidden things) | `Mods\World\<name>.json` |
| Script packs (rules, Lua, dialogues) | `Mods\Scripts\<name>.json` |

A pack switched off on the **Packs & mods** tab is renamed to `<name>.json.disabled`, so the game
skips it. The examples that come with the mods start switched off. Each save keeps the previous
version as `<name>.json.bak`.

With the game running, NPC, World and Script packs reload in game by themselves when you save. For
quests and content use `reloadquests` and `reloadcontent` in the DevTools command box (F7), or restart
the game. A switched-off pack does nothing in game; the tab shows a bar with **Switch it on** when the
open pack is off.

## How it's built

| Folder | What's in it |
|---|---|
| `editor/web/` | The pages (HTML, CSS, JavaScript). Each tab is one page. |
| `editor/NineLivesEditor/` | The app (C#, .NET Framework 4.8): the window, and a small local server the pages use to read and save packs. The pages and WebView2 are built into the one exe. |

License: MIT (code only; the game belongs to SmokymonkeyS).
