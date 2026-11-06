# Nine Lives Editor

> [!NOTE]
> Made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

One page in your browser for everything the mods let you change. It runs on your own PC, nothing
goes online.

| Tab | What you do there | Needs |
|---|---|---|
| **Overview** | See which mods are installed, switch mods and packs on and off, start new packs | |
| **Quests** | Make quests, or change the game's own (copy one of the 345 as a starting point) | [QuestLoader](../mods/QuestLoader) |
| **Items** | Prices and stats (damage, armor, health, stack size…), names and descriptions | [ContentLoader](../mods/ContentLoader) |
| **Loot** | What monsters drop and how often, with a "roll 10 kills" preview | ContentLoader |
| **Shops** | What each vendor sells | ContentLoader |
| **NPCs** | Add people to the world: look, place, job, their quests and shop stock | [NPCLoader](../mods/NPCLoader) |

The tabs work together: on an NPC, **New quest for …** opens the Quests tab with that NPC as the
giver, and **Edit the stock** opens its shop in the Shops tab.

## Open it

- **Installed with Setup.exe:** double-click `Open Nine Lives Editor.bat` in the game folder.
- **From source:** `python editor/ninelives_editor.py` (Python 3.8+, standard library only). It finds
  the game next to the repo, or pass `--game "path\to\Ninelives"`.

It opens `http://127.0.0.1:8090` in your browser. Keep its window open while you edit.

Play once with the mods installed (enter the world) before the first edit: the mods export the
game's quests, items, loot and NPCs for the editor. NPCs are listed as you visit their zones.

## Packs

Your changes are saved as packs:

| Kind | Folder |
|---|---|
| Quest packs | `Mods\Quests\<name>.json` |
| Content packs (items, loot, shops) | `Mods\Content\<name>.json` |
| NPC packs | `Mods\NPCs\<name>.json` |

A pack switched off on the Overview tab is renamed to `<name>.json.disabled`, so the game skips it.
The examples that come with the mods start switched off. Each save keeps the previous version as
`<name>.json.bak`.

Load changes in game without restarting: `reloadquests`, `reloadcontent` and `npcreload` in the
DevTools command box (F7). Otherwise restart the game.

License: MIT (code only; the game belongs to SmokymonkeyS).
