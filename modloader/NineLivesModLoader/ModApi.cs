using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NineLivesModLoader
{
    /// <summary>
    /// Optional base class for mods. Gives you Info, Config and Log, and hooks you can override.
    /// (Plain <see cref="IMod"/> still works.)
    /// </summary>
    public abstract class Mod : IMod
    {
        public ModEntry Info { get; private set; }
        public ModConfig Config { get; private set; }

        internal void Attach(ModEntry info)
        {
            Info = info;
            Config = ModConfig.For(info.Id);
            Events.GameStarted += () => Safe("OnGameStarted", OnGameStarted);
            Events.ZoneLoaded += z => Safe("OnZoneLoaded", () => OnZoneLoaded(z));
            Events.PlayerSpawned += () => Safe("OnPlayerSpawned", OnPlayerSpawned);
            Events.Update += OnUpdate;
            Events.GUI += OnGUI;
        }

        /// <summary>Called once, very early (before the first scene). Apply Harmony patches here.</summary>
        public abstract void Load();
        /// <summary>The login screen is up; Unity is fully running.</summary>
        public virtual void OnGameStarted() { }
        /// <summary>A zone or dungeon finished loading (scene name, e.g. "Zone_CrimsonIsles").</summary>
        public virtual void OnZoneLoaded(string zone) { }
        /// <summary>The player character exists in the world.</summary>
        public virtual void OnPlayerSpawned() { }
        /// <summary>Every frame.</summary>
        public virtual void OnUpdate() { }
        /// <summary>Unity OnGUI: draw IMGUI here.</summary>
        public virtual void OnGUI() { }

        protected void Log(string msg) { ModLoader.Log((Info != null ? Info.Name : GetType().Name) + ": " + msg); }

        void Safe(string what, Action a)
        {
            try { a(); } catch (Exception e) { Log(what + " failed: " + e); }
        }
    }

    /// <summary>Game events. Subscribe from Load().</summary>
    public static class Events
    {
        public static event Action GameStarted;
        public static event Action<string> ZoneLoaded;
        public static event Action PlayerSpawned;
        public static event Action Update;
        public static event Action GUI;

        internal static void FireGameStarted() { Fire(GameStarted, "GameStarted"); }
        internal static void FirePlayerSpawned() { Fire(PlayerSpawned, "PlayerSpawned"); }
        internal static void FireZoneLoaded(string z)
        {
            if (ZoneLoaded == null) return;
            foreach (Action<string> h in ZoneLoaded.GetInvocationList())
                try { h(z); } catch (Exception e) { ModLoader.Log("ZoneLoaded handler failed: " + e); }
        }
        internal static void FireUpdate() { Fire(Update, null); }
        internal static void FireGUI() { Fire(GUI, null); }

        static void Fire(Action ev, string name)
        {
            if (ev == null) return;
            foreach (Action h in ev.GetInvocationList())
            {
                try { h(); }
                catch (Exception e) { if (name != null) ModLoader.Log(name + " handler failed: " + e); }
            }
        }
    }

    /// <summary>Handy shortcuts into the game.</summary>
    public static class Game
    {
        /// <summary>The player character, or null when not in the world.</summary>
        public static PlayerController Player { get { return Universe.getCurrentPlayerController(); } }
        /// <summary>The player's stats (exp, gold …), or null.</summary>
        public static CommonStats Stats { get { return Universe.getCurrentPlayerStats(); } }
        public static bool InWorld { get { return Player != null; } }
        /// <summary>Current scene name, e.g. "Zone_CrimsonIsles".</summary>
        public static string Zone { get { return SceneManager.GetActiveScene().name; } }

        public static Vector3 PlayerPosition { get { var p = Player; return p != null ? p.transform.position : Vector3.zero; } }

        /// <summary>Move the player (handles the CharacterController for you).</summary>
        public static void Teleport(Vector3 pos)
        {
            var p = Player;
            if (p == null) return;
            var cc = p.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            p.transform.position = pos;
            if (cc != null) cc.enabled = true;
        }

        /// <summary>Put items in the player's bag. Returns false if they don't fit or you're not in the world.</summary>
        public static bool GiveItem(int itemId, int count)
        {
            var bag = GUI_Bag.getGUIInstance();
            if (bag == null || !InWorld || !bag.addNewItems(itemId, count)) return false;
            // Adding to an existing stack doesn't mark the bag as changed, so quest item
            // counters (e.g. "Fever 1/2") and saving wouldn't notice. Mark it.
            if (bag.actionDockCtrl != null) bag.actionDockCtrl.onUpdated();
            return true;
        }

        public static void GiveGold(long amount) { var s = Stats; if (s != null) s.gold.add(amount); }
        public static void GiveExp(long amount) { var s = Stats; if (s != null) s.exp.add(amount); }

        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static HashSet<int> hasItemFile;

        /// <summary>An item's display name, e.g. 630000050 -> "Fever". Null if unknown.</summary>
        public static string ItemName(int itemId)
        {
            string n;
            if (names.TryGetValue(itemId, out n)) return n;
            names[itemId] = n = LookUpName(itemId);
            return n;
        }

        static string LookUpName(int itemId)
        {
            try
            {
                if (hasItemFile == null) hasItemFile = new HashSet<int>(Item.getItemXMLIDList());
                int fileId = itemId;
                string prefix = null;
                if (!hasItemFile.Contains(fileId))
                {
                    // Variants ("Fine Knife" etc.) have no item file of their own: they use their base
                    // item's, and their affix ("Fine") comes from the item library's random code table.
                    var lib = ItemLibraryBase.GetItemLibraryCtrl();
                    ActionAndButton e;
                    if (lib == null || lib.itemIDList == null || !lib.itemIDList.TryGetValue(itemId, out e) || e == null
                        || e.baseItemID <= 0 || !hasItemFile.Contains(e.baseItemID))
                        return null;                 // no data: don't ask the game (it would log an error)
                    fileId = e.baseItemID;
                    int i = lib.itemRandomCodeIDs != null ? lib.itemRandomCodeIDs.IndexOf(e.randomCodeID) : -1;
                    if (e.randomCodeID != 0 && i >= 0 && lib.itemRandomCodes[i].affixType != ItemAffix.none)
                        prefix = lib.itemRandomCodes[i].affixType.ToString();
                }
                var st = Item.LoadFromResource(fileId);
                if (st == null) return null;
                return prefix != null ? prefix + " " + st.dispName : st.dispName;
            }
            catch { return null; }
        }

        /// <summary>Is a progress flag set? (Mods should use flags 9000-9999.)</summary>
        public static bool GetFlag(int index) { return FlagController.CheckFlag(index); }
        public static void SetFlag(int index, bool on) { FlagController.setByIndex(index, on); }
    }

    /// <summary>
    /// Text commands any mod can add. A command box (the DevTools mod, F7) runs them, so mods
    /// don't need to depend on each other.
    /// </summary>
    public static class Commands
    {
        public sealed class Command
        {
            public string Name, Usage, Help, Mod;
            public Func<string[], string> Run;
        }

        static readonly Dictionary<string, Command> all = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Add a command. The handler gets the words after the command name and returns text to show
        /// (or null). Throw ArgumentException to show a usage message.
        /// </summary>
        public static void Register(string name, string usage, string help, Func<string[], string> handler)
        {
            all[name] = new Command { Name = name, Usage = usage, Help = help, Run = handler };
        }

        public static IEnumerable<Command> All { get { return all.Values; } }

        /// <summary>Run a registered command. False if there's no such command.</summary>
        public static bool TryRun(string name, string[] args, out string result)
        {
            result = null;
            Command c;
            if (!all.TryGetValue(name, out c)) return false;
            try { result = c.Run(args); }
            catch (ArgumentException e) { result = e.Message.Length > 0 ? e.Message : "Usage: " + c.Usage; }
            catch (Exception e) { result = "Error: " + e.Message; ModLoader.Log("command " + name + " failed: " + e); }
            return true;
        }
    }

    /// <summary>On-screen messages drawn by the mod loader.</summary>
    public static class Messages
    {
        internal struct Msg { public string Text; public float Until; }
        internal static readonly List<Msg> Active = new List<Msg>();

        /// <summary>Show a message at the top of the screen.</summary>
        public static void Show(string text, float seconds = 4f)
        {
            Active.Add(new Msg { Text = text, Until = Time.realtimeSinceStartup + seconds });
            if (Active.Count > 5) Active.RemoveAt(0);
        }
    }

    /// <summary>
    /// Simple per-mod settings in Mods\config\&lt;mod id&gt;.cfg (key = value, # comments).
    /// Values you ask for are written back with their defaults, so players can find and edit them.
    /// </summary>
    public sealed class ModConfig
    {
        static readonly Dictionary<string, ModConfig> all = new Dictionary<string, ModConfig>();
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, string> comments = new Dictionary<string, string>();

        public static ModConfig For(string modId)
        {
            ModConfig c;
            if (!all.TryGetValue(modId, out c)) all[modId] = c = new ModConfig(Path.Combine(ModLoader.ConfigDir, modId + ".cfg"));
            return c;
        }

        ModConfig(string path)
        {
            this.path = path;
            try
            {
                if (File.Exists(path))
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int i = line.IndexOf('=');
                        if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch (Exception e) { ModLoader.Log("Couldn't read " + path + ": " + e.Message); }
        }

        /// <summary>Read a setting (string, int, float, bool or KeyCode), creating it with the default if missing.</summary>
        public T Get<T>(string key, T defaultValue, string comment = null)
        {
            if (!order.Contains(key)) order.Add(key);
            if (comment != null) comments[key] = comment;
            string s;
            T result = defaultValue;
            if (values.TryGetValue(key, out s))
            {
                try
                {
                    if (typeof(T).IsEnum) result = (T)Enum.Parse(typeof(T), s, true);
                    else result = (T)Convert.ChangeType(s, typeof(T), CultureInfo.InvariantCulture);
                }
                catch { ModLoader.Log("Bad value for " + key + " in " + Path.GetFileName(path) + ": " + s); }
            }
            else values[key] = Convert.ToString(defaultValue, CultureInfo.InvariantCulture);
            Save();
            return result;
        }

        public void Set<T>(string key, T value)
        {
            if (!order.Contains(key)) order.Add(key);
            values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
            Save();
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>();
                foreach (var k in order)
                {
                    string c;
                    if (comments.TryGetValue(k, out c)) lines.Add("# " + c);
                    lines.Add(k + " = " + values[k]);
                }
                foreach (var kv in values) if (!order.Contains(kv.Key)) lines.Add(kv.Key + " = " + kv.Value);
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception e) { ModLoader.Log("Couldn't save " + path + ": " + e.Message); }
        }
    }
}
