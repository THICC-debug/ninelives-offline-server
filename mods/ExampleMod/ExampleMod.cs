using HarmonyLib;
using NineLivesModLoader;
using UnityEngine;

namespace ExampleMod
{
    // A starter mod showing the mod API. Copy this folder to make your own.
    // ModInfo shows up in the in-game mod list (F11).
    [ModInfo("examplemod", "ExampleMod", "1.0.0", Author = "you", Description = "Template mod: says hello and shows the API.")]
    public class ExampleMod : Mod
    {
        KeyCode helloKey;

        // Called once, very early. Read settings and apply Harmony patches here.
        public override void Load()
        {
            // Settings live in Mods\config\examplemod.cfg and are created with these defaults.
            helloKey = Config.Get("HelloKey", KeyCode.F8, "Press to show a hello message in game");
            new Harmony("ninelives.examplemod").PatchAll(typeof(ExampleMod).Assembly);
            Log("hello from Load()");
        }

        public override void OnZoneLoaded(string zone) { Log("entered " + zone); }

        public override void OnPlayerSpawned() { Messages.Show("ExampleMod: press " + helloKey + " to say hello"); }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(helloKey) && Game.InWorld)
                Messages.Show("Hello! You have " + Game.Stats.gold.read() + " gold. (" + Game.ItemName(630000050) + " is item 630000050)");
        }
    }

    // Harmony example: runs after the game sets up the login screen.
    // Swap in any game class and method you want to change.
    [HarmonyPatch(typeof(Universe), nameof(Universe.initResolution))]
    static class LoginScreenPatch
    {
        static bool logged;
        static void Postfix()
        {
            if (logged) return;
            logged = true;
            ModLoader.Log("ExampleMod: Harmony works, the login screen is up");
        }
    }
}
