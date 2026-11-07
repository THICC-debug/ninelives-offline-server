# NPCLoader for Nine Lives

**Version 1.2.1** · a mod for *Ninelives* 0.24.3 (offline)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

Add your own NPCs to the world: quest givers, vendors, alchemists, bankers, or people who just
stand around. Each one is a copy of an existing NPC in the same zone (so it gets that NPC's
model, clothes and animations) with its own id, name, position and role.

## Requirements

- *Ninelives* 0.24.3 patched with the
  [Nine Lives offline server](https://github.com/THICC-debug/ninelives-offline-server)'s
  Setup.exe (or `Patch Game.bat` from source), mod loader 2.1 or newer.
- [DevTools](../DevTools) for the in-game commands (optional; you can also write packs by hand).

## Install

Tick NPCLoader in Setup.exe, or unzip the release into your game folder (the one with `Ninelives.exe`).
You get `Mods\NPCLoader.dll` and two examples, both switched off:

- `Mods\NPCs\example-npcs.json.disabled`: Old Bram, next to Crimson Crest's binding stone
- `Mods\Quests\example-old-bram.json.disabled`: his quest, "Bram's Berries" (needs [QuestLoader](../QuestLoader))

Switch them on in the Nine Lives Editor (**Packs & mods**), or remove `.disabled` from the names.

## Placing NPCs with the editor

The Nine Lives Editor's **NPCs** tab: pick who the NPC looks like (any NPC in the same zone), put it
next to an NPC or type a position, choose its job, and give it quests (**New quest for …**) or
shop stock (**Edit the stock**).

**Live in game:** with the game running (and you in the world), the NPCs tab connects to it by
itself (the badge at the top says **● In game**). Every change shows in the world straight away:

- **Put here (in front of me)** places the NPC where you're standing in game, facing you.
- **Take me there** teleports you in front of the NPC; **Turn to face me** turns it toward you.
- The arrows move it half a metre (or 1 m) from where you're facing in game; ⟲ ⟳ turn it.
- **◀ Previous / Next ▶** flip through the looks of the people standing in your zone.
- **Save** keeps the changes; **Discard changes** puts the game back as it was saved.

The link is a small server inside the game that only your own PC can reach (`127.0.0.1:8099`).

## Placing NPCs in game (DevTools)

Open the DevTools command box (**F7**):

| Command | What it does |
|---|---|
| `npcnear` | List the NPCs around you: their id (to use as a template), name and role |
| `npcadd <name> [template] [role]` | Add an NPC 2.5 m in front of you, facing you. `npcadd Old Bram 250 quest` copies NPC 250 (the Towner). Leave the template out to copy the nearest NPC. |
| `npclist` | List the custom NPCs in this zone |
| `npcmove <id>` | Move one of your NPCs in front of you |
| `npcremove <id>` | Delete one of your NPCs |
| `npcreload` | Reload every NPC pack |

NPCs you add this way are saved to `Mods\NPCs\my-npcs.json`.

## Roles

| Role | What clicking the NPC does |
|---|---|
| `quest` (default) | Talks: offers and accepts the quests whose giver/ender is this NPC's id |
| `shop` | Opens a shop. Its stock is the shop table with this NPC's id. Make one with [ContentLoader](../ContentLoader) (`shops.drops.<id>`). Copy a vendor as the template. |
| `alchemy` | Opens the alchemy window |
| `bank`, `share` | Opens the bank / shared bank |
| `none` | Nothing: just someone standing there |

## Giving your NPC a quest

With [QuestLoader](../QuestLoader), use the NPC's id as `giver` and `ender`:

```json
{ "id": 900000101, "title": "Bram's Berries", "giver": 90001, "ender": 90001,
  "targets": [ { "kind": "item", "item": 600000001, "count": 3 } ],
  "rewards": [ { "kind": "gold", "amount": 120 } ], "clearFlag": 9100 }
```

Custom NPCs show up in the editor's NPC picker, marked "(custom)".

## NPC pack format

`Mods\NPCs\<name>.json`:

```json
{
  "name": "my-npcs",
  "npcs": [
    { "id": 90001, "name": "Old Bram", "zone": "Zone_CrimsonIsles",
      "x": 1285.5, "y": 144.8, "z": 1116.5, "rotation": 0,
      "template": 250, "role": "quest" }
  ]
}
```

| Field | Notes |
|---|---|
| `id` | Use **90001 and up**, so ids never clash with the game's own NPCs |
| `zone` | Scene name, e.g. `Zone_CrimsonIsles` (DevTools `pos` shows it) |
| `x`, `y`, `z`, `rotation` | Position, and facing in degrees. DevTools `pos` shows where you stand. The NPC is set down on whatever surface is under that spot. |
| `template` | Id of an NPC **in the same zone** to copy. `npcnear` lists them. |
| `role` | See above |
| `sit` | `true` to sit (if the template has sitting animations). Default: stand, even if the template sits. |
| `enabled` | `false` hides the NPC without deleting it |

## Known limits

- The template has to be an NPC in the same zone. It can be one the game is hiding at the moment.
- The copy behaves like its template: if the template wanders a route, the copy may wander too.
  Pick an NPC that stands still.
- New NPCs can't have a brand-new look; they reuse the template's.

## Build from source

```
dotnet build -c Release              # build and install into the game
dotnet build -c Release -t:Package   # make release\NPCLoader-1.0.2.zip
```

License: MIT (code only; the game belongs to SmokymonkeyS).
