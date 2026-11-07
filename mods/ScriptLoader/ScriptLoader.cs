using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NineLivesModLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ScriptLoader
{
    /// <summary>
    /// Event rules ("when … only if … then …") and Lua scripts from script packs in Mods\Scripts.
    /// A pack: { "name", "rules": [ {id, name, enabled, once, cooldown, when, if, then} ], "scripts": [ {id, name, code, enabled} ] }
    /// </summary>
    [ModInfo("scriptloader", "ScriptLoader", "1.1.1", Author = "Nine Lives offline project",
        Description = "Event rules, dialogues and Lua scripts from script packs in Mods\\Scripts (Scripts and Dialogue tabs in the editor).")]
    public partial class ScriptLoader : Mod
    {
        internal static ScriptLoader Instance;
        string packsDir, statePath;

        class Rule
        {
            public string Key, Pack, Id, Name;
            public bool Once; public float Cooldown;
            public JObject When; public JArray If, Then;
            public float LastRun = -9999;
            public bool Inside;        // areas: is the player in it now
            public bool LastBool;      // flags/items: last seen state
            public float NextTimer;
        }

        /// <summary>A running list of actions (it can wait).</summary>
        class Run { public Rule Rule; public JArray Steps; public int Index; public float WaitUntil; public Dictionary<string, object> Event; }

        readonly List<Rule> rules = new List<Rule>();
        readonly List<Run> running = new List<Run>();
        internal readonly List<string> problems = new List<string>();
        internal readonly Dictionary<string, LuaHost> lua = new Dictionary<string, LuaHost>();   // pack -> its Lua
        Dictionary<string, Dictionary<string, bool>> state;   // character -> rule key -> done (for "once")
        float nextPoll, loadAt = -1;
        bool loaded;
        internal string lastTalk;

        public override void Load()
        {
            Instance = this;
            packsDir = Path.Combine(ModLoader.ModsDir, "Scripts");
            Directory.CreateDirectory(packsDir);
            var dir = Path.Combine(ModLoader.ModsDir, "ScriptLoader");
            Directory.CreateDirectory(dir);
            statePath = Path.Combine(dir, "state.json");
            try { state = File.Exists(statePath) ? JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(File.ReadAllText(statePath)) : null; } catch { }
            if (state == null) state = new Dictionary<string, Dictionary<string, bool>>();
            new Harmony("ninelives.scriptloader").PatchAll(typeof(ScriptLoader).Assembly);
            Commands.Register("scriptsreload", "scriptsreload", "reload script packs from Mods\\Scripts", a => Reload());
            LiveLink.Route("/scripts/", Live);
            Log("ready - script packs go in Mods\\Scripts");
        }

        public override void OnPlayerSpawned()
        {
            if (!loaded) loadAt = Time.realtimeSinceStartup + 2f;
            else Fire("zone", new Dictionary<string, object> { { "zone", Game.Zone } });
        }

        public override void OnZoneLoaded(string zone)
        {
            foreach (var r in rules) r.Inside = false;
            running.Clear();
        }

        // ------------------------------------------------------------ loading
        internal string Reload()
        {
            rules.Clear(); running.Clear(); problems.Clear(); dialogs.Clear(); Close();
            foreach (var l in lua.Values) l.Stop();
            lua.Clear();
            foreach (var f in Directory.GetFiles(packsDir, "*.json").OrderBy(f => f))
            {
                var pack = Path.GetFileNameWithoutExtension(f);
                JObject p;
                try { p = JObject.Parse(File.ReadAllText(f)); }
                catch (Exception e) { problems.Add(pack + ": " + e.Message); continue; }
                var rs = p["rules"] as JArray;
                if (rs != null)
                    foreach (var t in rs.OfType<JObject>())
                    {
                        if (t.Value<bool?>("enabled") == false) continue;
                        var when = t["when"] as JObject;
                        if (when == null) { problems.Add(pack + ": a rule has no \"when\""); continue; }
                        rules.Add(new Rule
                        {
                            Pack = pack, Id = t.Value<string>("id") ?? Guid.NewGuid().ToString("N"), Name = t.Value<string>("name"),
                            Key = pack + "/" + (t.Value<string>("id") ?? ""), Once = t.Value<bool?>("once") ?? false,
                            Cooldown = t.Value<float?>("cooldown") ?? 0, When = when, If = t["if"] as JArray ?? new JArray(), Then = t["then"] as JArray ?? new JArray(),
                        });
                    }
                LoadDialogs(pack, p);
                var scripts = p["scripts"] as JArray;
                bool needsLua = scripts != null && scripts.Count > 0 || (rs != null && rs.ToString().Contains("\"lua\""));
                if (needsLua)
                {
                    try { StartLua(pack, scripts); }
                    catch (Exception e) { problems.Add(pack + ": Lua isn't available (is Mods\\lib\\MoonSharp.Interpreter.dll there?): " + e.Message); }
                }
            }
            loaded = true;
            foreach (var p in problems) Log(p);
            if (Game.InWorld) Fire("zone", new Dictionary<string, object> { { "zone", Game.Zone } });
            return "scripts: " + rules.Count + " rule(s), " + dialogs.Count + " dialogue(s), " + lua.Count + " Lua pack(s)" + (problems.Count > 0 ? "; " + problems.Count + " problem(s): " + problems[0] : "");
        }

        // Separate method: if MoonSharp is missing, only this call fails (and is caught above).
        void StartLua(string pack, JArray scripts)
        {
            var host = LuaHost.Create(pack, problems);
            if (host == null) return;
            lua[pack] = host;
            if (scripts != null)
                foreach (var s in scripts.OfType<JObject>())
                    if (s.Value<bool?>("enabled") != false) host.RunFile(s.Value<string>("id") ?? "script", s.Value<string>("code") ?? "");
        }

        // ------------------------------------------------------------ every frame
        public override void OnUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (loadAt > 0 && now >= loadAt) { loadAt = -1; try { Log(Reload()); } catch (Exception e) { Log("loading scripts failed: " + e); } }
            if (!loaded || !Game.InWorld) return;

            foreach (var l in lua.Values) l.Tick(now);
            DialogUpdate();
            // Keys
            foreach (var r in rules.Where(r => r.When.Value<string>("type") == "key").ToList())
            {
                KeyCode k;
                if (TryKey(r.When.Value<string>("key"), out k) && Input.GetKeyDown(k)) Trigger(r, new Dictionary<string, object> { { "key", k.ToString() } });
            }
            foreach (var l in lua.Values) l.KeyCheck();

            if (now >= nextPoll)
            {
                nextPoll = now + 0.25f;
                var me = Game.PlayerPosition;
                foreach (var r in rules.ToList())
                {
                    var w = r.When; var type = w.Value<string>("type");
                    if (type == "enterArea" || type == "leaveArea")
                    {
                        if (!ZoneOk(w)) { r.Inside = false; continue; }
                        var c = new Vector3(w.Value<float?>("x") ?? 0, w.Value<float?>("y") ?? 0, w.Value<float?>("z") ?? 0);
                        float rad = w.Value<float?>("radius") ?? 5;
                        var d = me - c; d.y *= 0.5f;   // height counts half: stairs and slopes
                        bool inside = d.magnitude <= rad;
                        if (inside != r.Inside) { r.Inside = inside; if (inside == (type == "enterArea")) Trigger(r, Pos(me)); }
                    }
                    else if (type == "flag")
                    {
                        bool on = Game.GetFlag(w.Value<int?>("flag") ?? 0) == (w.Value<bool?>("on") ?? true);
                        if (on && !r.LastBool) Trigger(r, new Dictionary<string, object> { { "flag", w.Value<int?>("flag") ?? 0 } });
                        r.LastBool = on;
                    }
                    else if (type == "item")
                    {
                        bool has = ItemCount(w.Value<int?>("item") ?? 0) >= (w.Value<int?>("count") ?? 1);
                        if (has && !r.LastBool) Trigger(r, new Dictionary<string, object> { { "item", w.Value<int?>("item") ?? 0 } });
                        r.LastBool = has;
                    }
                    else if (type == "timer")
                    {
                        if (!ZoneOk(w)) continue;
                        float every = Math.Max(1, w.Value<float?>("seconds") ?? 60);
                        if (r.NextTimer == 0) r.NextTimer = now + every;
                        else if (now >= r.NextTimer) { r.NextTimer = now + every; Trigger(r, new Dictionary<string, object>()); }
                    }
                }
            }

            // Running action lists (they can wait)
            for (int i = running.Count - 1; i >= 0; i--)
            {
                var run = running[i];
                while (run.Index < run.Steps.Count && now >= run.WaitUntil)
                {
                    var step = run.Steps[run.Index++] as JObject;
                    if (step == null) continue;
                    try { Do(step, run, now); }
                    catch (Exception e) { Log((run.Rule != null ? run.Rule.Key : "run") + ": " + step.Value<string>("type") + " failed: " + e.Message); }
                }
                if (run.Index >= run.Steps.Count && now >= run.WaitUntil) running.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------ events from the game
        /// <summary>Something happened: run the rules waiting for it, and Lua's on() handlers.</summary>
        internal void Fire(string type, Dictionary<string, object> e)
        {
            if (!loaded) return;
            foreach (var r in rules.Where(r => r.When.Value<string>("type") == type).ToList())
                if (Matches(r.When, type, e)) Trigger(r, e);
            foreach (var l in lua.Values) l.Fire(type, e);
        }

        static bool Matches(JObject w, string type, Dictionary<string, object> e)
        {
            Func<string, object> ev = k => e.ContainsKey(k) ? e[k] : null;
            switch (type)
            {
                case "zone": return string.IsNullOrEmpty(w.Value<string>("zone")) || w.Value<string>("zone") == (string)ev("zone");
                case "talk": return (w.Value<int?>("npc") ?? 0) == 0 || w.Value<int?>("npc") == Convert.ToInt32(ev("npc"));
                case "questAccepted":
                case "questDone": return (w.Value<int?>("quest") ?? 0) == 0 || w.Value<int?>("quest") == Convert.ToInt32(ev("quest"));
                case "kill":
                    var name = w.Value<string>("monster");
                    return string.IsNullOrEmpty(name) || string.Equals(name, (string)ev("monster"), StringComparison.OrdinalIgnoreCase)
                           || (w.Value<int?>("monsterId") ?? 0) == Convert.ToInt32(ev("monsterId") ?? 0);
            }
            return true;
        }

        void Trigger(Rule r, Dictionary<string, object> e)
        {
            float now = Time.realtimeSinceStartup;
            if (r.Cooldown > 0 && now - r.LastRun < r.Cooldown) return;
            if (r.Once && Done(r.Key)) return;
            foreach (var c in r.If.OfType<JObject>()) if (!Check(c)) return;
            r.LastRun = now;
            if (r.Once) MarkDone(r.Key);
            running.Add(new Run { Rule = r, Steps = r.Then, Event = e });
        }

        /// <summary>Run actions now (the editor's "Run now", and Lua's game.run).</summary>
        internal void RunSteps(JArray steps, string pack) { running.Add(new Run { Steps = steps, Event = new Dictionary<string, object>(), Rule = new Rule { Pack = pack, Key = pack + "/test" } }); }

        // ------------------------------------------------------------ conditions and actions
        static bool ZoneOk(JObject w) { var z = w.Value<string>("zone"); return string.IsNullOrEmpty(z) || z == Game.Zone; }

        bool Check(JObject c)
        {
            bool not = c.Value<bool?>("not") ?? false;
            bool ok;
            switch (c.Value<string>("type"))
            {
                case "flag": ok = Game.GetFlag(c.Value<int?>("flag") ?? 0) == (c.Value<bool?>("on") ?? true); break;
                case "item": ok = ItemCount(c.Value<int?>("item") ?? 0) >= (c.Value<int?>("count") ?? 1); break;
                case "zone": ok = c.Value<string>("zone") == Game.Zone; break;
                case "gold": ok = Game.Stats != null && Game.Stats.gold.read() >= (c.Value<long?>("amount") ?? 0); break;
                case "quest": ok = QuestController.getReceivedQuest(c.Value<int?>("quest") ?? 0) != null; break;
                default: ok = true; break;
            }
            return ok != not;
        }

        void Do(JObject s, Run run, float now)
        {
            switch (s.Value<string>("type"))
            {
                case "message": Messages.Show(Fill(s.Value<string>("text"), run.Event), s.Value<float?>("seconds") ?? 4f); break;
                case "wait": run.WaitUntil = now + Math.Max(0, s.Value<float?>("seconds") ?? 1); break;
                case "giveItem": Game.GiveItem(s.Value<int?>("item") ?? 0, s.Value<int?>("count") ?? 1); break;
                case "takeItem": TakeItem(s.Value<int?>("item") ?? 0, s.Value<int?>("count") ?? 1); break;
                case "gold": Game.GiveGold(s.Value<long?>("amount") ?? 0); break;
                case "exp": Game.GiveExp(s.Value<long?>("amount") ?? 0); break;
                case "setFlag": Game.SetFlag(s.Value<int?>("flag") ?? 0, s.Value<bool?>("on") ?? true); break;
                case "startQuest": { var q = QuestController.GetQuestCtrl(); if (q != null) q.questReceive(s.Value<int?>("quest") ?? 0); break; }
                case "teleport": Game.Teleport(new Vector3(s.Value<float?>("x") ?? 0, s.Value<float?>("y") ?? 0, s.Value<float?>("z") ?? 0)); break;
                case "showNpc": SetShown("ModNPC_" + s.Value<int?>("npc") + "_", true, s.Value<bool?>("show") ?? true); break;
                case "showThing": SetShown("WorldObj_" + s.Value<string>("thing"), false, s.Value<bool?>("show") ?? true); break;
                case "dialog": { var why = OpenDialog(s.Value<string>("dialog") ?? "", run.Rule != null ? run.Rule.Pack : null); if (why != null) Log(why); break; }
                case "hidePath": { var t = FindPath(s.Value<string>("path")); if (t != null) t.gameObject.SetActive(!(s.Value<bool?>("hide") ?? true)); break; }
                case "lua":
                {
                    LuaHost host;
                    if (!lua.TryGetValue(run.Rule != null ? run.Rule.Pack : "", out host)) { host = LuaHost.Create(run.Rule != null ? run.Rule.Pack : "inline", problems); if (host == null) break; lua[run.Rule.Pack] = host; }
                    host.RunSnippet(s.Value<string>("code") ?? "", run.Event);
                    break;
                }
            }
        }

        static string Fill(string text, Dictionary<string, object> e)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var s = Game.Stats;
            text = text.Replace("{name}", s != null ? s.dispName : "").Replace("{zone}", Game.Zone);
            if (e != null) foreach (var kv in e) text = text.Replace("{" + kv.Key + "}", Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            return text;
        }

        internal static int ItemCount(int id)
        {
            try { var bag = GUI_Bag.getGUIInstance(); return bag != null ? bag.actionDockCtrl.checkItemCount(id) : 0; } catch { return 0; }
        }

        internal static void TakeItem(int id, int count)
        {
            var q = QuestController.GetQuestCtrl();
            if (q != null) q.forceDeleteItemByID(id, count);
        }

        /// <summary>Show or hide our NPCs (ModNPC_…) or placed things (WorldObj_…), including hidden ones.</summary>
        internal static void SetShown(string name, bool prefix, bool show)
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
            {
                var g = (GameObject)o;
                if (!g.scene.IsValid()) continue;
                if (prefix ? g.name.StartsWith(name, StringComparison.Ordinal) : g.name == name) g.SetActive(show);
            }
        }

        /// <summary>A thing in the scene by its path (as the editor's World tab shows it).</summary>
        internal static Transform FindPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            IList<Transform> level = SceneManager.GetActiveScene().GetRootGameObjects().Select(g => g.transform).ToList();
            Transform cur = null;
            foreach (var part in path.Split('/'))
            {
                string name = part; int k = 0, h = part.LastIndexOf('#');
                if (h > 0 && int.TryParse(part.Substring(h + 1), out k)) name = part.Substring(0, h); else k = 0;
                cur = level.Where(t => t.name == name).Skip(k).FirstOrDefault();
                if (cur == null) return null;
                var kids = new List<Transform>();
                for (int i = 0; i < cur.childCount; i++) kids.Add(cur.GetChild(i));
                level = kids;
            }
            return cur;
        }

        static bool TryKey(string name, out KeyCode k)
        {
            k = KeyCode.None;
            if (string.IsNullOrEmpty(name)) return false;
            try { k = (KeyCode)Enum.Parse(typeof(KeyCode), name, true); return true; } catch { return false; }
        }

        static Dictionary<string, object> Pos(Vector3 p)
        {
            return new Dictionary<string, object> { { "x", Math.Round(p.x, 2) }, { "y", Math.Round(p.y, 2) }, { "z", Math.Round(p.z, 2) } };
        }

        // "once": remembered per character
        static string Character { get { return Universe.characterSessionKey ?? ""; } }
        bool Done(string key) { Dictionary<string, bool> d; return state.TryGetValue(Character, out d) && d.ContainsKey(key); }
        void MarkDone(string key)
        {
            Dictionary<string, bool> d;
            if (!state.TryGetValue(Character, out d)) state[Character] = d = new Dictionary<string, bool>();
            d[key] = true;
            try { File.WriteAllText(statePath, JsonConvert.SerializeObject(state, Formatting.Indented)); } catch { }
        }

        // ------------------------------------------------------------ live link (Nine Lives Editor)
        string Live(LiveRequest r)
        {
            switch (r.Path)
            {
                case "/scripts/reload": return "{\"ok\":true,\"message\":" + LiveLink.Quote(Reload()) + "}";
                case "/scripts/status":
                    return JsonConvert.SerializeObject(new Dictionary<string, object> {
                        { "loaded", loaded }, { "rules", rules.Select(x => x.Key).ToList() }, { "lua", lua.Keys.ToList() }, { "problems", problems },
                        { "running", running.Count }, { "dialogs", dialogs.Keys.ToList() }, { "dialogOpen", open == null ? null : open.Id },
                        { "lastTalk", lastTalk }, { "drawError", drawError } });
                case "/scripts/run":   // run a rule's actions now (body: the rule)
                {
                    if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";
                    var rule = JObject.Parse(r.Body);
                    var then = rule["then"] as JArray;
                    if (then == null) return "{\"error\":\"The rule has no actions.\"}";
                    var failed = (rule["if"] as JArray ?? new JArray()).OfType<JObject>().Where(c => !Check(c)).Select(c => c.Value<string>("type")).ToList();
                    RunSteps(then, r.Get("pack") ?? "editor");
                    return "{\"ok\":true,\"conditions\":" + (failed.Count == 0 ? "\"all met\"" : LiveLink.Quote("not met now: " + string.Join(", ", failed.ToArray()))) + "}";
                }
                case "/scripts/dialog":   // play a dialogue now (body: the dialogue, as in the editor)
                {
                    if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";
                    OpenRaw(JObject.Parse(r.Body), r.Get("pack") ?? "editor");
                    return "{\"ok\":true}";
                }
                case "/scripts/lua":   // the editor's Lua console (body: code)
                {
                    var pack = r.Get("pack") ?? "console";
                    LuaHost host;
                    if (!lua.TryGetValue(pack, out host))
                    {
                        var p = new List<string>();
                        host = LuaHost.Create(pack, p);
                        if (host == null) return "{\"error\":" + LiveLink.Quote(p.FirstOrDefault() ?? "Lua couldn't start") + "}";
                        lua[pack] = host;
                    }
                    var output = host.Console(r.Body);
                    return JsonConvert.SerializeObject(output);
                }
            }
            return "{\"error\":\"unknown request\"}";
        }
    }

    // ------------------------------------------------------------ hooks into the game
    [HarmonyPatch(typeof(CharacterSelector), nameof(CharacterSelector.OnMouseUpByDispatcher))]
    static class OnTalk
    {
        static readonly System.Reflection.FieldInfo Stats = AccessTools.Field(typeof(CharacterSelector), "statsSet");
        static void Postfix(CharacterSelector __instance)
        {
            var me = ScriptLoader.Instance; if (me == null) return;
            var st = Stats.GetValue(__instance) as CommonStats;
            if (st == null) { me.lastTalk = "clicked something with no stats"; return; }
            if (st == Game.Stats || st.getCID() <= 0) return;
            var dist = Vector3.Distance(st.transform.position, Game.PlayerPosition);
            me.lastTalk = st.dispName + " (" + st.getCID() + ") at " + dist.ToString("0.0") + " m";
            if (dist > 6f) { me.lastTalk += ": too far to talk"; return; }
            me.Fire("talk", new Dictionary<string, object> { { "npc", st.getCID() }, { "name", st.dispName } });
            bool opened = me.OpenForNpc(st.getCID());
            me.lastTalk += opened ? ": dialogue opened" : ": no dialogue for this NPC";
            if (opened) ModLoader.Log("ScriptLoader: talked to " + me.lastTalk);
        }
    }

    [HarmonyPatch(typeof(QuestController), nameof(QuestController.questReceive), new[] { typeof(int), typeof(bool) })]
    static class OnQuestAccepted
    {
        static void Postfix(int id_, bool __result)
        {
            if (__result && ScriptLoader.Instance != null) ScriptLoader.Instance.Fire("questAccepted", new Dictionary<string, object> { { "quest", id_ } });
        }
    }

    [HarmonyPatch(typeof(QuestController), nameof(QuestController.questClear))]
    static class OnQuestDone
    {
        static void Postfix(int id_, bool __result)
        {
            if (__result && ScriptLoader.Instance != null) ScriptLoader.Instance.Fire("questDone", new Dictionary<string, object> { { "quest", id_ } });
        }
    }

    [HarmonyPatch(typeof(QuestController), nameof(QuestController.onKill))]
    static class OnKill
    {
        static void Postfix(CommonStats stats_)
        {
            if (stats_ == null || ScriptLoader.Instance == null) return;
            ScriptLoader.Instance.Fire("kill", new Dictionary<string, object> { { "monster", stats_.dispName }, { "monsterId", stats_.getCID() }, { "dropId", stats_.itemDropGroupID } });
        }
    }
}
