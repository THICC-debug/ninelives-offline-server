using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace EnemyLoader
{
    // ------------------------------------------------------------ JSON format
    /// <summary>An enemy pack: Mods\Enemies\&lt;name&gt;.json</summary>
    public class EnemyPack
    {
        public string name;
        public string author;
        public string description;
        public List<EnemyDef> enemies = new List<EnemyDef>();
        /// <summary>New names for the game's own monsters.</summary>
        public List<RenameDef> renames = new List<RenameDef>();
    }

    /// <summary>A new name for one of the game's own monsters (all of that kind, in one zone or everywhere).</summary>
    public class RenameDef
    {
        /// <summary>The monster's name in game, e.g. "Banded Yerlbok".</summary>
        public string monster;
        /// <summary>What to call it instead.</summary>
        public string name;
        /// <summary>Only in this zone (blank = everywhere).</summary>
        public string zone;
        public bool enabled = true;
    }

    /// <summary>One spawn: some monsters of one kind, around a spot.</summary>
    public class EnemyDef
    {
        /// <summary>Unique number for this spawn (the editor picks it).</summary>
        public int id;
        public bool enabled = true;
        /// <summary>Which monster: its name in game, e.g. "Banded Yerlbok".</summary>
        public string monster;
        /// <summary>Optional new name shown over their heads (kill quests then use this name).</summary>
        public string name;
        public string zone;
        public float x, y, z;
        /// <summary>Which way they face, in degrees.</summary>
        public float rotation;
        /// <summary>How many, and how far apart (metres from the spot).</summary>
        public int count = 1;
        public float spread = 4f;
        /// <summary>Come back after being killed, like the game's own monsters.</summary>
        public bool respawn = true;
        /// <summary>Loot table to drop (0 = the monster's own).</summary>
        public int dropId;

        // Tweaks: 1 = as the game has it, 2 = twice as much, 0.5 = half.
        public float health = 1, damage = 1, attackSpeed = 1, moveSpeed = 1, defense = 1, sight = 1, exp = 1, gold = 1, size = 1;
    }

    [ModInfo("enemyloader", "EnemyLoader", "1.1.0", Author = "Nine Lives offline project",
        Description = "Spawn monsters where you like, with tweaked health, damage and speed, and rename the game's own monsters, from JSON enemy packs in Mods\\Enemies.")]
    public class EnemyLoader : Mod
    {
        string packsDir;
        float spawnAt = -1;
        // What can be spawned in this zone: monster name -> its prefab and loot table (from the zone's own spawners).
        readonly Dictionary<string, KeyValuePair<GameObject, int>> kinds = new Dictionary<string, KeyValuePair<GameObject, int>>();
        // Spawned monsters, by spawn id.
        readonly Dictionary<int, List<GameObject>> spawned = new Dictionary<int, List<GameObject>>();
        // Renames: from the saved packs, and the editor's unsaved ones (by pack) while you edit.
        List<RenameDef> savedRenames = new List<RenameDef>();
        readonly Dictionary<string, List<RenameDef>> liveRenames = new Dictionary<string, List<RenameDef>>();
        Dictionary<string, string> renameHere = new Dictionary<string, string>();   // original name -> new name, in this zone
        // Spawns that have a name of their own: renames leave them alone.
        readonly HashSet<int> ownName = new HashSet<int>();
        float sweepAt;
        /// <summary>A renamed monster carries a switched-off child object named this plus its original name (ContentLoader reads it too).</summary>
        public const string Marker = "ModRenamedFrom:";

        public override void Load()
        {
            packsDir = Path.Combine(ModLoader.ModsDir, "Enemies");
            Directory.CreateDirectory(packsDir);
            Commands.Register("enemyreload", "enemyreload", "respawn enemies from Mods\\Enemies", a => Respawn());
            Commands.Register("enemykinds", "enemykinds", "list the monsters that can be spawned in this zone", a => { FindKinds(); return string.Join(", ", kinds.Keys.OrderBy(k => k).ToArray()); });
            foreach (var p in new[] { "/enemy", "/enemy/remove", "/enemy/reload", "/enemy/kinds", "/enemy/stats", "/enemy/goto", "/enemy/renames" })
                LiveLink.Route(p, r => HandleLive(r.Method, r.Path, r.Query, r.Body));
            savedRenames = ReadRenames();
            // Kill quests (and ScriptLoader rules) go by the monster's name: let them see its original name.
            try { new Harmony("ninelives.enemyloader").PatchAll(typeof(EnemyLoader).Assembly); }
            catch (Exception e) { Log("couldn't hook kill quests, so renamed monsters won't count for them: " + e.Message); }
            Log("ready - enemy packs go in Mods\\Enemies");
        }

        public override void OnZoneLoaded(string zone) { spawned.Clear(); kinds.Clear(); ownName.Clear(); spawnAt = -1; BuildRenames(); }
        public override void OnPlayerSpawned() { spawnAt = Time.realtimeSinceStartup + 5f; }

        public override void OnUpdate()
        {
            if (Time.realtimeSinceStartup >= sweepAt)
            {
                sweepAt = Time.realtimeSinceStartup + 1f;
                try { Sweep(); } catch (Exception e) { Log("renaming failed: " + e.Message); sweepAt += 30f; }
            }
            if (spawnAt < 0 || Time.realtimeSinceStartup < spawnAt) return;
            spawnAt = -1;
            try { Log(Respawn()); } catch (Exception e) { Log("spawning failed: " + e); }
        }

        // ------------------------------------------------------------ packs
        List<EnemyDef> ReadPacks()
        {
            var all = new List<EnemyDef>();
            foreach (var f in Directory.GetFiles(packsDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var p = JsonConvert.DeserializeObject<EnemyPack>(File.ReadAllText(f));
                    if (p != null && p.enemies != null) all.AddRange(p.enemies.Where(e => e != null));
                }
                catch (Exception e) { Log(Path.GetFileName(f) + ": " + e.Message); }
            }
            return all;
        }

        List<RenameDef> ReadRenames()
        {
            var all = new List<RenameDef>();
            foreach (var f in Directory.GetFiles(packsDir, "*.json").OrderBy(f => f))
            {
                if (liveRenames.ContainsKey(Path.GetFileNameWithoutExtension(f))) continue;   // the editor has newer ones
                try
                {
                    var p = JsonConvert.DeserializeObject<EnemyPack>(File.ReadAllText(f));
                    if (p != null && p.renames != null) all.AddRange(p.renames.Where(r => r != null));
                }
                catch { }   // ReadPacks logs it
            }
            return all;
        }

        // ------------------------------------------------------------ renaming the game's own monsters
        void BuildRenames()
        {
            var map = new Dictionary<string, string>();
            var zone = Game.Zone ?? "";
            var all = savedRenames.Concat(liveRenames.Values.SelectMany(l => l))
                .Where(r => r != null && r.enabled && !string.IsNullOrEmpty(r.monster) && !string.IsNullOrEmpty(r.name) && r.name.Trim() != r.monster.Trim()).ToList();
            foreach (var r in all) if (string.IsNullOrEmpty(r.zone) && !map.ContainsKey(r.monster.Trim())) map[r.monster.Trim()] = r.name.Trim();
            foreach (var r in all) if (r.zone == zone) map[r.monster.Trim()] = r.name.Trim();   // a rename for this zone wins
            renameHere = map;
            sweepAt = 0;
        }

        /// <summary>Every second: give the monsters around their new names (or their own back).</summary>
        void Sweep()
        {
            if (!Game.InWorld) return;
            var me = Game.Stats;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s == null || s == me || s.isMount || s.isMinion || s.itemDropGroupID <= 0 || ownName.Contains(s.GetInstanceID())) continue;
                var mark = MarkerOf(s);
                var original = mark != null ? mark.name.Substring(Marker.Length) : s.dispName;
                if (string.IsNullOrEmpty(original)) continue;
                string want;
                if (renameHere.TryGetValue(original, out want))
                {
                    if (mark == null) { var m = new GameObject(Marker + original); m.SetActive(false); m.transform.parent = s.transform; }
                    if (s.dispName != want) s.dispName = want;
                }
                else if (mark != null) { s.dispName = original; mark.parent = null; UnityEngine.Object.Destroy(mark.gameObject); }
            }
        }

        static Transform MarkerOf(CommonStats s)
        {
            var t = s.transform;
            for (int i = 0; i < t.childCount; i++) { var c = t.GetChild(i); if (c.name.StartsWith(Marker)) return c; }
            return null;
        }
        /// <summary>The game's own name for a renamed monster (as kill quests know it); null when it isn't renamed.</summary>
        public static string OriginalName(CommonStats s)
        {
            var m = s != null ? MarkerOf(s) : null;
            return m != null ? m.name.Substring(Marker.Length) : null;
        }

        string Respawn()
        {
            foreach (var id in spawned.Keys.ToList()) Despawn(id);
            liveRenames.Clear(); savedRenames = ReadRenames(); BuildRenames();
            if (!Game.InWorld) return "not in the world";
            FindKinds();
            int n = 0; var problems = new List<string>();
            foreach (var d in ReadPacks())
            {
                if (!d.enabled || d.zone != Game.Zone) continue;
                var why = Spawn(d);
                if (why == null) n++; else problems.Add(d.id + " (" + d.monster + "): " + why);
            }
            foreach (var p in problems) Log(p);
            return n + " enemy spawn(s) in " + Game.Zone + (problems.Count > 0 ? ", " + problems.Count + " couldn't be spawned (see the log)" : "") + "; " + kinds.Count + " kinds can be spawned here";
        }

        // ------------------------------------------------------------ what can be spawned
        /// <summary>The zone's spawners name every monster prefab it uses: collect them by monster name.</summary>
        void FindKinds()
        {
            kinds.Clear();
            Action<GameObject, int> add = (prefab, drop) =>
            {
                if (prefab == null) return;
                var st = prefab.GetComponentInChildren<CommonStats>(true);
                if (st == null || string.IsNullOrEmpty(st.dispName) || st.isMount) return;
                int dropId = drop > 0 ? drop : st.itemDropGroupID;
                KeyValuePair<GameObject, int> have;
                // a few names have a stronger kind too (another loot table): use the basic one, the lowest table, as the editor does
                if (!kinds.TryGetValue(st.dispName, out have) || (have.Value <= 0 && dropId > 0) || (dropId > 0 && dropId < have.Value)) kinds[st.dispName] = new KeyValuePair<GameObject, int>(prefab, dropId);
            };
            foreach (var g in Resources.FindObjectsOfTypeAll<AI_GroupController>())
            {
                var a = g.autoInstantiateRandom; if (a == null) continue;
                if (a.prefabs != null) foreach (var p in a.prefabs) if (p != null) add(p.prefab, p.itemDropGroupID);
                if (a.defaultPrefabs != null) foreach (var p in a.defaultPrefabs) if (p != null) add(p.prefab, p.itemDropGroupID);
            }
            foreach (var f in Resources.FindObjectsOfTypeAll<FlagAction_InstantiateRandom>())
            {
                if (f.prefabs != null) foreach (var p in f.prefabs) if (p != null) add(p.prefab, p.itemDropGroupID);
                if (f.defaultPrefabs != null) foreach (var p in f.defaultPrefabs) if (p != null) add(p.prefab, p.itemDropGroupID);
            }
        }

        // ------------------------------------------------------------ spawning
        string Spawn(EnemyDef d)
        {
            if (string.IsNullOrEmpty(d.monster)) return "no monster picked";
            if (kinds.Count == 0) FindKinds();
            KeyValuePair<GameObject, int> kind;
            if (!kinds.TryGetValue(d.monster, out kind)) return "\"" + d.monster + "\" can't be spawned in " + Game.Zone + " (only monsters that live in this zone can)";
            var list = new List<GameObject>();
            spawned[d.id] = list;
            int count = Mathf.Clamp(d.count, 1, 30);
            var rnd = new System.Random(d.id);
            var center = new Vector3(d.x, d.y, d.z);
            for (int i = 0; i < count; i++)
            {
                var off = Vector3.zero;
                if (i > 0 || count > 1)
                {
                    // spread around the spot (the same places every time)
                    float ang = (float)(rnd.NextDouble() * Math.PI * 2), r = d.spread * Mathf.Sqrt((float)rnd.NextDouble());
                    off = new Vector3(Mathf.Cos(ang) * r, 0, Mathf.Sin(ang) * r);
                }
                var spot = Ground(center + off);
                var rot = Quaternion.Euler(0, d.rotation + (count > 1 ? (float)rnd.NextDouble() * 360f : 0f), 0);
                var go = (GameObject)UnityEngine.Object.Instantiate(kind.Key, spot, rot);
                go.name = "ModEnemy_" + d.id + "_" + i;
                // The game's AI reads its parent when it starts (as its spawners give it one): no parent, no AI.
                go.transform.parent = Holder();
                go.transform.position = spot;
                var st = go.GetComponentInChildren<CommonStats>(true);
                if (st != null) { Tweak(st, d, kind.Value); if (!string.IsNullOrEmpty(d.name)) ownName.Add(st.GetInstanceID()); }
                if (d.size > 0 && Math.Abs(d.size - 1f) > 0.001f) go.transform.localScale = go.transform.localScale * Mathf.Clamp(d.size, 0.2f, 5f);
                list.Add(go);
            }
            return null;
        }

        /// <summary>
        /// Change its stats before it starts: the game builds the live stats from the *_init values in
        /// CommonStats.Start, unless they're already built, so mark them unbuilt too.
        /// </summary>
        static void Tweak(CommonStats st, EnemyDef d, int kindDrop)
        {
            Func<float, float> m = v => float.IsNaN(v) || v < 0 ? 1f : Mathf.Clamp(v, 0f, 100f);
            float hp = m(d.health), dmg = m(d.damage), aspd = Mathf.Max(0.05f, m(d.attackSpeed)), spd = m(d.moveSpeed), def = m(d.defense), sight = m(d.sight);
            st.health_init *= hp;
            st.damage_init *= dmg; st.damageRandom_init *= dmg;
            st.weaponDamage_init *= dmg; st.weaponDamageRandom_init *= dmg;
            st.weaponDamage_off_init *= dmg; st.weaponDamageRandom_off_init *= dmg;
            st.weaponSpeed_init *= aspd; st.weaponSpeed_off_init *= aspd;   // swings per second
            st.moveSpeed_init *= spd;
            st.defense_init *= def; st.resistance_init *= def;
            st.sightRange_init *= sight;
            st.exp_init = (long)Math.Round(st.exp_init * m(d.exp));
            st.gold_init = (long)Math.Round(st.gold_init * m(d.gold));
            foreach (var p in new[] { st.health, st.damage, st.weaponDamage, st.weaponDamage_off, st.weaponSpeed, st.weaponSpeed_off, st.moveSpeed, st.defense, st.resistance, st.sightRange })
                if (p != null) p.initialized = false;
            st.exp = null; st.gold = null;
            if (!string.IsNullOrEmpty(d.name)) st.dispName = d.name;
            if (d.dropId > 0) st.itemDropGroupID = d.dropId;
            else if (kindDrop > 0) st.itemDropGroupID = kindDrop;
            st.isNonResurrect = !d.respawn;
        }

        Transform holder;
        /// <summary>The object spawned monsters hang under, in the game's world like the game's own spawners.</summary>
        Transform Holder()
        {
            if (holder != null) return holder;
            var h = new GameObject("ModEnemies").transform;
            try { var w = Universe.GetWorldRootCtrl(); if (w != null) h.parent = w.transform; } catch { }
            h.localPosition = Vector3.zero; h.localRotation = Quaternion.identity; h.localScale = Vector3.one;
            return holder = h;
        }

        void Despawn(int id)
        {
            List<GameObject> list;
            if (!spawned.TryGetValue(id, out list)) return;
            foreach (var go in list) if (go != null) UnityEngine.Object.Destroy(go);
            spawned.Remove(id);
        }

        static Vector3 Ground(Vector3 spot)
        {
            var player = Game.Player != null ? Game.Player.transform : null;
            bool found = false; float bestY = 0, bestD = float.MaxValue;
            // from well above, so a spot picked on the map (height unknown) still finds the ground
            float top = spot.y > 0.01f || spot.y < -0.01f ? spot.y + 30f : 1500f;
            foreach (var h in Physics.RaycastAll(new Vector3(spot.x, top, spot.z), Vector3.down, top + 600f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (player != null && h.transform.IsChildOf(player)) continue;
                if (h.collider.GetComponentInParent<CommonStats>() != null) continue;   // a monster or NPC, not the ground
                // the ground nearest the height asked for, or the highest when no height was given
                float d = spot.y > 0.01f || spot.y < -0.01f ? Mathf.Abs(h.point.y - spot.y) : -h.point.y;
                if (d < bestD) { bestD = d; bestY = h.point.y; found = true; }
            }
            return found ? new Vector3(spot.x, bestY + 0.1f, spot.z) : spot;
        }

        // ------------------------------------------------------------ the editor's live link
        string HandleLive(string method, string path, string query, string body)
        {
            var q = new LiveRequest { Query = query };
            if (path == "/enemy/kinds")
            {
                if (!Game.InWorld) return "{\"zone\":null,\"kinds\":[]}";
                FindKinds();
                return JsonConvert.SerializeObject(new { zone = Game.Zone, kinds = kinds.Select(k => new { name = k.Key, dropId = k.Value.Value }).OrderBy(k => k.name).ToList() });
            }
            if (path == "/enemy/renames" && method == "POST")   // the editor's renames for one pack, before they're saved
            {
                var r = JsonConvert.DeserializeObject<LiveRenames>(body);
                if (r == null || string.IsNullOrEmpty(r.pack)) return "{\"error\":\"needs a pack\"}";
                liveRenames[r.pack] = r.renames ?? new List<RenameDef>();
                savedRenames = ReadRenames(); BuildRenames(); Sweep();
                return "{\"ok\":true,\"here\":" + renameHere.Count + "}";
            }
            if (path == "/enemy/reload" && method == "POST") return "{\"ok\":true,\"message\":" + LiveLink.Quote(Respawn()) + "}";
            if (path == "/enemy/remove" && method == "POST")
            {
                int id; int.TryParse(q.Get("id") ?? "", out id);
                Despawn(id);
                return "{\"ok\":true}";
            }
            if (path == "/enemy" && method == "POST")   // show this spawn as it is in the editor now
            {
                var d = JsonConvert.DeserializeObject<EnemyDef>(body);
                if (d == null || d.id <= 0) return "{\"error\":\"needs an id\"}";
                Despawn(d.id);
                if (!Game.InWorld || d.zone != Game.Zone || !d.enabled) return "{\"ok\":true,\"shown\":false}";
                var why = Spawn(d);
                return why == null ? "{\"ok\":true,\"shown\":true}" : "{\"ok\":false,\"error\":" + LiveLink.Quote(why) + "}";
            }
            if (path == "/enemy/goto" && method == "POST")
            {
                var d = JsonConvert.DeserializeObject<EnemyDef>(body);
                if (d == null) return "{\"error\":\"bad request\"}";
                Game.Teleport(Ground(new Vector3(d.x - 6f, d.y, d.z - 6f)));
                return "{\"ok\":true}";
            }
            if (path == "/enemy/stats")   // the live stats of a spawned one, to check the tweaks
            {
                int id; int.TryParse(q.Get("id") ?? "", out id);
                List<GameObject> list;
                if (!spawned.TryGetValue(id, out list)) return "{\"error\":\"not spawned here\"}";
                var go = list.FirstOrDefault(g => g != null); if (go == null) return "{\"error\":\"gone\"}";
                var st = go.GetComponentInChildren<CommonStats>(true);
                Func<FloatParameterSet, float> v = p => p != null && p.initialized ? p.mathTotal() : -1f;
                return JsonConvert.SerializeObject(new
                {
                    name = st.dispName, started = st.health != null && st.health.initialized, dead = st.isDead,
                    health = v(st.health), damage = v(st.damage), weaponDamage = v(st.weaponDamage), weaponSpeed = v(st.weaponSpeed),
                    moveSpeed = v(st.moveSpeed), defense = v(st.defense), sight = v(st.sightRange),
                    exp = st.exp != null ? st.exp.read() : -1, gold = st.gold != null ? st.gold.read() : -1,
                    dropId = st.itemDropGroupID, respawns = !st.isNonResurrect, count = list.Count(g => g != null),
                    x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z, scale = go.transform.localScale.x
                });
            }
            return "{\"error\":\"unknown request\"}";
        }
    }

    class LiveRenames { public string pack; public List<RenameDef> renames; }

    /// <summary>
    /// Kill quests count a kill when the monster's name matches (QuestController.onKill). While that runs a
    /// renamed monster has its original name back, so the game's quests, and quests made for it, still count.
    /// First in and last out, so ScriptLoader's kill rules see the original name too.
    /// </summary>
    [HarmonyPatch(typeof(QuestController), nameof(QuestController.onKill))]
    static class KillByOriginalName
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(CommonStats stats_, out string __state)
        {
            __state = null;
            try { var o = EnemyLoader.OriginalName(stats_); if (o != null) { __state = stats_.dispName; stats_.dispName = o; } } catch { }
        }
        [HarmonyPriority(Priority.Last)]
        static void Postfix(CommonStats stats_, string __state) { if (__state != null && stats_ != null) stats_.dispName = __state; }
    }
}
