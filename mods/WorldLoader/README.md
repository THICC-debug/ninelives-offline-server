# WorldLoader for Nine Lives

**Version 1.1.0** · a mod for *Ninelives* 0.24.3 (offline) · build in the [Nine Lives Editor](../../editor)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

Build in the world: copy any house, fence, tree or prop in a zone and place the copies anywhere in
it (turned, tilted, resized), or hide things that are in the way.

## Requirements

- *Ninelives* 0.24.3 with Setup.exe (mod loader 2.2 or newer; 2.4 for the editor's 3D view tools).
- The [Nine Lives Editor](../../editor) to build live (or write packs by hand).

## Building with the editor

The Nine Lives Editor's **Build** tab shows the game in 3D (the editor runs it in the background as its
3D view). WorldLoader provides the building tools there:

- **Assets** (bottom panel): pictures of every kind of thing in the zone. Click one and it follows the
  mouse in the 3D view; click to put it down (wheel or `,` `.` turns it, Shift+click keeps placing copies,
  Esc cancels).
- **Select, move, rotate, scale** (`Q` `W` `E` `R`, or the toolbar): drag the thing itself to slide it
  along the ground, or its handles: red/green/blue arrows to move along one direction, rings to turn,
  the cube to resize. A yellow box shows what's selected. **Snap** (toolbar) snaps to a grid and to angle
  steps; hold Ctrl to flip it while dragging.
- **Right-click** a thing (a quick click) for a menu: properties, move tools, pick up and carry, drop to
  the ground, copy, frame, delete. `Ctrl+D` copies, `Del` deletes, double-click any scenery to see what
  it is (to hide it, or copy a bigger part of it, in the inspector's full page).
- Your NPCs (NPCLoader) and the monsters you spawned (EnemyLoader 1.0+) can be clicked, dragged, turned
  and deleted the same way.

Every change is an unsaved change of the pack in the editor: **Save** keeps it, **Undo** and **Discard**
take it back. In a normal game, **F8** turns on a simpler build mode (click and drag your copies; `[` `]`
turn, PgUp/PgDn raise, `+` `-` resize).

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

- Parts drawn from the baked index don't cast shadows (they still receive them): casting shadows from
  pieces of the game's baked meshes crashes Unity 5.3. Parts rebuilt from a collision model are normal.

- Copies come from the zone you're in. Copying from other zones isn't supported yet.
- NPCs and monsters can't be copied here: use [NPCLoader](../NPCLoader) and [EnemyLoader](../EnemyLoader).

License: MIT (code only; the game belongs to SmokymonkeyS).
