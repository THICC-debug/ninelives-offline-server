# NPCLoader for Nine Lives

**Version 1.2.3** · a mod for *Ninelives* 0.24.3 (offline)

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

In the Nine Lives Editor's **Build** view (the game in 3D, run by the editor): **Assets → NPCs** shows
everyone standing in the zone. Click one, then click in the 3D view to put down a new NPC who looks like
them (Esc cancels). Drag it with the move and rotate tools like any placed thing; **right-click** it for
a menu (give it a dialogue, make a quest for it, properties, move, delete).

The NPC inspector (right side) sets its name, look, job, quests (**New quest for …**) and, for a
vendor, what it sells (see [Vendors](#vendors)). It's connected to the game (the 3D view, or a game you started yourself: the badge
at the top says **● In game**), so every change shows in the world straight away ("me" is the camera in
the 3D view):

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
| `shop` | Opens a shop with the NPC's `sells` list (see [Vendors](#vendors)). Without a list: the shop table with this NPC's id from [ContentLoader](../ContentLoader) (`shops.drops.<id>`). |
| `alchemy` | Opens the alchemy window |
| `bank`, `share` | Opens the bank / shared bank |
| `none` | Nothing: just someone standing there |

## Vendors

Any of your NPCs can be a vendor. In the editor, click **🛒 Make … a vendor** in the NPC inspector (or
pick **Shopkeeper** as its job), then type item names into **What … sells**:

- **Price for one**: the gold the player pays. Leave it blank for the game's usual price (shown greyed out).
- **How many**: how many they have. Each one takes a slot in the shop window, which has 54.
- **Quick fill**: copy the stock of one of the game's vendors, or set every price to a percentage of the usual price.
- ▲ changes the order the items appear in; ✕ removes one.

Changes show in the 3D view's shop straight away. The shop holds exactly the list. Once the player has
bought everything, it's stocked again the next time they open it. A bought item is worth its usual amount
when the player sells it back, whatever they paid. The editor warns when you price something below that,
because players could then buy it and sell it back at a profit.

In the pack it's the `sells` list:

```json
{ "id": 90004, "name": "Mira", "role": "shop", "template": 1010, "zone": "Zone_CrimsonIsles", "x": 1290, "y": 145, "z": 1110,
  "sells": [ { "id": 100201000, "price": 50, "stock": 3 }, { "id": 100101000 } ] }
```

| `sells` field | Notes |
|---|---|
| `id` | Item id |
| `price` | Gold for one. Left out or 0: the game's usual price (base price × shop markup) |
| `stock` | How many (default 1) |

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
| `sells` | For a `shop`: what it sells and for how much (see [Vendors](#vendors)) |
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
dotnet build -c Release -t:Package   # make release\NPCLoader-1.2.2.zip
```

License: MIT (code only; the game belongs to SmokymonkeyS).
