using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace WorldLoader
{
    public class WorldPack
    {
        public string name;
        public string author;
        public string description;
        public List<WorldObject> objects = new List<WorldObject>();
    }

    public class WorldObject
    {
        /// <summary>Any text, unique in the pack ("house-1").</summary>
        public string id;
        public string name;
        /// <summary>Scene name, e.g. "Zone_CrimsonIsles".</summary>
        public string zone;
        /// <summary>Path of the thing copied (or hidden), as the editor's Pick shows it.</summary>
        public string source;
        /// <summary>Zone the source is in; empty = the same zone.</summary>
        public string sourceZone;
        public float x, y, z;
        /// <summary>Rotation in degrees around X (tilt), Y (turn) and Z (roll).</summary>
        public float rx, ry, rz;
        public float scale = 1;
        /// <summary>Hide the source instead of placing a copy (e.g. a tree in the way).</summary>
        public bool hide;
        /// <summary>Copy everything (doors, triggers…), not just the look and collision.</summary>
        public bool keepScripts;
        public bool enabled = true;
    }

    [ModInfo("worldloader", "WorldLoader", "1.0.1", Author = "Nine Lives offline project",
        Description = "Place copies of houses, fences, trees and props, or hide things, from world packs in Mods\\World.")]
    public class WorldLoader : Mod
    {
        string packsDir;
        readonly Dictionary<string, GameObject> copies = new Dictionary<string, GameObject>();       // object id -> copy
        readonly Dictionary<string, string> copySource = new Dictionary<string, string>();          // object id -> source|keepScripts
        readonly Dictionary<string, GameObject> hidden = new Dictionary<string, GameObject>();      // object id -> thing hidden
        float spawnAt = -1;
        readonly List<KeyValuePair<float, Material[]>> glows = new List<KeyValuePair<float, Material[]>>();

        public override void Load()
        {
            packsDir = Path.Combine(ModLoader.ModsDir, "World");
            Directory.CreateDirectory(packsDir);
            Commands.Register("worldreload", "worldreload", "reload world packs from Mods\\World", a => Reload());
            LiveLink.Route("/world/", Live);
            Log("ready - world packs go in Mods\\World");
        }

        public override void OnZoneLoaded(string zone) { copies.Clear(); copySource.Clear(); hidden.Clear(); spawnAt = -1; }
        public override void OnPlayerSpawned() { spawnAt = Time.realtimeSinceStartup + 3f; }

        public override void OnUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (spawnAt > 0 && now >= spawnAt) { spawnAt = -1; try { Log(Reload()); } catch (Exception e) { Log("loading world packs failed: " + e); } }
            for (int i = glows.Count - 1; i >= 0; i--)
                if (now > glows[i].Key) { try { EffectController.clearForcusEffect(glows[i].Value); } catch { } glows.RemoveAt(i); }
            // A copy of baked scenery still uses the original's "hidden behind something" data, so it would
            // fade in and out as you turn. While this zone has such copies, the cameras skip that check.
            if (now >= nextBakedCheck) { nextBakedCheck = now + 1f; anyMovedBaked = copies.Values.Any(g => g != null && Copier.HasMovedBakedParts(g)); }
            bool want = occlusionMode == 1 || (occlusionMode == 0 && anyMovedBaked);
            foreach (var cam in Camera.allCameras)
            {
                if (want && cam.useOcclusionCulling) { cam.useOcclusionCulling = false; occlusionOff.Add(cam); }
                else if (!want && occlusionOff.Contains(cam)) { cam.useOcclusionCulling = true; occlusionOff.Remove(cam); }
            }
            occlusionOff.RemoveWhere(c => c == null);
        }

        float nextBakedCheck; bool anyMovedBaked;
        int occlusionMode;   // 0 = automatic, 1 = always off, 2 = leave the game's setting
        readonly HashSet<Camera> occlusionOff = new HashSet<Camera>();

        // ------------------------------------------------------------ packs
        IEnumerable<WorldObject> Objects(List<string> problems)
        {
            foreach (var f in Directory.GetFiles(packsDir, "*.json").OrderBy(f => f))
            {
                WorldPack p = null;
                try { p = JsonConvert.DeserializeObject<WorldPack>(File.ReadAllText(f)); }
                catch (Exception e) { problems.Add(Path.GetFileName(f) + ": " + e.Message); }
                if (p != null && p.objects != null) foreach (var o in p.objects) yield return o;
            }
        }

        string Reload()
        {
            foreach (var g in copies.Values) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in hidden.Values) if (g != null) g.SetActive(true);
            copies.Clear(); copySource.Clear(); hidden.Clear();
            if (!Game.InWorld) return "Not in the world yet.";
            var problems = new List<string>();
            int made = 0, hid = 0;
            foreach (var o in Objects(problems))
            {
                if (!o.enabled || o.zone != Game.Zone || string.IsNullOrEmpty(o.id)) continue;
                var why = Show(o);
                if (why != null) problems.Add((o.name ?? o.id) + ": " + why);
                else if (o.hide) hid++; else made++;
            }
            foreach (var p in problems) Log(p);
            return "world: " + made + " placed, " + hid + " hidden in " + Game.Zone + (problems.Count > 0 ? "; " + problems.Count + " problem(s): " + problems[0] : "");
        }

        /// <summary>Put an object in the world as described (replacing what was there for its id).</summary>
        string Show(WorldObject o)
        {
            if (!string.IsNullOrEmpty(o.sourceZone) && o.sourceZone != Game.Zone)
                return "copying from another zone (" + o.sourceZone + ") isn't supported yet";
            var src = Copier.Find(o.source);
            if (src == null) return "can't find \"" + o.source + "\" in this zone";

            if (o.hide)
            {
                Remove(o.id);
                hidden[o.id] = src.gameObject;
                src.gameObject.SetActive(false);
                return null;
            }

            GameObject go;
            var key = o.source + "|" + o.keepScripts;
            string had;
            if (copies.TryGetValue(o.id, out go) && go != null && copySource.TryGetValue(o.id, out had) && had == key)
            {
                Place(go, o);   // same thing: just move it (no rebuild, no flicker)
                return null;
            }
            Remove(o.id);
            Copier.Report rep;
            go = Copier.MakeCopy(src, o.keepScripts, out rep);
            go.name = "WorldObj_" + o.id;
            Place(go, o);
            copies[o.id] = go; copySource[o.id] = key;
            if (rep.missing > 0) Log(o.id + ": " + rep.Text());
            if (rep.missing > 0 && rep.missing == rep.batched && rep.batched == rep.renderers)
                return "the game's pre-baked look of this can't be copied (try a bigger or smaller part, or keepScripts)";
            return null;
        }

        static void Place(GameObject go, WorldObject o)
        {
            go.transform.position = new Vector3(o.x, o.y, o.z);
            go.transform.rotation = Quaternion.Euler(o.rx, o.ry, o.rz);
            go.transform.localScale = Vector3.one * (o.scale > 0 ? o.scale : 1);
        }

        void Remove(string id)
        {
            GameObject g;
            if (copies.TryGetValue(id, out g)) { if (g != null) UnityEngine.Object.Destroy(g); copies.Remove(id); copySource.Remove(id); }
            if (hidden.TryGetValue(id, out g)) { if (g != null) g.SetActive(true); hidden.Remove(id); }
        }

        void Glow(Transform t)
        {
            try { glows.Add(new KeyValuePair<float, Material[]>(Time.realtimeSinceStartup + 2.5f, EffectController.forcusEffect(t, ColorDefine.CharacterSelector_intensity))); }
            catch { }
        }

        // ------------------------------------------------------------ live link (Nine Lives Editor)
        static string F(float v) { return Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture); }

        static Dictionary<string, object> Describe(Transform t, Vector3 from)
        {
            var b = Copier.BoundsOf(t);
            var rs = t.GetComponentsInChildren<MeshRenderer>(true);
            return new Dictionary<string, object> {
                { "path", Copier.PathOf(t) }, { "name", t.name }, { "size", Math.Round(b.size.magnitude, 1) },
                { "x", Math.Round(t.position.x, 2) }, { "y", Math.Round(t.position.y, 2) }, { "z", Math.Round(t.position.z, 2) },
                { "ry", Math.Round(t.eulerAngles.y, 1) }, { "parts", rs.Length }, { "baked", rs.Count(r => r.isPartOfStaticBatch) },
                { "children", t.childCount }, { "shown", t.gameObject.activeInHierarchy },
                { "distance", Math.Round(Vector3.Distance(b.center, from), 1) }, { "character", Copier.IsCharacter(t) } };
        }

        /// <summary>A sensible "whole thing" above a hit part: stop below big group folders.</summary>
        static int Suggest(List<Transform> chain)
        {
            for (int i = 0; i < chain.Count - 1; i++)
            {
                var parent = chain[i + 1];
                if (parent.name.StartsWith("_") || parent.childCount > 12 || parent.parent == null) return i;
            }
            return chain.Count - 1;
        }

        string Live(LiveRequest r)
        {
            if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";
            var me = Game.PlayerPosition;
            switch (r.Path)
            {
                case "/world/pick":   // what's in the middle of the game's view
                {
                    var cam = Camera.main;
                    if (cam == null) return "{\"error\":\"No camera.\"}";
                    var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
                    var player = Game.Player.transform;
                    var hits = Physics.RaycastAll(ray, 400f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                                      .Where(h => !h.transform.IsChildOf(player)).OrderBy(h => h.distance).ToList();
                    if (hits.Count == 0) return "{\"error\":\"Nothing in the middle of the screen. Turn the camera to look at it.\"}";
                    var chain = new List<Transform>();
                    for (var t = hits[0].transform; t != null; t = t.parent) chain.Add(t);
                    Glow(chain[Suggest(chain)]);
                    return JsonConvert.SerializeObject(new Dictionary<string, object> {
                        { "zone", Game.Zone }, { "suggested", Suggest(chain) }, { "hit", new[] { F(hits[0].point.x), F(hits[0].point.y), F(hits[0].point.z) } },
                        { "chain", chain.Select(t => Describe(t, me)).ToList() } });
                }
                case "/world/near":   // things around the player
                {
                    float radius = 20; float.TryParse(r.Get("radius") ?? "20", out radius);
                    var found = new Dictionary<Transform, bool>();
                    foreach (var c in Physics.OverlapSphere(me, Mathf.Clamp(radius, 2, 80)))
                    {
                        if (c.isTrigger || c.transform.IsChildOf(Game.Player.transform) || Copier.IsCharacter(c.transform)) continue;
                        var chain = new List<Transform>();
                        for (var t = c.transform; t != null; t = t.parent) chain.Add(t);
                        found[chain[Suggest(chain)]] = true;
                    }
                    var list = found.Keys.Select(t => Describe(t, me)).OrderBy(d => (double)d["distance"]).Take(60).ToList();
                    return JsonConvert.SerializeObject(new Dictionary<string, object> { { "zone", Game.Zone }, { "things", list } });
                }
                case "/world/describe":   // one path, with its parts (to go up or down a level)
                {
                    var t = Copier.Find(r.Get("path"));
                    if (t == null) return "{\"error\":\"Not found in this zone.\"}";
                    var kids = new List<Dictionary<string, object>>();
                    for (int i = 0; i < t.childCount && i < 80; i++) kids.Add(Describe(t.GetChild(i), me));
                    var d = Describe(t, me);
                    d["parent"] = t.parent != null ? Copier.PathOf(t.parent) : null;
                    d["kids"] = kids;
                    return JsonConvert.SerializeObject(d);
                }
                case "/world/highlight":
                {
                    var t = Copier.Find(r.Get("path"));
                    if (t == null) { GameObject g; if (copies.TryGetValue(r.Get("id") ?? "", out g) && g != null) t = g.transform; }
                    if (t == null) return "{\"error\":\"Not found in this zone.\"}";
                    Glow(t);
                    return "{\"ok\":true}";
                }
                case "/world/ground":   // the ground under a spot
                {
                    var p = LiveLink.Ground(new Vector3(LiveLink.Num(r.Body, "x"), LiveLink.Num(r.Body, "y") + 2f, LiveLink.Num(r.Body, "z")));
                    return "{\"y\":" + F(p.y - 0.05f) + "}";
                }
                case "/world/obj":
                {
                    var o = JsonConvert.DeserializeObject<WorldObject>(r.Body);
                    if (o == null || string.IsNullOrEmpty(o.id)) return "{\"error\":\"needs an id\"}";
                    if (!o.enabled || o.zone != Game.Zone) { Remove(o.id); return "{\"ok\":true,\"shown\":false}"; }
                    var why = Show(o);
                    return why == null ? "{\"ok\":true,\"shown\":true}" : "{\"ok\":false,\"error\":" + LiveLink.Quote(why) + "}";
                }
                case "/world/drawdebug":   // copied baked parts: where Unity is told to draw them
                    return JsonConvert.SerializeObject(copies.Values.Where(g => g != null).SelectMany(g => g.GetComponentsInChildren<MeshRenderer>(true))
                        .Where(r => r.isPartOfStaticBatch).Select(r => new Dictionary<string, object> {
                            { "object", r.name }, { "copy", r.transform.root.name }, { "visible", r.isVisible }, { "enabled", r.enabled },
                            { "anchor", Copier.BatchRootOf(r) }, { "bounds", r.bounds.center + " " + r.bounds.size }, { "copyAt", r.transform.position.ToString() } }).ToList());
                case "/world/occlusion":   // test switch: auto | off | game
                {
                    var m = r.Get("mode") ?? "auto";
                    occlusionMode = m == "off" ? 1 : m == "game" ? 2 : 0;
                    return "{\"ok\":true,\"mode\":" + LiveLink.Quote(m) + ",\"cameras\":" + Camera.allCameras.Length + "}";
                }
                case "/world/remove": Remove(r.Get("id") ?? ""); return "{\"ok\":true}";
                case "/world/reload": return "{\"ok\":true,\"message\":" + LiveLink.Quote(Reload()) + "}";
            }
            return "{\"error\":\"unknown request\"}";
        }
    }
}
