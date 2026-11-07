using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace NPCLoader
{
    // ------------------------------------------------------------ JSON format
    /// <summary>An NPC pack: Mods\NPCs\&lt;name&gt;.json</summary>
    public class NpcPack
    {
        public string name;
        public string author;
        public string description;
        public List<NpcDef> npcs = new List<NpcDef>();
    }

    public class NpcDef
    {
        /// <summary>The NPC's id (cid). Quests use it as giver/ender, shops as their stock table. Use 90001 and up.</summary>
        public int id;
        public string name;
        /// <summary>Scene name, e.g. "Zone_CrimsonIsles".</summary>
        public string zone;
        public float x, y, z;
        /// <summary>Facing, in degrees.</summary>
        public float rotation;
        /// <summary>Id of an existing NPC in the same zone whose look (model, clothes, animations) is copied.</summary>
        public int template;
        /// <summary>quest (default), shop, alchemy, bank, share (shared bank), skillReset, none.</summary>
        public string role = "quest";
        public bool enabled = true;
        /// <summary>Sit instead of standing (only if the template has sitting animations).</summary>
        public bool sit;
    }

    [ModInfo("npcloader", "NPCLoader", "1.2.0", Author = "Nine Lives offline project",
        Description = "Adds custom NPCs (quest givers, vendors, alchemists) from JSON NPC packs in Mods\\NPCs.")]
    public class NPCLoader : Mod
    {
        public const int FirstId = 90001;
        static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        static string ToJson(object o) { return JsonConvert.SerializeObject(o, Formatting.Indented, Json); }

        string packsDir, myPackPath;
        readonly List<GameObject> spawned = new List<GameObject>();
        readonly Dictionary<int, int> spawnedTemplate = new Dictionary<int, int>();   // NPC id -> template it was copied from
        LiveLink live;

        // Re-apply id/name/role for a few seconds after spawning: the game's own start-up code runs on
        // the copy after we create it and could reset them.
        readonly List<KeyValuePair<CommonStats, NpcDef>> settle = new List<KeyValuePair<CommonStats, NpcDef>>();
        float spawnAt = -1, giveUpAt = -1, settleUntil = -1;

        public override void Load()
        {
            packsDir = Path.Combine(ModLoader.ModsDir, "NPCs");
            myPackPath = Path.Combine(packsDir, "my-npcs.json");
            Directory.CreateDirectory(packsDir);
            Commands.Register("npcadd", "npcadd <name> [template id] [role]", "add an NPC in front of you (saved to my-npcs)", NpcAdd);
            Commands.Register("npclist", "npclist", "list custom NPCs in this zone", a => NpcList());
            Commands.Register("npcmove", "npcmove <id>", "move one of your NPCs in front of you", NpcMove);
            Commands.Register("npcremove", "npcremove <id>", "delete one of your NPCs", NpcRemove);
            Commands.Register("npcreload", "npcreload", "reload NPC packs from Mods\\NPCs", a => Respawn());
            Commands.Register("npcnear", "npcnear", "list nearby NPCs you can use as a template", a => NpcNear());
            live = new LiveLink(HandleLive);
            string err;
            if (!live.Start(out err)) { Log("live link for the editor is off (port " + LiveLink.Port + ": " + err + ")"); live = null; }
            Log("ready - NPC packs go in Mods\\NPCs" + (live != null ? "; the Nine Lives Editor can show changes live" : ""));
        }

        public override void OnZoneLoaded(string zone)
        {
            // Wait for the player (OnPlayerSpawned): copying an NPC while the zone is still loading
            // its characters can stall the load.
            spawned.Clear(); spawnedTemplate.Clear(); settle.Clear();
            spawnAt = -1;
        }

        public override void OnPlayerSpawned()
        {
            spawnAt = Time.realtimeSinceStartup + 5f;
            giveUpAt = spawnAt + 60f;
        }

        /// <summary>Are all the templates this zone needs loaded and set up? (Copying a half-loaded NPC breaks it.)</summary>
        bool TemplatesReady()
        {
            foreach (var kv in ReadPacks(new List<string>()))
                foreach (var d in kv.Value.npcs)
                {
                    if (!d.enabled || d.zone != Game.Zone) continue;
                    var t = FindNpc(d.template);
                    if (t != null && t.gameObject.activeInHierarchy && (!t.initialized || !t.initializedCharacterModel)) return false;
                }
            return true;
        }

        public override void OnUpdate()
        {
            if (live != null) live.Pump();
            float now = Time.realtimeSinceStartup;
            if (spawnAt > 0 && now >= spawnAt)
            {
                if (Game.InWorld && !TemplatesReady() && now < giveUpAt) spawnAt = now + 1f;   // try again in a second
                else { spawnAt = -1; try { Log(Respawn()); } catch (Exception e) { Log("spawning failed: " + e); } }
            }
            if (settle.Count > 0)
            {
                foreach (var kv in settle) if (kv.Key != null) { Apply(kv.Key, kv.Value); FixModel(kv.Key); }
                if (now > settleUntil) settle.Clear();
            }
        }

        // ------------------------------------------------------------ packs
        List<KeyValuePair<string, NpcPack>> ReadPacks(List<string> problems)
        {
            var packs = new List<KeyValuePair<string, NpcPack>>();
            foreach (var f in Directory.GetFiles(packsDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var p = JsonConvert.DeserializeObject<NpcPack>(File.ReadAllText(f));
                    if (p != null && p.npcs != null) packs.Add(new KeyValuePair<string, NpcPack>(f, p));
                }
                catch (Exception e) { problems.Add(Path.GetFileName(f) + ": " + e.Message); }
            }
            return packs;
        }

        NpcPack MyPack()
        {
            if (File.Exists(myPackPath)) return JsonConvert.DeserializeObject<NpcPack>(File.ReadAllText(myPackPath)) ?? new NpcPack();
            return new NpcPack { name = "my-npcs", description = "NPCs placed in game with npcadd." };
        }

        void SaveMyPack(NpcPack p) { File.WriteAllText(myPackPath, ToJson(p)); }

        // ------------------------------------------------------------ spawning
        string Respawn()
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.Destroy(go);
            spawned.Clear(); spawnedTemplate.Clear(); settle.Clear();
            if (!Game.InWorld) return "Not in the world yet.";
            var problems = new List<string>();
            int made = 0;
            var seen = new HashSet<int>();
            foreach (var kv in ReadPacks(problems))
                foreach (var d in kv.Value.npcs)
                {
                    if (!d.enabled || d.zone != Game.Zone) continue;
                    if (!seen.Add(d.id)) { problems.Add("id " + d.id + " is used twice"); continue; }
                    string why = Spawn(d);
                    if (why == null) made++; else problems.Add(Path.GetFileName(kv.Key) + ": " + d.name + " (" + d.id + "): " + why);
                }
            settleUntil = Time.realtimeSinceStartup + 15f;
            foreach (var p in problems) Log(p);
            var s = made + " custom NPC(s) in " + Game.Zone;
            if (problems.Count > 0) s += "\n" + problems.Count + " problem(s): " + problems[0];
            return s;
        }

        static CommonStats FindNpc(int cid)
        {
            // Includes NPCs that are switched off right now (the game hides groups of NPCs by area),
            // but only ones in the loaded scene, not prefabs.
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s == null || !s.gameObject.scene.IsValid()) continue;
                if (s.getCID() == cid && s.gameObject.name.IndexOf("ModNPC_", StringComparison.Ordinal) < 0) return s;
            }
            return null;
        }

        string Spawn(NpcDef d)
        {
            if (d.id <= 0) return "needs an id";
            var tpl = FindNpc(d.template);
            if (tpl == null) return "template NPC " + d.template + " isn't in this zone (or is hidden right now)";
            if (tpl.gameObject.activeInHierarchy && (!tpl.initialized || !tpl.initializedCharacterModel)) return "template NPC " + d.template + " hasn't finished loading; try npcreload";
            var rot = Quaternion.Euler(0, d.rotation, 0);
            var spot = Ground(new Vector3(d.x, d.y, d.z));
            var go = (GameObject)UnityEngine.Object.Instantiate(tpl.gameObject, spot, rot);
            // Not under the template's parent: the game switches whole groups of NPCs on and off by
            // area (ActiveControllByTrigger), so a copy placed elsewhere would be hidden next to you.
            // The world root is always on, and the game's AI needs a parent (it uses this one too).
            var root = Universe.GetWorldRootCtrl();
            go.transform.parent = root != null ? root.transform : tpl.transform.parent;
            go.name = "ModNPC_" + d.id + "_" + d.name;
            // Templates may be shown or hidden by quest progress or area: the copy should always be there.
            foreach (var fc in go.GetComponentsInChildren<FlagChecker>(true)) UnityEngine.Object.Destroy(fc);
            foreach (var fc in go.GetComponentsInChildren<FlagCheckToAction>(true)) UnityEngine.Object.Destroy(fc);
            foreach (var ac in go.GetComponentsInChildren<ActiveControllByTrigger>(true)) UnityEngine.Object.Destroy(ac);
            StripModel(go, tpl);
            var anim = go.GetComponent<CharacterAnimationController>();
            if (anim != null) anim.isSitdown = d.sit;   // the template may be sitting on a bench
            var cs = go.GetComponent<CommonStats>();
            if (cs != null) cs.isSitDown = d.sit;
            go.SetActive(true);
            var stats = go.GetComponent<CommonStats>();
            Apply(stats, d);
            spawned.Add(go);
            spawnedTemplate[d.id] = d.template;
            settle.Add(new KeyValuePair<CommonStats, NpcDef>(stats, d));
            return null;
        }

        /// <summary>Stand on whatever is below (a platform, steps): the pack's height may be a little off.</summary>
        static Vector3 Ground(Vector3 spot)
        {
            RaycastHit hit;
            if (Physics.Raycast(spot + Vector3.up * 3f, Vector3.down, out hit, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.05f;
            return spot;
        }

        GameObject SpawnedNpc(int id)
        {
            spawned.RemoveAll(g => g == null);
            return spawned.FirstOrDefault(g => g.name.StartsWith("ModNPC_" + id + "_", StringComparison.Ordinal));
        }

        void Despawn(int id)
        {
            var go = SpawnedNpc(id);
            if (go != null) { spawned.Remove(go); UnityEngine.Object.Destroy(go); }
            spawnedTemplate.Remove(id);
            settle.RemoveAll(kv => kv.Value.id == id);
        }

        // ------------------------------------------------------------ live link (Nine Lives Editor)
        /// <summary>Runs on the main thread. Shows editor changes in the world right away (not saved).</summary>
        string HandleLive(string method, string path, string query, string body)
        {
            if (path == "/status")
            {
                if (!Game.InWorld) return "{\"inWorld\":false}";
                var t = Game.Player.transform;
                return JsonConvert.SerializeObject(new Dictionary<string, object> {
                    { "inWorld", true }, { "zone", Game.Zone }, { "x", R(t.position.x) }, { "y", R(t.position.y) }, { "z", R(t.position.z) },
                    { "facing", R(t.eulerAngles.y) }, { "custom", spawned.Where(g => g != null).Count() } });
            }
            if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";

            if (path == "/npcs")   // everyone in this zone, nearest first
            {
                var me = Game.PlayerPosition;
                var seen = new HashSet<int>();
                var list = new List<Dictionary<string, object>>();
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
                {
                    var c = (CommonStats)o;
                    if (c == null || c == Game.Stats || !c.gameObject.scene.IsValid() || c.getCID() <= 0) continue;
                    bool mine = c.gameObject.name.StartsWith("ModNPC_", StringComparison.Ordinal);
                    if (!seen.Add(c.getCID())) continue;
                    var p = c.transform.position;
                    list.Add(new Dictionary<string, object> {
                        { "cid", c.getCID() }, { "name", c.dispName }, { "x", R(p.x) }, { "y", R(p.y) }, { "z", R(p.z) },
                        { "rotation", R(c.transform.eulerAngles.y) }, { "role", c.selectedGUILink.ToString() }, { "custom", mine },
                        { "shown", c.gameObject.activeInHierarchy }, { "distance", R(Vector3.Distance(p, me)) } });
                }
                return JsonConvert.SerializeObject(new Dictionary<string, object> { { "zone", Game.Zone }, { "npcs", list.OrderBy(n => (float)n["distance"]).ToList() } });
            }

            if (path == "/here")   // a spot in front of the player, facing them
            {
                var t = Game.Player.transform;
                var pos = Ground(t.position + t.forward * 2.5f);
                return JsonConvert.SerializeObject(new Dictionary<string, object> {
                    { "zone", Game.Zone }, { "x", R(pos.x) }, { "y", R(pos.y) }, { "z", R(pos.z) }, { "rotation", R((t.eulerAngles.y + 180f) % 360f) } });
            }

            if (path == "/goto" && method == "POST")   // stand in front of a spot, where an NPC there would face you
            {
                var d = JsonConvert.DeserializeObject<NpcDef>(body);
                var at = new Vector3(d.x, d.y, d.z);
                var front = Quaternion.Euler(0, d.rotation, 0) * Vector3.forward;
                Game.Teleport(Ground(at + front * 3f));
                return "{\"ok\":true}";
            }

            if (path == "/npc" && method == "POST")   // show this NPC as it is in the editor now
            {
                var d = JsonConvert.DeserializeObject<NpcDef>(body);
                if (d == null || d.id <= 0) return "{\"error\":\"needs an id\"}";
                if (d.zone != Game.Zone || !d.enabled) { Despawn(d.id); return "{\"ok\":true,\"shown\":false}"; }
                var go = SpawnedNpc(d.id);
                int tpl;
                if (go != null && spawnedTemplate.TryGetValue(d.id, out tpl) && tpl == d.template)
                {
                    // Same look: just move, turn and rename it (no reload, no flicker).
                    var spot = Ground(new Vector3(d.x, d.y, d.z));
                    go.transform.position = spot;
                    go.transform.rotation = Quaternion.Euler(0, d.rotation, 0);
                    go.name = "ModNPC_" + d.id + "_" + d.name;
                    var ai = go.GetComponent<AI_Base>();
                    if (ai != null) try { Traverse.Create(ai).Field("paramSet").Field("homePosition").SetValue(spot); } catch { }   // or it walks back
                    var st = go.GetComponent<CommonStats>();
                    if (st != null) Apply(st, d);
                    settle.RemoveAll(kv => kv.Value.id == d.id);
                    if (st != null) settle.Add(new KeyValuePair<CommonStats, NpcDef>(st, d));
                    settleUntil = Math.Max(settleUntil, Time.realtimeSinceStartup + 5f);
                    return "{\"ok\":true,\"shown\":true}";
                }
                Despawn(d.id);
                var why = Spawn(d);
                settleUntil = Time.realtimeSinceStartup + 15f;
                return why == null ? "{\"ok\":true,\"shown\":true}" : "{\"ok\":false,\"error\":" + LiveLink.Quote(why) + "}";
            }

            if (path == "/remove" && method == "POST")
            {
                int id; int.TryParse(LiveLink.QueryValue(query, "id") ?? "", out id);
                Despawn(id);
                return "{\"ok\":true}";
            }

            if (path == "/reload" && method == "POST") return "{\"ok\":true,\"message\":" + LiveLink.Quote(Respawn()) + "}";
            return "{\"error\":\"unknown request\"}";
        }

        // The game loads an NPC's model 100 km away and moves it into place when it's ready. A copy
        // inherits the template's finished model and also loads its own, and the move goes wrong:
        // one ends up 100 km up, the other 100 km down. Keep the model the NPC uses (controllTarget),
        // put it back at the NPC's feet, and delete the spare.
        /// <summary>
        /// Take the template's finished body and the parts the game rebuilds at startup (sound pack,
        /// buff racks, hair controller) off the copy, so it loads its own body like a newly placed
        /// NPC. Otherwise the copy ends up with two bodies, animation wired to the wrong one.
        /// </summary>
        static readonly string[] RebuiltStats = {
            "CCResistance", "attackRange", "attackpower", "avoidance", "bowLoadSpeed", "bowRange", "breath", "castSpeed",
            "critDamage", "critRate", "damage", "defense", "dexterity", "health", "healthLeech", "healthRecovery", "jumpPower",
            "lootRange", "mana", "manaLeech", "mass", "missileRangeAmp", "mountMoveSpeed", "moveSpeed", "regenerate",
            "resistance", "sightRange", "slipdownSpeed", "swimSpeedAmp", "talkRange", "walkSpeedAmp", "weaponDamage",
            "weaponDamage_off", "weaponSpeed", "weaponSpeed_off", "willpower" };

        static void StripModel(GameObject go, CommonStats tpl)
        {
            var st = go.GetComponent<CommonStats>();
            string modelName = tpl.controllTarget != null ? tpl.controllTarget.name : null;
            // The selector's portrait camera hangs off the body once an NPC is loaded: move it back
            // to the selector first, or deleting the body takes it along and clicking breaks.
            // "Already registered with the mouse system" is copied from the template as true, so the
            // copy would never register and clicks on it would go nowhere.
            foreach (var sb in go.GetComponentsInChildren<SelectorBase>(true)) sb.isEventLinked = false;
            var faceCam = AccessTools.Field(typeof(CharacterSelector), "faceCamera");
            foreach (var sel in go.GetComponentsInChildren<CharacterSelector>(true))
            {
                var cam = faceCam == null ? null : faceCam.GetValue(sel) as Transform;
                if (cam != null) cam.parent = sel.transform;
            }
            var doomed = new List<GameObject>();
            foreach (Transform c in go.transform)
                if ((modelName != null && c.name == modelName) || c.name.StartsWith("_soundPack") ||
                    c.name == "_buffRack" || c.name == "_debuffRack" || c.name == "_skillRack")
                    doomed.Add(c.gameObject);
            foreach (var d in doomed) UnityEngine.Object.DestroyImmediate(d);
            foreach (var h in go.GetComponents<HairController>()) UnityEngine.Object.DestroyImmediate(h);
            if (st != null)
            {
                st.controllTarget = null;
                st.initialized = false;
                st.initializedCharacterModel = false;
            }
            var init = go.GetComponent<InitSetup>();
            if (init != null) { init.controllTarget = null; init.initialized = false; }
            // Stats are copied marked "set up" but with their values zeroed (talk range 0 = can't talk).
            // Clear the ones the game rebuilds at startup so it rebuilds them properly.
            if (st != null)
                foreach (var name in RebuiltStats)
                {
                    var f = AccessTools.Field(typeof(CommonStats), name);
                    if (f != null) f.SetValue(st, null);
                }
            // More copied "already done" flags: the copy's equipment would never dress the new body.
            foreach (var eq in go.GetComponents<ItemEquiperBase>()) eq.initialized = false;
            if (st != null) st.firstEquiped = false;
        }

        static readonly FieldInfo SelectorReady = AccessTools.Field(typeof(CharacterSelector), "initPortrait");

        static void FixModel(CommonStats s)
        {
            if (s == null || !s.initialized) return;
            // Clicking an NPC only works once its selector heard "stats initialized". A copy's selector
            // can start before that's possible and never hears it, so tell it ourselves.
            foreach (var sel in s.GetComponentsInChildren<CharacterSelector>(true))
                if (SelectorReady != null && !(bool)SelectorReady.GetValue(sel))
                    try { sel.onCommonStatsInitialized(s); } catch (Exception e) { ModLoader.Log("NPCLoader: selector: " + e.Message); }
            if (!s.initializedCharacterModel || s.controllTarget == null) return;
            var keep = s.controllTarget;
            if (keep.parent == s.transform && keep.localPosition.sqrMagnitude > 1000f * 1000f) keep.localPosition = Vector3.zero;
            var spares = new List<Transform>();
            foreach (Transform c in s.transform) if (c != keep && c.name == keep.name) spares.Add(c);
            foreach (var c in spares) UnityEngine.Object.Destroy(c.gameObject);
        }

        static void Apply(CommonStats s, NpcDef d)
        {
            s.isSitDown = d.sit;
            var anim = s.GetComponent<CharacterAnimationController>();
            if (anim != null) anim.isSitdown = d.sit;
            s.cid = d.id.ToString(CultureInfo.InvariantCulture);
            s.setCID(d.id);
            if (!string.IsNullOrEmpty(d.name)) s.dispName = d.name;
            SelectedGUILinkType role;
            s.selectedGUILink = TryRole(d.role, out role) ? role : SelectedGUILinkType.quest;
        }

        static bool TryRole(string r, out SelectedGUILinkType role)
        {
            role = SelectedGUILinkType.quest;
            if (string.IsNullOrEmpty(r)) return true;
            try { role = (SelectedGUILinkType)Enum.Parse(typeof(SelectedGUILinkType), r, true); return true; }
            catch { return false; }
        }

        // ------------------------------------------------------------ commands
        string NpcAdd(string[] a)
        {
            if (!Game.InWorld) return "You need to be in the world.";
            if (a.Length == 0) throw new ArgumentException("npcadd <name> [template id] [role]   e.g. npcadd Old Bram 250 quest");
            // Trailing words that are a number / a role are the template and role; the rest is the name.
            var words = a.ToList();
            string role = "quest";
            SelectedGUILinkType tmp;
            int tpl = 0, n;
            if (words.Count > 1 && !int.TryParse(words[words.Count - 1], out n) && TryRole(words[words.Count - 1], out tmp) && IsRoleWord(words[words.Count - 1]))
            { role = words[words.Count - 1].ToLowerInvariant(); words.RemoveAt(words.Count - 1); }
            if (words.Count > 1 && int.TryParse(words[words.Count - 1], out n)) { tpl = n; words.RemoveAt(words.Count - 1); }
            var name = string.Join(" ", words.ToArray());
            var player = Game.Player.transform;
            if (tpl == 0)
            {
                var near = Nearby(player.position, 1).FirstOrDefault();
                if (near == null) return "No NPC nearby to copy. Give a template id (see npcnear).";
                tpl = near.getCID();
            }
            if (FindNpc(tpl) == null) return "NPC " + tpl + " isn't in this zone. Try npcnear.";

            var pack = MyPack();
            var all = new List<int>();
            foreach (var kv in ReadPacks(new List<string>())) all.AddRange(kv.Value.npcs.Select(x => x.id));
            int id = Math.Max(FirstId, all.Count > 0 ? all.Max() + 1 : FirstId);
            var pos = player.position + player.forward * 2.5f;
            var d = new NpcDef { id = id, name = name, zone = Game.Zone, x = R(pos.x), y = R(player.position.y), z = R(pos.z),
                                 rotation = R((player.eulerAngles.y + 180f) % 360f), template = tpl, role = role };
            pack.npcs.Add(d);
            SaveMyPack(pack);
            string why = Spawn(d);
            settleUntil = Time.realtimeSinceStartup + 15f;
            return why != null ? "Saved, but couldn't show it: " + why
                               : "Added \"" + name + "\" (id " + id + ", copy of " + tpl + ", " + role + "). Quests can use giver/ender " + id + ".";
        }

        static bool IsRoleWord(string w)
        {
            var l = w.ToLowerInvariant();
            return l == "quest" || l == "shop" || l == "alchemy" || l == "bank" || l == "share" || l == "skillreset" || l == "none";
        }

        string NpcMove(string[] a)
        {
            if (!Game.InWorld) return "You need to be in the world.";
            int id; if (a.Length == 0 || !int.TryParse(a[0], out id)) throw new ArgumentException("npcmove <id>");
            var pack = MyPack();
            var d = pack.npcs.FirstOrDefault(x => x.id == id);
            if (d == null) return "NPC " + id + " isn't in my-npcs.json (edit other packs by hand).";
            var p = Game.Player.transform; var pos = p.position + p.forward * 2.5f;
            d.zone = Game.Zone; d.x = R(pos.x); d.y = R(p.position.y); d.z = R(pos.z); d.rotation = R((p.eulerAngles.y + 180f) % 360f);
            SaveMyPack(pack);
            return Respawn();
        }

        string NpcRemove(string[] a)
        {
            int id; if (a.Length == 0 || !int.TryParse(a[0], out id)) throw new ArgumentException("npcremove <id>");
            var pack = MyPack();
            int n = pack.npcs.RemoveAll(x => x.id == id);
            if (n == 0) return "NPC " + id + " isn't in my-npcs.json (edit other packs by hand).";
            SaveMyPack(pack);
            return "Removed " + id + ". " + Respawn();
        }

        string NpcList()
        {
            var lines = new List<string>();
            foreach (var kv in ReadPacks(new List<string>()))
                foreach (var d in kv.Value.npcs.Where(x => x.zone == Game.Zone))
                    lines.Add(d.id + "  " + d.name + "  (" + d.role + ", copy of " + d.template + ", " + Path.GetFileNameWithoutExtension(kv.Key) + ")");
            return lines.Count == 0 ? "No custom NPCs in this zone. Add one with npcadd <name>." : string.Join("\n", lines.Take(9).ToArray());
        }

        string NpcNear()
        {
            if (!Game.InWorld) return "You need to be in the world.";
            var list = Nearby(Game.PlayerPosition, 8);
            if (list.Count == 0) return "No NPCs nearby.";
            return string.Join("\n", list.Select(s => s.getCID() + "  " + s.dispName + "  " + s.selectedGUILink + "  " +
                Mathf.RoundToInt(Vector3.Distance(s.transform.position, Game.PlayerPosition)) + " m").ToArray());
        }

        static List<CommonStats> Nearby(Vector3 pos, int count)
        {
            var l = new List<CommonStats>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s == null || s.getCID() <= 0 || s == Game.Stats || s.gameObject.name.StartsWith("ModNPC_")) continue;
                l.Add(s);
            }
            return l.OrderBy(s => (s.transform.position - pos).sqrMagnitude).Take(count).ToList();
        }

        static float R(float v) { return (float)Math.Round(v, 2); }
    }
}
