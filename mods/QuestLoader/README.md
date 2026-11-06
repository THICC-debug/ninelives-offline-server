# QuestLoader for Nine Lives

**Version 1.2.0** · a mod for *Ninelives* 0.24.3 (offline) · edit quests in the [Nine Lives Editor](../../editor)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

## What it does

- **Loads custom quests** from `Mods\Quests\*.json` (quest packs). A pack can add new quests or
  replace original ones by using the same quest id.
- **Exports the game's own data** for modders, from your copy of the game:
  - `Mods\QuestLoader\export\quests.json`: all 345 original quests, in the same format as packs
  - `Mods\QuestLoader\export\items.json`: every item id and name
  - `Mods\QuestLoader\export\npcs.json`: NPC ids, names and positions, added as you visit zones
- **Editor:** make quest packs in the [Nine Lives Editor](../../editor)'s **Quests** tab (it comes with Setup.exe).

## Requirements

- *Ninelives* 0.24.3 patched with the
  [Nine Lives offline server](https://github.com/THICC-debug/ninelives-offline-server)'s
  Setup.exe (or `Patch Game.bat` from source), mod loader 2.1 or newer.

## Install

Tick QuestLoader in Setup.exe, or unzip the release into your game folder (the one with `Ninelives.exe`):

```
Ninelives\Mods\QuestLoader.dll
Ninelives\Mods\Quests\example-pack.json.disabled     ← a sample quest, switched off
```

Switch the example on in the Nine Lives Editor (**Overview**), or rename it to `example-pack.json`.
Start the game and log in once: QuestLoader exports the quests and items when you enter the world.

## Making quests

Open the Nine Lives Editor (`Open Nine Lives Editor.bat` in the game folder) and go to **Quests**.

1. **Original quests**: browse all 345 quests. **Copy as a new quest** to use one as a starting point,
   or **Replace this quest in my pack** to change the original.
2. **New quest** starts from scratch. Pick the NPCs who give and finish it, write what they say,
   add a goal (bring items or defeat monsters) and rewards (exp, gold, items).
3. **Save pack** (Ctrl+S) writes `Mods\Quests\<pack>.json`.
4. **Load it without restarting:** in game, open the [DevTools](../DevTools) command box (F7) and type `reloadquests`.
   New quests appear, changed ones update (even ones you've accepted), and deleted ones disappear.
   Without DevTools, restart the game.

**When it's offered / finished:** pick quests by name instead of typing flag numbers. "Only after *Crafting?* is done" uses that quest's finish flag. Every flag number shows what it means, for example `8138 · "Crafting?" done`.

NPCs show up in the editor's NPC picker once you've visited the zone they're in, plus your own NPCs from [NPCLoader](../NPCLoader).

## Quest pack format

```json
{
  "name": "my-quests",
  "quests": [
    {
      "id": 900000001,
      "title": "Fever Dreams",
      "objective": "Bring 2 Fever to the Towner.",
      "types": ["Normal", "Collect"],
      "giver": 250, "ender": 250,
      "description": "What the giver says…",
      "descriptionForEnder": "What they say when you hand in…",
      "targets": [ { "kind": "item", "item": 630000050, "count": 2 } ],
      "rewards": [ { "kind": "exp", "amount": 150 }, { "kind": "item", "item": 630000042, "amount": 1 } ],
      "clearFlag": 9000
    }
  ]
}
```

| Field | Notes |
|---|---|
| `id` | New quests: 900000001 and up. Same id as an original replaces it. |
| `giver`, `ender` | NPC ids. See `export\npcs.json` or the editor's NPC picker. |
| `targets` | `{"kind":"item","item":ID,"count":N}` or `{"kind":"kill","monster":"Name","count":N}`. Item count -1 = just own one. |
| `rewards` | `{"kind":"exp"/"gold","amount":N}` or `{"kind":"item","item":ID,"amount":N}` |
| `receives` | Items handed over when the quest is accepted: `{"item":ID,"count":N}` |
| `clearFlag` | Progress flag set when cleared, so the quest isn't offered again. **Mod quests: use 9000–9999.** |
| `requires` | Conditions before it's offered: `flagsAll`, `flagsAny`, `itemsAll`, `itemsAny` |
| `setFlagsOnClear` | Flags changed on clear: `[{"flag":9001,"on":true}]` |
| `translations` | Other languages from the original game, kept as they are |

## Known limits

- Quests can only use NPCs that already exist in the game. Adding new NPCs isn't supported yet.
- Flags that aren't quest-finish markers (used by the world itself) show as "not a quest flag".
- The editor edits English text only.

## Build from source

```
dotnet build -c Release              # build and install into the game
dotnet build -c Release -t:Package   # make release\QuestLoader-1.1.0.zip
```

License: MIT (code only; the game belongs to SmokymonkeyS).
