# WorldLoader for Nine Lives

**Version 1.0.1** · a mod for *Ninelives* 0.24.3 (offline) · build in the [Nine Lives Editor](../../editor)

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

Much of the game's scenery is "pre-baked": combined into big meshes when the game was built. Those
meshes can't be read by mods, and a plain copy of a pre-baked part still draws at the original's
place (and is shown or hidden along with it). WorldLoader handles each pre-baked part of a copy, in
this order:

1. **Collision model:** buildings usually collide with their own model; the copy uses that.
2. **Baked index:** the copy draws its own pieces of the baked mesh, at the copy, as an ordinary
   object. Which pieces belong to which part is stored in the game's scene files but not available
   while the game runs, so it ships as `Mods\WorldLoader\batches\<scene>.json` (names, positions and
   piece numbers only), made by [`tools/batch_index.py`](tools/batch_index.py).
3. **Same model in memory:** a not-baked copy of the same prop, checked to fit exactly.
4. **Anchor (last resort):** the copy keeps its baked piece and Unity draws it from an anchor that
   moves with the copy. It can fade in and out as you turn, so while a zone has these, WorldLoader
   switches off the cameras' "hidden behind something" check.

Copies with no collision of their own get a box that fits their look, so you can't walk through them.
The editor's Pick shows how many parts are pre-baked. If a part can't be rebuilt, the copy says so.

### Remaking the baked index

Only needed for a different game version:

```
pip install UnityPy
python tools/batch_index.py "<game folder>"     # writes batches\<scene>.json
```

## Known limits

- Copies come from the zone you're in. Copying from other zones isn't supported yet.
- NPCs and monsters can't be copied here: use [NPCLoader](../NPCLoader).

License: MIT (code only; the game belongs to SmokymonkeyS).
