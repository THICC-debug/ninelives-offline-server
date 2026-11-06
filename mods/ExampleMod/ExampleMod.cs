using HarmonyLib;
using NineLivesModLoader;

namespace ExampleMod
{
    // The loader creates this class and calls Load() when the game starts.
    public class ExampleMod : IMod
    {
        public void Load()
        {
            ModLoader.Log("Example mod says hello");

            // Apply every [HarmonyPatch] class in this DLL.
            new Harmony("ninelives.examplemod").PatchAll(typeof(ExampleMod).Assembly);
        }
    }

    // Runs after the game sets up the screen resolution on the login screen.
    // Swap in any game class and method you want to change.
    [HarmonyPatch(typeof(Universe), nameof(Universe.initResolution))]
    static class LoginScreenPatch
    {
        static bool logged;

        static void Postfix()
        {
            if (logged) return;
            logged = true;
            ModLoader.Log("Harmony works: login screen is up");
        }
    }
}
