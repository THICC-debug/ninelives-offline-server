# ScriptLoader for Nine Lives

**Version 1.1.0** · a mod for *Ninelives* 0.24.3 (offline) · build scripts in the [Nine Lives Editor](../../editor)

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

Make things happen in the game:

- **Rules**, no code: *when* something happens, check a few things, *then* do something.
- **Dialogues**: conversations with answers to choose from, for an NPC or started by a rule.
- **Lua scripts** for anything rules can't do (Lua 5.2 via [MoonSharp](https://www.moonsharp.org/), MIT).

## Requirements

- *Ninelives* 0.24.3 with Setup.exe (mod loader 2.2 or newer).
- `Mods\lib\MoonSharp.Interpreter.dll` (comes with this mod) for Lua.

## Install

Tick ScriptLoader in Setup.exe, or unzip the release into your game folder. You get
`Mods\ScriptLoader.dll`, `Mods\lib\MoonSharp.Interpreter.dll` and
`Mods\Scripts\example-scripts.json.disabled` (a welcome message at Crimson Crest's binding stone,
a Gnoll bounty, and F5 shows where you are). Switch the example on in the editor (**Packs & mods**).

## Rules

In the editor's **Scripts** tab: **New rule**, then pick from the menus.

| When | Only if | Then |
|---|---|---|
| Entering a zone | A flag is on / off | Show a message (`{name}` = the player's name) |
| Walking into / out of an area | Has an item | Wait |
| Talking to an NPC | Is in a zone | Give / take an item |
| Accepting / finishing a quest | Has gold | Give or take gold, give experience |
| Defeating a monster | Has a quest in progress | Set a flag, start a quest |
| A flag turns on or off | (any of these can be NOT) | Teleport the player |
| Having enough of an item | | Show / hide one of your NPCs or placed things |
| Every few seconds | | Hide part of the game's world |
| Pressing a key | | Run Lua |

A rule can run **only once per character**, or wait a number of seconds between runs.
**▶ Run now in the game** runs a rule's actions straight away (to try them).
Saving reloads the scripts in the game.

## Dialogues

In the editor's **Dialogue** tab: **New dialogue** (or **Give … a dialogue** on an NPC in the NPCs tab).

- A dialogue is **lines** (what the NPC says) and **answers** (what the player can say).
- Each answer goes to another line, or ends the conversation. It can be shown **only if** something
  is true, and can **also do** things: give an item, start a quest, set a flag… (the same actions as rules).
- A line can also do things when it's shown.
- Pick an NPC and talking to them opens it. Give an NPC who only talks the job "none" (NPCs tab), so
  the quest window doesn't open as well. Rules start dialogues with **Start a dialogue**, Lua with
  `game.dialog("id")`.
- **▶ Preview here** walks through it in the editor; **Play in the game** shows it in the game.

In the game it's a box at the bottom of the screen: click an answer or press 1–9; Esc or walking away closes it.

## Lua

**New Lua script** gives you a code editor and a **console** that runs Lua in the game.
Scripts run once when you enter the world; hook events with `on`:

```lua
on("talk", function(e)
  if e.npc == 90001 then game.message("Old Bram waves at " .. game.player().name) end
end)

every(60, function() game.gold(1) end)        -- a coin a minute

onKey("F5", function()
  local p = game.player()
  log("I'm at", p.x, p.y, p.z, "in", p.zone)
end)
```

| | |
|---|---|
| `on(event, fn)` | `zone`, `talk` (e.npc, e.name), `questAccepted` / `questDone` (e.quest), `kill` (e.monster, e.monsterId) |
| `onKey(key, fn)` | `"F5"`, `"G"`, `"Alpha1"`… |
| `after(s, fn)`, `every(s, fn)`, `stop(id)` | timers |
| `game.message(text, seconds)` | |
| `game.give(item, n)`, `game.take(item, n)`, `game.count(item)` | items in the bag |
| `game.gold(n)`, `game.exp(n)` | minus takes gold |
| `game.flag(n)`, `game.setFlag(n, on)` | progress flags; mods use 9000–9999 |
| `game.player()` | `{x, y, z, facing, zone, name, gold}` |
| `game.teleport(x, y, z)`, `game.zone()`, `game.time()` | |
| `game.npc(id, show)`, `game.thing(id, show)` | your NPCs ([NPCLoader](../NPCLoader)) and placed things ([WorldLoader](../WorldLoader)) |
| `game.startQuest(id)`, `game.dialog(id)` | |
| `log(...)`, `print(...)` | to the editor's console and `Mods\ModLoader.log` |

Lua is sandboxed: it can't read or write files or start programs. It runs inside the game, so an
endless loop freezes the game: use `every()` instead of loops that wait.

## Script pack format

`Mods\Scripts\<name>.json`:

```json
{
  "name": "my-scripts",
  "rules": [
    { "id": "welcome", "name": "Welcome", "once": true,
      "when": { "type": "enterArea", "zone": "Zone_CrimsonIsles", "x": 1282.5, "y": 145, "z": 1121.1, "radius": 8 },
      "if":   [ { "type": "flag", "flag": 9100, "on": false } ],
      "then": [ { "type": "message", "text": "Welcome, {name}!" }, { "type": "giveItem", "item": 600000001, "count": 2 } ] }
  ],
  "dialogs": [
    { "id": "bram-talk", "name": "Old Bram", "npc": 90001, "start": "hello",
      "nodes": [
        { "id": "hello", "text": "Hello, {name}! Got any berries?",
          "choices": [ { "text": "Here you go.", "if": [ { "type": "item", "item": 600000001, "count": 1 } ],
                         "do": [ { "type": "takeItem", "item": 600000001, "count": 1 }, { "type": "gold", "amount": 20 } ], "goto": "thanks" },
                       { "text": "Not today." } ] },
        { "id": "thanks", "text": "Lovely! Here, for your trouble.", "choices": [ { "text": "Bye." } ] } ] }
  ],
  "scripts": [ { "id": "main", "name": "Main", "code": "log('hello')" } ]
}
```

"Once per character" is remembered in `Mods\ScriptLoader\state.json`.

License: MIT (code only; the game belongs to SmokymonkeyS). MoonSharp: MIT.
