using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NineLivesModLoader
{
    /// <summary>Implement this (or derive from <see cref="Mod"/>) in a public class with a parameterless constructor.</summary>
    public interface IMod
    {
        /// <summary>Called once, very early (before the first scene). Apply Harmony patches here.</summary>
        void Load();
    }

    /// <summary>Describes a mod. Put it on your mod class.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ModInfoAttribute : Attribute
    {
        public ModInfoAttribute(string id, string name, string version) { Id = id; Name = name; Version = version; }
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Author { get; set; }
        public string Description { get; set; }
        /// <summary>Ids of mods that must load first.</summary>
        public string[] Dependencies { get; set; }
    }

    /// <summary>A mod the loader found, loaded or not.</summary>
    public sealed class ModEntry
    {
        public string Id, Name, Version, Author, Description, File;
        public string[] Dependencies = new string[0];
        public bool Enabled = true;     // from Mods\mods.cfg
        public bool Loaded;
        public string Problem;          // why it didn't load, if it didn't
        public IMod Instance;
        internal Type Type;
    }

    /// <summary>
    /// Loads every DLL in the game's Mods folder. Shared libraries (0Harmony.dll …) go in Mods\lib.
    /// Mods can be switched off in Mods\mods.cfg or the in-game mod list (F11).
    /// </summary>
    public static class ModLoader
    {
        public const string Version = "2.1.1";
        static bool started;
        static string logPath;

        public static string GameDir { get; private set; }
        public static string ModsDir { get; private set; }
        public static string ConfigDir { get { return Path.Combine(ModsDir, "config"); } }
        public static readonly List<ModEntry> Mods = new List<ModEntry>();

        /// <summary>Kept for mods written against loader 1.x.</summary>
        public static List<IMod> LoadedMods
        {
            get { var l = new List<IMod>(); foreach (var m in Mods) if (m.Loaded) l.Add(m.Instance); return l; }
        }

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
                Directory.CreateDirectory(ConfigDir);
                logPath = Path.Combine(ModsDir, "ModLoader.log");
                File.WriteAllText(logPath, "");
                Log("Nine Lives Mod Loader " + Version + " - " + DateTime.Now);

                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromMods;
                StartHost();   // needs Harmony, so only after the resolver is in place
                LoadMods();
            }
            catch (Exception e)
            {
                Log("Mod loader error: " + e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StartHost()
        {
            try { ModHost.Install(); }
            catch (Exception e) { Log("Couldn't start events and the mod list (is Mods\\lib\\0Harmony.dll there?): " + e.Message); }
        }

        static void LoadMods()
        {
            var enabled = ReadEnabled();

            // 1) Find mods and read their info.
            foreach (var dll in Directory.GetFiles(ModsDir, "*.dll"))
            {
                try { Discover(dll); }
                catch (Exception e) { Mods.Add(new ModEntry { Id = Path.GetFileNameWithoutExtension(dll), Name = Path.GetFileName(dll), File = Path.GetFileName(dll), Problem = "couldn't read: " + e.Message }); }
            }
            foreach (var m in Mods)
            {
                bool on;
                if (enabled.TryGetValue(m.Id, out on)) m.Enabled = on;
            }
            WriteEnabled();

            // 2) Load in dependency order.
            var done = new HashSet<string>();
            int loadedCount = 0;
            for (int pass = 0; pass < Mods.Count + 1; pass++)
            {
                bool progress = false;
                foreach (var m in Mods)
                {
                    if (m.Loaded || m.Problem != null || done.Contains(m.Id)) continue;
                    if (!m.Enabled) { m.Problem = "switched off"; continue; }
                    if (m.Type == null) { m.Problem = "no mod class (implement IMod)"; continue; }
                    bool ready = true;
                    foreach (var dep in m.Dependencies)
                    {
                        var d = Mods.Find(x => x.Id == dep);
                        if (d == null || !d.Enabled) { m.Problem = "needs mod '" + dep + "'"; break; }
                        if (d.Problem != null) { m.Problem = "needs mod '" + dep + "' (" + d.Problem + ")"; break; }
                        if (!d.Loaded) ready = false;   // wait for it, try again next pass
                    }
                    if (m.Problem != null || !ready) continue;
                    done.Add(m.Id);
                    progress = true;
                    try
                    {
                        m.Instance = (IMod)Activator.CreateInstance(m.Type);
                        var asMod = m.Instance as Mod;
                        if (asMod != null) asMod.Attach(m);
                        m.Instance.Load();
                        m.Loaded = true;
                        loadedCount++;
                        Log("Loaded " + m.Name + " " + m.Version + " (" + m.File + ")");
                    }
                    catch (Exception e)
                    {
                        m.Problem = "crashed while loading: " + e.Message;
                        Log("FAILED " + m.Name + ": " + e);
                    }
                }
                if (!progress) break;
            }
            foreach (var m in Mods)
                if (!m.Loaded && m.Problem == null) m.Problem = "dependency loop";
            foreach (var m in Mods)
                if (!m.Loaded) Log("Not loaded: " + m.Name + " - " + m.Problem);
            Log(loadedCount + " mod(s) loaded");
        }

        static void Discover(string path)
        {
            var asm = Assembly.LoadFrom(path);
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
            bool any = false;
            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IMod).IsAssignableFrom(t)) continue;
                any = true;
                var info = (ModInfoAttribute)Attribute.GetCustomAttribute(t, typeof(ModInfoAttribute));
                Mods.Add(new ModEntry
                {
                    Id = info != null ? info.Id : t.Name.ToLowerInvariant(),
                    Name = info != null ? info.Name : t.Name,
                    Version = info != null ? info.Version : asm.GetName().Version.ToString(),
                    Author = info != null ? info.Author : null,
                    Description = info != null ? info.Description : null,
                    Dependencies = info != null && info.Dependencies != null ? info.Dependencies : new string[0],
                    File = Path.GetFileName(path),
                    Type = t,
                });
            }
            if (!any) Log("No mod class in " + Path.GetFileName(path) + " (fine for plain libraries)");
        }

        // ------------------------------------------------------------ Mods\mods.cfg
        static string EnabledPath { get { return Path.Combine(ModsDir, "mods.cfg"); } }

        static Dictionary<string, bool> ReadEnabled()
        {
            var d = new Dictionary<string, bool>();
            try
            {
                if (File.Exists(EnabledPath))
                    foreach (var raw in File.ReadAllLines(EnabledPath))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim().ToLowerInvariant() != "false";
                    }
            }
            catch (Exception e) { Log("Couldn't read mods.cfg: " + e.Message); }
            return d;
        }

        /// <summary>Saves which mods are on. Takes effect the next time the game starts.</summary>
        public static void WriteEnabled()
        {
            try
            {
                var lines = new List<string> { "# Which mods load. Set to false to switch a mod off (or use F11 in game). Applies on next start." };
                foreach (var m in Mods) lines.Add(m.Id + " = " + (m.Enabled ? "true" : "false"));
                File.WriteAllLines(EnabledPath, lines.ToArray());
            }
            catch (Exception e) { Log("Couldn't write mods.cfg: " + e.Message); }
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
