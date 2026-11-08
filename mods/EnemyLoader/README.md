# EnemyLoader

Put monsters where you want them in Nine Lives, and make them weaker or tougher: more health, harder
hits, faster attacks, bigger, better rewards. Spawns are kept in JSON **enemy packs** in `Mods\Enemies`.

The easy way is the Nine Lives Editor's **Build** workspace. In the Assets panel, choose **Enemies** and click a
monster: it's spawned where the 3D view is looking. Then tweak it in the inspector's **Enemy** tab: drag it on
the map and drag the sliders. While your game is in that zone, every change shows up in it
straight away. The editor also shows the monster's picture and stats and a **suggested fighting level**.

## Install

Unzip into the game folder (the one with `Ninelives.exe`). It needs the Nine Lives mod loader.
For pictures, stats and the map in the editor, also install ContentLoader 1.4.1 or newer.

## What can be spawned where

Only monsters that live in a zone can be spawned there. The game only has their models loaded in
their own zone. In game, `enemykinds` (F7 command box) lists what can be spawned where you are.

## Enemy packs

`Mods\Enemies\<name>.json`. Rename it to `.json.disabled` to switch it off.

```json
{
  "name": "my-enemies",
  "enemies": [
    {
      "id": 1001,
      "monster": "Bone Soldier",
      "name": "Bone Warlord",
      "zone": "Zone_CrimsonIsles",
      "x": 1012.5, "y": 0, "z": 1180,
      "count": 3, "spread": 6,
      "respawn": false,
      "health": 8, "damage": 2, "attackSpeed": 1.2, "size": 1.6, "exp": 10, "gold": 8
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | A unique number for the spawn |
| `monster` | The monster's name in game, exactly (capital letters too) |
| `name` | Optional new name over their heads. Kill quests then need this name |
| `zone`, `x`, `y`, `z` | Where. `y` 0 = on the ground |
| `rotation` | Which way it faces, in degrees |
| `count`, `spread` | How many (1–30), and how far from the spot they stand (metres) |
| `respawn` | `true` (default): comes back after being killed, like the game's own monsters |
| `dropId` | Loot table to drop instead of its own (see ContentLoader's `loot.json`) |
| `enabled` | `false` switches one spawn off |

**Tweaks** multiply the game's numbers: 1 = as in game, 2 = twice, 0.5 = half. Leave one out to keep it as it is.

| Tweak | What it changes |
|---|---|
| `health` | Health |
| `damage` | Every hit's damage |
| `attackSpeed` | Attacks per second (2 = attacks twice as often) |
| `moveSpeed` | How fast it runs |
| `defense` | Defense and resistance |
| `sight` | How far away it notices you |
| `exp`, `gold` | What killing it gives |
| `size` | How big it looks (0.3–4) |

## Renaming the game's own monsters

A pack can also give the game's own monsters new names: every monster of that kind, everywhere or in one
zone. In the editor: Enemies ▸ **✎ Rename the game's monsters**. In the pack:

```json
"renames": [
  { "monster": "Banded Yerlbok", "name": "Grumpy Yerlbok" },
  { "monster": "Utal", "name": "Utal the Old", "zone": "Zone_CrimsonIsles" }
]
```

| Field | What it does |
|---|---|
| `monster` | The monster's name in game, exactly |
| `name` | What to call it instead (over its head and in the target portrait) |
| `zone` | Only in this zone (leave it out for everywhere; a rename for the zone you're in wins) |
| `enabled` | `false` switches it off |

Kill quests still count a renamed monster under the game's name, so the game's own quests keep working,
and so do ScriptLoader `kill` rules. A spawn with a `name` of its own keeps that name. This uses Harmony
(`Mods\lib\0Harmony.dll`, included).

## Suggested fighting level

Nine Lives has no levels: exp buys skills. So the editor works out its own 1–30 scale for how hard a
fight is. It's based on how much the monster can take (health, defense) and how hard it hits (damage
per second), compared with every monster the game has. Level 1 is the weakest, 30 the toughest, and
tweaked ones can go past 30. The editor also names a few game monsters that are about as tough.

## Commands (F7 box)

- `enemyreload`: respawn everything from the packs
- `enemykinds`: list the monsters that can be spawned in this zone

## Credits

Made with help from Claude (Anthropic), an AI assistant.
