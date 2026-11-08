using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace ContentLoader
{
    /// <summary>
    /// export\monsters.json: every monster kind met in each zone, with its id, base stats, the places
    /// it has been seen and a picture (export\monsters\*.png), so tools can list "the enemies of a zone".
    /// </summary>
    static class Monsters
    {
        const int Grid = 5, MaxSpots = 400, PicSize = 256;
        static readonly Queue<KeyValuePair<CommonStats, string>> pending = new Queue<KeyValuePair<CommonStats, string>>();
        static string lastJson;
        static int whyLogged;
        static void Why(CommonStats s, string why) { if (whyLogged++ < 12) ModLoader.Log("ContentLoader: no picture of " + s.dispName + " yet: " + why + " (" + Mathf.RoundToInt((s.transform.position - Game.PlayerPosition).magnitude) + " m away)"); }

        static string Path_ { get { return Path.Combine(ContentLoader.ExportDir, "monsters.json"); } }
        static string PicDir { get { return Path.Combine(ContentLoader.ExportDir, "monsters"); } }

        /// <summary>A file name from the monster's name: m_ and 8 hex digits.</summary>
        static string PicName(string name)
        {
            uint h = 2166136261;
            foreach (var c in name ?? "") { h ^= c; h *= 16777619; }
            return "m_" + h.ToString("x8");
        }

        public static void Scan(Func<object, string> toJson)
        {
            var known = new Dictionary<string, MonsterInfo>();
            if (File.Exists(Path_))
            {
                try { foreach (var m in JsonConvert.DeserializeObject<List<MonsterInfo>>(File.ReadAllText(Path_)) ?? new List<MonsterInfo>()) if (m != null) known[m.zone + "|" + m.name + "|" + m.dropId] = m; }
                catch { }   // damaged: start a fresh list
            }
            var zone = Game.Zone;
            var me = Game.Stats;
            var here = Game.PlayerPosition;
            var counted = new Dictionary<string, int>();
            var nearest = new Dictionary<string, CommonStats>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s == null || s == me || s.itemDropGroupID <= 0 || s.isMount || s.isMinion || string.IsNullOrEmpty(s.dispName)) continue;
                if (Spawned(s)) continue;   // EnemyLoader's spawns: tweaked, not the game's own
                var own = OwnName(s);       // EnemyLoader renames: listed under the game's name
                var key = zone + "|" + own + "|" + s.itemDropGroupID;
                MonsterInfo m;
                if (!known.TryGetValue(key, out m)) known[key] = m = new MonsterInfo { name = own, zone = zone, dropId = s.itemDropGroupID };
                m.shownAs = own != s.dispName ? s.dispName : null;
                counted[key] = (counted.ContainsKey(key) ? counted[key] : 0) + 1;
                if (s.isDead) continue;
                m.cid = s.getCID();
                if (!string.IsNullOrEmpty(s.dispTitle)) m.title = s.dispTitle;
                try { m.stats = StatsOf(s); } catch { }
                var p = s.transform.position;
                var spot = new[] { Mathf.RoundToInt(p.x / Grid) * Grid, Mathf.RoundToInt(p.z / Grid) * Grid };
                if (m.spots == null) m.spots = new List<int[]>();
                if (m.spots.Count < MaxSpots && !m.spots.Any(a => a[0] == spot[0] && a[1] == spot[1])) m.spots.Add(spot);
                CommonStats best;
                if (s.initializedCharacterModel && (!nearest.TryGetValue(key, out best) || (p - here).sqrMagnitude < (best.transform.position - here).sqrMagnitude)) nearest[key] = s;
            }
            foreach (var kv in counted) known[kv.Key].seen = Math.Max(known[kv.Key].seen, kv.Value);

            // pictures: one per monster name, shared by the zones it lives in
            Directory.CreateDirectory(PicDir);
            var queued = new HashSet<string>(pending.Select(q => q.Value));
            foreach (var kv in nearest)
            {
                var m = known[kv.Key];
                var pic = PicName(m.name);
                if (File.Exists(Path.Combine(PicDir, pic + ".png"))) { m.pic = pic; continue; }
                if (queued.Add(pic)) pending.Enqueue(new KeyValuePair<CommonStats, string>(kv.Value, pic));
            }
            foreach (var m in known.Values) if (m.pic == null && File.Exists(Path.Combine(PicDir, PicName(m.name) + ".png"))) m.pic = PicName(m.name);
            Write(known, toJson);
            ModLoader.Log("ContentLoader: monsters: " + counted.Count + " kinds in " + zone + ", " + known.Count + " known in total" + (pending.Count > 0 ? ", " + pending.Count + " pictures to take" : ""));
        }

        static void Write(Dictionary<string, MonsterInfo> known, Func<object, string> toJson)
        {
            var list = known.Values.OrderBy(m => m.zone).ThenBy(m => m.dropId).ThenBy(m => m.name).ToList();
            var json = toJson(list);
            if (json == lastJson) return;
            File.WriteAllText(Path_, json);
            lastJson = json;
        }

        /// <summary>Takes at most one picture a frame; marks it in monsters.json once taken.</summary>
        public static void Update(Func<object, string> toJson)
        {
            if (pending.Count == 0) return;
            var job = pending.Dequeue();
            bool ok = false;
            try { ok = job.Key != null && !job.Key.isDead && Snap(job.Key, Path.Combine(PicDir, job.Value + ".png")); }
            catch (Exception e) { ModLoader.Log("ContentLoader: picture of a monster failed: " + e.Message); }
            if (!ok || pending.Count > 0) return;
            // all taken: note them in the file
            try
            {
                var list = JsonConvert.DeserializeObject<List<MonsterInfo>>(File.ReadAllText(Path_)) ?? new List<MonsterInfo>();
                var known = new Dictionary<string, MonsterInfo>();
                foreach (var m in list) if (m != null) { known[m.zone + "|" + m.name + "|" + m.dropId] = m; if (File.Exists(Path.Combine(PicDir, PicName(m.name) + ".png"))) m.pic = PicName(m.name); }
                Write(known, toJson);
            }
            catch { }
        }

        static bool Spawned(CommonStats s)
        {
            for (var t = s.transform; t != null; t = t.parent) if (t.name.StartsWith("ModEnemy_")) return true;
            return false;
        }
        /// <summary>The game's name for it: EnemyLoader keeps a renamed monster's original name on a child "ModRenamedFrom:&lt;name&gt;".</summary>
        static string OwnName(CommonStats s)
        {
            var t = s.transform;
            for (int i = 0; i < t.childCount; i++) { var n = t.GetChild(i).name; if (n.StartsWith("ModRenamedFrom:")) return n.Substring("ModRenamedFrom:".Length); }
            return s.dispName;
        }

        static float V(FloatParameterSet p, float init) { return p != null && p.initialized ? p.mathTotal() : init; }

        static Dictionary<string, float> StatsOf(CommonStats s)
        {
            var d = new Dictionary<string, float>();
            Action<string, float> put = (k, v) => { if (!float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) > 0.0001f) d[k] = (float)Math.Round(v, 2); };
            put("health", V(s.health, s.health_init));
            put("mana", V(s.mana, s.mana_init));
            put("damage", s.damage_init);
            put("damageRandom", s.damageRandom_init);
            put("weaponDamage", s.weaponDamage_init);
            put("weaponDamageRandom", s.weaponDamageRandom_init);
            put("weaponSpeed", V(s.weaponSpeed, s.weaponSpeed_init));
            put("attackPower", V(s.attackpower, s.attackpower_init));
            put("willpower", V(s.willpower, s.willpower_init));
            put("dexterity", V(s.dexterity, s.dexterity_init));
            put("defense", V(s.defense, s.defense_init));
            put("resistance", V(s.resistance, s.resistance_init));
            put("avoidance", V(s.avoidance, s.avoidance_init));
            put("critRate", V(s.critRate, s.critRate_init));
            put("healthRecovery", V(s.healthRecovery, s.healthRecovery_init));
            put("moveSpeed", V(s.moveSpeed, s.moveSpeed_init));
            put("attackRange", V(s.attackRange, s.attackRange_init));
            put("sightRange", V(s.sightRange, s.sightRange_init));
            put("exp", s.exp_init);
            put("gold", s.gold_init);
            return d;
        }

        /// <summary>
        /// A picture of one monster with its own camera. Its renderers go on a spare layer for a moment so
        /// only it is drawn. Drawn on black and on white: the difference gives a clean transparent background
        /// whatever its shaders write to alpha.
        /// </summary>
        static bool Snap(CommonStats s, string file)
        {
            var root = s.controllTarget != null ? s.controllTarget : s.transform;
            // Far away, the game switches a monster's model off: switch it on just for the picture.
            var rends = root.GetComponentsInChildren<Renderer>(true).Where(r => r != null && (r is SkinnedMeshRenderer || r is MeshRenderer)).ToArray();
            var wasOff = rends.Where(r => !r.enabled).ToArray();
            var inactive = new List<GameObject>();
            foreach (var r in rends)
                for (var t = r.transform; t != null && t != root.parent; t = t.parent)
                    if (!t.gameObject.activeSelf && !inactive.Contains(t.gameObject)) inactive.Add(t.gameObject);
            try
            {
                foreach (var g in inactive) g.SetActive(true);
                foreach (var r in wasOff) r.enabled = true;
                return SnapShown(s, root, rends, file);
            }
            finally
            {
                foreach (var r in wasOff) if (r != null) r.enabled = false;
                for (int i = inactive.Count - 1; i >= 0; i--) if (inactive[i] != null) inactive[i].SetActive(false);
            }
        }

        static bool SnapShown(CommonStats s, Transform root, Renderer[] rends, string file)
        {
            if (rends.Length == 0) { Why(s, "no model shown (" + root.GetComponentsInChildren<Renderer>(true).Length + " hidden parts, loaded " + s.initializedCharacterModel + ")"); return false; }
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            float radius = b.extents.magnitude;
            if (radius < 0.05f || radius > 60f) { Why(s, "odd size " + radius); return false; }

            const int Layer = 31;
            var oldLayers = rends.Select(r => r.gameObject.layer).ToArray();
            var go = new GameObject("NineLivesMonsterCamera");
            var rt = new RenderTexture(PicSize, PicSize, 24, RenderTextureFormat.ARGB32);
            var black = new Texture2D(PicSize, PicSize, TextureFormat.RGB24, false);
            var white = new Texture2D(PicSize, PicSize, TextureFormat.RGB24, false);
            var was = RenderTexture.active;
            try
            {
                foreach (var r in rends) r.gameObject.layer = Layer;
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.fieldOfView = 30f;
                cam.cullingMask = 1 << Layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.targetTexture = rt;
                // a little to the side and above its face
                var fwd = s.transform.forward; fwd.y = 0;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                var dir = (Quaternion.AngleAxis(-25f, Vector3.up) * fwd.normalized + Vector3.up * 0.35f).normalized;
                float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.02f;
                cam.transform.position = b.center + dir * dist;
                cam.transform.LookAt(b.center);
                cam.nearClipPlane = Mathf.Max(0.05f, dist - radius * 1.5f);
                cam.farClipPlane = dist + radius * 1.5f;

                cam.backgroundColor = Color.black; cam.Render();
                RenderTexture.active = rt; black.ReadPixels(new Rect(0, 0, PicSize, PicSize), 0, 0); black.Apply();
                cam.backgroundColor = Color.white; cam.Render();
                RenderTexture.active = rt; white.ReadPixels(new Rect(0, 0, PicSize, PicSize), 0, 0); white.Apply();

                var pb = black.GetPixels32(); var pw = white.GetPixels32();
                var outPx = new Color32[pb.Length];
                int solid = 0;
                for (int i = 0; i < pb.Length; i++)
                {
                    // on black: c*a; on white: c*a + (1-a)  →  a = 1 - (white - black)
                    int diff = ((pw[i].r - pb[i].r) + (pw[i].g - pb[i].g) + (pw[i].b - pb[i].b)) / 3;
                    int a = Mathf.Clamp(255 - diff, 0, 255);
                    if (a < 8) { outPx[i] = new Color32(0, 0, 0, 0); continue; }
                    solid++;
                    outPx[i] = new Color32((byte)Mathf.Min(255, pb[i].r * 255 / a), (byte)Mathf.Min(255, pb[i].g * 255 / a), (byte)Mathf.Min(255, pb[i].b * 255 / a), (byte)a);
                }
                if (solid < PicSize * PicSize / 200) { Why(s, "nothing drawn (" + solid + " px)"); return false; }   // not loaded yet
                var tex = new Texture2D(PicSize, PicSize, TextureFormat.ARGB32, false);
                try { tex.SetPixels32(outPx); tex.Apply(); File.WriteAllBytes(file, tex.EncodeToPNG()); }
                finally { UnityEngine.Object.Destroy(tex); }
                return true;
            }
            finally
            {
                for (int i = 0; i < rends.Length; i++) if (rends[i] != null) rends[i].gameObject.layer = oldLayers[i];
                RenderTexture.active = was;
                UnityEngine.Object.Destroy(go);
                rt.Release(); UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(black); UnityEngine.Object.Destroy(white);
            }
        }
    }
}
