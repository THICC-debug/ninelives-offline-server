using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NineLivesModLoader
{
    /// <summary>Implement this in a public class with a parameterless constructor.</summary>
    public interface IMod
    {
        /// <summary>Called once, right after the game's code starts.</summary>
        void Load();
    }

    /// <summary>
    /// Loads every DLL in the game's Mods folder and calls Load() on each IMod in it.
    /// Shared libraries (such as 0Harmony.dll) go in Mods\lib.
    /// </summary>
    public static class ModLoader
    {
        static bool started;
        static string logPath;

        public static string GameDir { get; private set; }
        public static string ModsDir { get; private set; }
        public static readonly List<IMod> Mods = new List<IMod>();

        public static void Init()
        {
            if (started) return;
            started = true;
            try
            {
                // ...\Ninelives\Ninelives_Data\Managed\NineLivesModLoader.dll -> ...\Ninelives
                var managed = Path.GetDirectoryName(typeof(ModLoader).Assembly.Location);
                GameDir = Path.GetFullPath(Path.Combine(managed, Path.Combine("..", "..")));
                ModsDir = Path.Combine(GameDir, "Mods");
                Directory.CreateDirectory(Path.Combine(ModsDir, "lib"));
                logPath = Path.Combine(ModsDir, "ModLoader.log");
                File.WriteAllText(logPath, "");
                Log("Nine Lives Mod Loader 1.0.0 - " + DateTime.Now);

                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromMods;

                foreach (var dll in Directory.GetFiles(ModsDir, "*.dll"))
                {
                    try { LoadModFile(dll); }
                    catch (Exception e) { Log("FAILED " + Path.GetFileName(dll) + ": " + e); }
                }
                Log(Mods.Count + " mod(s) loaded");
            }
            catch (Exception e)
            {
                Log("Mod loader error: " + e);
            }
        }

        static void LoadModFile(string path)
        {
            var asm = Assembly.LoadFrom(path);
            int found = 0;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IMod).IsAssignableFrom(t)) continue;
                var mod = (IMod)Activator.CreateInstance(t);
                mod.Load();
                Mods.Add(mod);
                found++;
                Log("Loaded " + t.FullName + " from " + Path.GetFileName(path));
            }
            if (found == 0) Log("No IMod class in " + Path.GetFileName(path));
        }

        // Lets mods use libraries kept in Mods\lib (e.g. 0Harmony.dll).
        static Assembly ResolveFromMods(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name + ".dll";
            foreach (var dir in new[] { Path.Combine(ModsDir, "lib"), ModsDir })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        }

        /// <summary>Write a line to Mods\ModLoader.log and the game's output_log.txt.</summary>
        public static void Log(string msg)
        {
            var line = "[ModLoader] " + msg;
            try { if (logPath != null) File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
            try { UnityEngine.Debug.Log(line); } catch { }
        }
    }
}
