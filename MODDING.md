# Modding Nine Lives

`Patch Game.bat` installs a small mod loader into the game. At startup it loads every
mod DLL in `Ninelives\Mods\`, and mods change the game's code with
[Harmony](https://harmony.pardeike.net/). You never edit game files by hand.

```
Ninelives\
├── Mods\
│   ├── lib\0Harmony.dll      ← shared libraries for mods
│   ├── YourMod.dll           ← mods go here
│   ├── config\yourmod.cfg    ← each mod's settings
│   ├── mods.cfg              ← which mods are on
│   └── ModLoader.log         ← what loaded, and any errors
└── Ninelives_Data\Managed\NineLivesModLoader.dll
```

In game, **F11** opens the mod list: what's installed, what's running, and why anything didn't load.
Tick or untick a mod to switch it on or off. That's saved to `mods.cfg` and applies on the next start.

> [!NOTE]
> **Why not BepInEx?** BepInEx was tried first. On this game's Unity version (5.3.6)
> its startup hook breaks every UI script and leaves a blank screen, so this project
> uses its own small loader instead.

## Your first mod in 5 minutes

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) and a game patched with
`Patch Game.bat`.

1. Copy `mods/ExampleMod` to `mods/MyMod`, and rename `ExampleMod.csproj` and `ExampleMod.cs`
   (and the class and Harmony ID inside).
2. Build it:
   ```
   dotnet build -c Release mods/MyMod
   ```
   The build copies `MyMod.dll` into `Ninelives\Mods\` and Harmony into `Mods\lib\`.
3. Start the game and check `Ninelives\Mods\ModLoader.log`.

## How a mod looks

```csharp
using HarmonyLib;
using NineLivesModLoader;
using UnityEngine;

[ModInfo("mymod", "My Mod", "1.0.0", Author = "you", Description = "Shown in the F11 mod list")]
public class MyMod : Mod
{
    KeyCode key;

    public override void Load()            // once, very early: settings and Harmony patches
    {
        key = Config.Get("Key", KeyCode.F8, "Key that gives gold");   // Mods\config\mymod.cfg
        new Harmony("me.mymod").PatchAll(typeof(MyMod).Assembly);
    }

    public override void OnUpdate()        // every frame
    {
        if (Input.GetKeyDown(key) && Game.InWorld)
        {
            Game.GiveGold(100);
            Messages.Show("+100 gold");
        }
    }
}

// Harmony patch: change how the game itself works.
[HarmonyPatch(typeof(SomeGameClass), "SomeMethod")]
static class DoubleGold
{
    static void Prefix(ref int amount) => amount *= 2;
}
```

### The mod API (`NineLivesModLoader`)

| Part | What it gives you |
|---|---|
| `[ModInfo(id, name, version)]` | Name, version, `Author`, `Description`, `Dependencies` (ids of mods to load first) |
| `Mod` base class | `Info`, `Config`, `Log()`, and hooks: `OnGameStarted`, `OnZoneLoaded(zone)`, `OnPlayerSpawned`, `OnUpdate`, `OnGUI` |
| `Events` | The same hooks as C# events, if you'd rather subscribe than override |
| `Game` | `Player`, `Stats`, `InWorld`, `Zone`, `PlayerPosition`, `Teleport()`, `GiveItem()`, `GiveGold()`, `GiveExp()`, `ItemName()`, `GetFlag()` / `SetFlag()` |
| `Messages.Show(text)` | A message at the top of the screen |
| `Commands.Register(name, usage, help, handler)` | Add a text command. The DevTools command box (F7) runs it, so your mod doesn't depend on DevTools. |
| `Config.Get(key, default, comment)` | Settings in `Mods\config\<id>.cfg`: string, int, float, bool or `KeyCode`. Missing keys are written with their defaults so players can find them. |
| `ModLoader.Log()` | Writes to `Mods\ModLoader.log` |

Plain `IMod` (just a `Load()` method) still works, for tiny mods.

### Harmony

- **Prefix** runs before the game's method. It can change the arguments, or return `false`
  to skip the original.
- **Postfix** runs after it and can read or change the result (`ref T __result`).
- See the [Harmony docs](https://harmony.pardeike.net/articles/patching.html) for more.

### Quests

Quests don't need code: the [QuestLoader](mods/QuestLoader) mod loads quest packs (JSON) and comes
with a browser-based quest editor.

## Finding what to patch

The game's code is in `Ninelives_Data\Managed\Assembly-UnityScript.dll` (most of the game)
and `Assembly-CSharp-firstpass.dll` (libraries). Open them in
[ILSpy](https://github.com/icsharpcode/ILSpy) or [dnSpy](https://github.com/dnSpyEx/dnSpy)
to browse the classes.

Good places to start:

| Class | What's in it |
|---|---|
| `Universe` | Global game state: player stats, session, settings |
| `PlayerController` | The player character, saving |
| `AI_Base` | Monster AI, death and loot drops |
| `ItemLibraryBase`, `ItemBase`, `ShopItemList` | Item definitions, drop and shop tables |
| `GUI_*` | Every in-game window (bag, bank, shop, skill tree, alchemy…) |
| `GatekeeperAsync` | Everything sent to and from the server |
| `VersionDefine` | Version number and debug switches (`offlineItemDrop`, …) |

Build against the **original** game code: the example `.csproj` uses the patcher's
backup in `Ninelives_Data\OfflineBackup\` automatically.

## Rules of thumb

- Mods target **.NET Framework 3.5**. This Unity version runs a very old Mono, so newer APIs
  aren't there. Keep the example's `TargetFramework`.
- `Load()` runs very early, before the first scene. Don't touch scene objects there:
  use `OnGameStarted`, `OnZoneLoaded` or `OnPlayerSpawned`.
- The hooks catch and log errors for you. In Harmony patches, wrap risky code in `try/catch`,
  so one broken mod doesn't take the others down.
- Shared libraries go in `Mods\lib\`. The loader finds them there automatically.
- Mod progress flags: use **9000–9999**. The original game uses 6110–8306.
- To turn a mod off, untick it in the F11 list or set it to `false` in `Mods\mods.cfg`.

## Releasing a mod

Every mod is self-contained in its own folder, so each one can be released on its own:

```
mods/YourMod/
├── YourMod.csproj     ← has a <Version> and a Package target
├── YourMod.cs
├── README.md          ← what it does, keys, install, "made with AI" note if it applies
└── release/           ← YourMod-<version>.zip appears here (not committed)
```

`dotnet build -c Release -t:Package` builds `release/YourMod-<version>.zip`, laid out to unzip
straight into the game folder (`Mods/YourMod.dll`, `Mods/lib/0Harmony.dll`,
`Mods/YourMod-README.md`). [TravelMod](mods/TravelMod) is a complete example. Upload the zip
as a GitHub release (tag it `yourmod-v1.0.0`) or on a mod site.

Share only **your** DLL and source code, never game files. If your mod changes how the
game talks to the server, document it in [PROTOCOL.md](PROTOCOL.md).
