# Nine Lives Editor

> [!NOTE]
> Made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

One app for everything the mods let you change: `Nine Lives Editor.exe`. It runs on your own PC,
nothing goes online, and it needs nothing else installed (no Python).

## Build: the world in 3D

The editor opens on **Build**: the game itself, running in the background (no window, no sound, no
character) and shown in the middle of the editor, with the tools around it like Unity or the Creation Kit:

| Where | What |
|---|---|
| Middle | The 3D view: fly around and work on the world |
| Above it | Save / Discard (your placed things and NPCs), Undo, the tools (Select, Move, Rotate, Scale), Snap with grid and angle steps, the zone list, Hide NPCs, Bright light, No fog, look speed |
| Left | The outliner: your placed things and NPCs in this zone (click to select, double-click to fly there) |
| Right | The inspector: everything about the selected thing or NPC (⤢ Full page for the big version) |
| Bottom | **Assets** (pictures of every kind of thing in the zone, in folders like town, furniture and dungeons, and every person: click one, then click in the 3D view to put it down) and the **Map** |

Controls in the 3D view: hold the **right mouse button** to look and fly with **WASD / Q E** (Shift
faster, wheel = speed), **arrows** fly once you click in the 3D view, **middle-drag** pans, **wheel** zooms, **Alt+left-drag**
orbits, **F** frames the selection. **Q W E R** pick Select, Move, Rotate, Scale; drag a thing to slide
it, or its handles; **Ctrl** flips snapping. A quick **right-click** on a thing or NPC opens a menu (add
dialogue, make a quest, properties, move…). **Ctrl+D** copies, **Del** deletes, **Ctrl+Z** undoes,
**Ctrl+S** saves, **double-click** scenery to inspect it. The Help tab has the full list.

The editor starts the offline server and the game with `-nleditor`; the game logs in with its own
account (`nle-editor@local.test`, a "Builder" character the server makes for it), so your characters are
never touched. Closing the editor stops both. Untick **Start with the editor** to start it yourself (▶ Start).
Needs mod loader 2.4, [WorldLoader](../mods/WorldLoader) 1.1 and [NPCLoader](../mods/NPCLoader) 1.2.3.

## The other tabs

| Tab | What you do there | Needs |
|---|---|---|
| **Map** | Every zone from above: NPCs, your things, script areas. Drag to move, Shift+wheel to turn, click a spot to put something there | mod loader 2.3 |
| **Quests** | Make quests, or change the game's own (copy one of the 345 as a starting point). For a "defeat monsters" goal, pick the enemy from a list of each zone's enemies: picture, stats and a map of where they mostly are (needs ContentLoader 1.4) | [QuestLoader](../mods/QuestLoader) |
| **Items** | Prices and stats (damage, armor, health, stack size…), names and descriptions | [ContentLoader](../mods/ContentLoader) |
| **Monster loot** | What monsters drop and how often, with a "roll 10 kills" preview | ContentLoader |
| **Enemies** (in Build: Assets ▸ Enemies, inspector ▸ Enemy) | Put monsters where you want them: pick one (picture, stats, suggested fighting level), click the map, make it weaker or tougher (health, damage, attack speed, size, rewards…). Shows in game straight away. **✎ Rename the game's monsters** gives the game's own monsters new names | [EnemyLoader](../mods/EnemyLoader) |
| **Shops** | What the game's own vendors sell | ContentLoader |
| **Dialogue** | Conversations with answers: each answer leads on, and can give items, start quests, set flags | [ScriptLoader](../mods/ScriptLoader) 1.1 |
| **Scripts** | Rules (when this happens, then do that) and Lua scripts, with a console that runs in the game | ScriptLoader |
| **Packs & mods** | See which mods are installed, switch mods and packs on and off | |
| **Help** | Getting started, every control, and shortcuts to common tasks | |

**Your own vendors:** select one of your NPCs and click **🛒 Make … a vendor**. Type the items it sells,
with a price and how many for each (blank price = the game's usual one), or copy a game vendor's stock
to start from. Needs [NPCLoader](../mods/NPCLoader) 1.3.

**Arrange Build's panels** (like Premiere Pro): the left, right and bottom panels are rows of tabs. Drag a
tab onto another panel to move it there (a bar shows where it goes), onto an empty side to open a panel there
("Dock left / right / below"), or onto the 3D view to open it in a window of its own. Tabs from the top row can
be dragged in too. ✕ closes a tab; a panel with no tabs folds away. **⇤⇥** on the bottom panel stretches it
across the whole width, under the side panels. A panel can be **split** in two: drop a tab on the strip at a
side panel's bottom ("Split: put it below") or at the bottom panel's right end ("Split: put it beside"), and
drag the line between them to resize. View ▸ Panels puts everything back. The layout is remembered.

**Pages in their own windows:** hover a tab at the top and click its **⧉** (or middle-click the tab) to open
that page in a window of its own. Move it anywhere (another screen too), maximise it, or press **⛶ Full screen**
(F11; Esc leaves). **Dock in Build: Left / Right / Below** at its top puts it back as a tab in that panel;
**⧉** on a Build panel opens what it shows in its own window.

**Find anything:** the search box at the top (Ctrl+K) finds quests, items, monsters, shops and
NPCs by name and opens them. A dot on a tab means it has unsaved changes.

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
| Enemy packs (spawns and renames) | `Mods\Enemies\<name>.json` |
| World packs (placed and hidden things) | `Mods\World\<name>.json` |
| Script packs (rules, Lua, dialogues) | `Mods\Scripts\<name>.json` |

A pack switched off on the **Packs & mods** tab is renamed to `<name>.json.disabled`, so the game
skips it. The examples that come with the mods start switched off. Each save keeps the previous
version as `<name>.json.bak`.

With the game running, NPC, World and Script packs reload in game by themselves when you save. For
quests and content use `reloadquests` and `reloadcontent` in the DevTools command box (F7), or restart
the game. A switched-off pack does nothing in game; the tab shows a bar with **Switch it on** when the
open pack is off.

## Sharing mods

On **Packs & mods**, **⇪ Export a mod** puts the packs you pick into one `.zip`, with a name, author and
a few words about it. If a quest's giver is one of your NPCs, it offers to add that NPC pack too. Send the
zip to anyone with Nine Lives Offline.

**To add a mod someone sent you, drop its .zip into the game's `Mods` folder** (Packs & mods has an
**📂 Open the Mods folder** button). That's all. It's installed the next time you play (the offline server
does it before the game starts) or when the editor opens, and right away if Packs & mods is open:

- its packs go into the pack folders (`Mods\Quests`, `Mods\NPCs`…) and are ordinary packs from then
  on: they load in game, and anyone can open and change them in the editor
- the zip moves to `Mods\Installed`, with a note of what came from it; Packs & mods lists these under
  **Mods you've added**, with a ✎ button for each pack and a warning when one uses the same NPC, quest
  or spawn id as another of your packs
- a pack with the same name as one you have is replaced (yours is kept as `.bak`), so dropping in a
  newer version of a mod updates it
- only pack files are installed, never code: a zip with a `.dll` or `.exe` in it is left where it is
  (code mods go in with Setup.exe)

`nine-lives-mod.json` inside the zip describes the mod (name, author, what's in it, which mods it was
made with). The same rules are in `dropmods.py` (run by the server) and the editor's `Share.cs`.

## How it's built

| Folder | What's in it |
|---|---|
| `editor/web/` | The pages (HTML, CSS, JavaScript). Each tab and panel is one page; `index.html` lays them out. |
| `editor/NineLivesEditor/` | The app (C#, .NET Framework 4.8): the window, a small local server the pages use to read and save packs, and `GameRunner` (starts the game in the background and docks its window into Build). The pages and WebView2 are built into the one exe; `--web DIR` serves the pages from a folder instead, for working on them. |

License: MIT (code only; the game belongs to SmokymonkeyS).
