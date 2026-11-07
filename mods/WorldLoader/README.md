# WorldLoader for Nine Lives

**Version 1.0.0** · a mod for *Ninelives* 0.24.3 (offline) · build in the [Nine Lives Editor](../../editor)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

Build in the world: copy any house, fence, tree or prop in a zone and place the copies anywhere in
it (turned, tilted, resized), or hide things that are in the way.

## Requirements

- *Ninelives* 0.24.3 with Setup.exe (mod loader 2.2 or newer).
- The [Nine Lives Editor](../../editor) to build live (or write packs by hand).

## Building with the editor

Start the game, enter the world, and open the editor's **World** tab (the badge says **● In game**).

1. Turn the camera so the thing you want is in the middle of the game window, then **Pick what I'm
   looking at**. It glows in game. The editor lists it and the bigger things it belongs to (a wall,
   its house, the whole street); the suggested one is marked.
2. **Copy it here** puts a copy in front of you. **Hide it** hides the original instead.
3. Move it from where you face (▲ ▼ ◀ ▶), up and down, **Drop to the ground**, turn ⟲ ⟳, tilt,
   lean and resize. Every change shows in the game right away.
4. **Save** keeps it (`Mods\World\<pack>.json`); **Discard changes** puts the game back as saved.

**Things around me** lists what's within 20 m if aiming is awkward.

## World pack format

```json
{
  "name": "my-world",
  "objects": [
    { "id": "house-1", "name": "Copy of a house", "zone": "Zone_CrimsonIsles",
      "source": "World/_town/house_a", "x": 1290, "y": 144, "z": 1100,
      "rx": 0, "ry": 90, "rz": 0, "scale": 1 },
    { "id": "hide-tree-1", "name": "Hide a tree", "zone": "Zone_CrimsonIsles",
      "source": "World/_trees/tree#12", "hide": true }
  ]
}
```

| Field | Notes |
|---|---|
| `id` | Any text, unique |
| `zone` | Scene the object is placed in |
| `source` | Path of the thing to copy or hide, as the editor's Pick shows it |
| `x`, `y`, `z` | Position |
| `rx`, `ry`, `rz` | Tilt, turn and lean, in degrees |
| `scale` | 1 = same size |
| `hide` | `true` hides the source instead of copying it |
| `keepScripts` | `true` copies the game's scripts too (doors, triggers…). By default a copy is only its look and collision. |
| `enabled` | `false` switches it off without deleting it |

## How copies work

Much of the game's scenery is "pre-baked" (combined into big meshes when the game was built), and a
plain copy of a pre-baked part would still draw at the original's place. WorldLoader rebuilds those
parts: from the part's collision model when it has one, or by cutting the part out of the baked mesh.
The editor's Pick shows how many parts are pre-baked. If a part can't be rebuilt, the copy says so.

## Known limits

- Copies come from the zone you're in. Copying from other zones isn't supported yet.
- NPCs and monsters can't be copied here: use [NPCLoader](../NPCLoader).

License: MIT (code only; the game belongs to SmokymonkeyS).
