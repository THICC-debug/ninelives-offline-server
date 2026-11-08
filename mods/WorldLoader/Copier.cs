using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NineLivesModLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WorldLoader
{
    /// <summary>Finding things in the scene by path, and making copies of them that can be moved.</summary>
    static class Copier
    {
        // ------------------------------------------------------------ paths
        // A path is the names from the scene root down, with "#k" for the k-th sibling of the same name:
        // "World/props/fence#2/post". It survives game restarts as long as the scene doesn't change.

        public static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
            {
                int k = 0;
                if (t.parent != null)
                {
                    for (int i = 0; i < t.parent.childCount; i++)
                    {
                        var c = t.parent.GetChild(i);
                        if (c == t) break;
                        if (c.name == t.name) k++;
                    }
                }
                else
                {
                    foreach (var r in Roots()) { if (r == t) break; if (r.name == t.name) k++; }
                }
                parts.Insert(0, k > 0 ? t.name + "#" + k : t.name);
            }
            return string.Join("/", parts.ToArray());
        }

        public static Transform Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            IList<Transform> level = Roots();
            Transform cur = null;
            foreach (var part in parts)
            {
                string name = part; int k = 0;
                int h = part.LastIndexOf('#');
                if (h > 0 && int.TryParse(part.Substring(h + 1), out k)) name = part.Substring(0, h); else k = 0;
                cur = level.Where(t => t.name == name).Skip(k).FirstOrDefault();
                if (cur == null) return null;
                var kids = new List<Transform>();
                for (int i = 0; i < cur.childCount; i++) kids.Add(cur.GetChild(i));
                level = kids;
            }
            return cur;
        }

        static List<Transform> Roots()
        {
            var scene = SceneManager.GetActiveScene();
            return scene.GetRootGameObjects().Select(g => g.transform).ToList();
        }

        // ------------------------------------------------------------ looks
        public static Bounds BoundsOf(Transform t)
        {
            var all = t.GetComponentsInChildren<Renderer>(true);
            var rs = all.Where(r => r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleRenderer || r is ParticleSystemRenderer)).ToArray();
            if (rs.Length == 0) rs = all;   // (something hidden as a whole: its own size)
            if (rs.Length == 0) return new Bounds(t.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        /// <summary>Is this part of a character (NPC, monster, player)? Those are NPCLoader's job.</summary>
        public static bool IsCharacter(Transform t) { return t.GetComponentInParent<CommonStats>() != null; }

        // ------------------------------------------------------------ copies
        public class Report { public int renderers, batched, viaCollider, extracted, twins, drawn, missing; public bool boxed; public string Text()
            { return renderers + " part(s)" + (batched > 0 ? ", " + batched + " pre-baked (" + viaCollider + " rebuilt from collision, " + extracted + " cut out, " + twins + " found loaded, " + drawn + " drawn from the index, " + missing + " missing)" : ""); } }

        static readonly PropertyInfo StaticBatchIndex = typeof(Renderer).GetProperty("staticBatchIndex", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly PropertyInfo StaticBatchRoot = typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// A copy of src that can be moved. The game pre-bakes ("static batches") much of its scenery:
        /// a plain copy of such a part would still draw at the original's place, so those parts get a
        /// fresh mesh: the collision mesh if it's the same model, or the piece cut out of the baked mesh.
        /// keepScripts = false copies only the look and collision (no doors, triggers, spawners).
        /// </summary>
        public static GameObject MakeCopy(Transform src, bool keepScripts, out Report rep)
        {
            rep = new Report();
            var go = (GameObject)UnityEngine.Object.Instantiate(src.gameObject);
            try { return Build(go, src, keepScripts, rep); }
            catch { UnityEngine.Object.Destroy(go); throw; }   // never leave half a copy behind
        }

        /// <summary>Test switches (POST /world/copytest): turn parts of copying off to find a problem.</summary>
        public static bool UseCut = true, UseTwin = true, UseDraw = true, UseBox = true, UseColliders = true, DrawReceive = true;
        // Casting shadows from pieces of the game's baked meshes crashes Unity 5.3 (it reads mesh data the game has
        // already thrown away), so copies drawn that way don't cast shadows. They still receive them.
        public static bool DrawShadows = false;

        static GameObject Build(GameObject go, Transform src, bool keepScripts, Report rep)
        {
            if (!UseColliders) foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            var draws = new List<KeyValuePair<GameObject, BakedDraw.Setup>>();
            // Level-of-detail groups list the original's renderers; the copy swaps or removes those, and a group
            // pointing at renderers that are gone crashes the game while drawing. Copies don't need them.
            foreach (var lod in go.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(lod);
            var srcR = src.GetComponentsInChildren<MeshRenderer>(true);
            var cpyR = go.GetComponentsInChildren<MeshRenderer>(true);
            rep.renderers = srcR.Length + src.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            for (int i = 0; i < srcR.Length && i < cpyR.Length; i++)
            {
                if (!srcR[i].isPartOfStaticBatch) continue;
                rep.batched++;
                bool fromCollider;
                fromCollider = false;
                var mesh = UseCut ? OriginalMesh(srcR[i], out fromCollider) : null;
                bool twin = false;
                if (mesh == null && UseTwin) { mesh = LoadedTwin(srcR[i]); twin = mesh != null; }
                var holder = cpyR[i].gameObject;
                if (mesh == null)
                {
                    // Best: we know this part's own pieces of the baked mesh (from the scene file index):
                    // draw just those, from where the copy stands, as an ordinary object.
                    var pieces = BakedIndex.Pieces(srcR[i]);
                    var srcMf = srcR[i].GetComponent<MeshFilter>();
                    var combined = srcMf != null ? srcMf.sharedMesh : null;
                    if (pieces != null && combined != null && UseDraw)
                    {
                        var origRoot0 = StaticBatchRoot != null ? StaticBatchRoot.GetValue(srcR[i], null) as Transform : null;
                        var setup = new BakedDraw.Setup {
                            mesh = combined, pieces = pieces, materials = cpyR[i].sharedMaterials, layer = srcR[i].gameObject.layer,
                            fromBatch = srcR[i].transform.worldToLocalMatrix * (origRoot0 != null ? origRoot0.localToWorldMatrix : Matrix4x4.identity),
                            shadows = cpyR[i].shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off, receive = cpyR[i].receiveShadows };
                        UnityEngine.Object.DestroyImmediate(cpyR[i]);
                        var cmf = holder.GetComponent<MeshFilter>();
                        if (cmf != null) UnityEngine.Object.DestroyImmediate(cmf);
                        draws.Add(new KeyValuePair<GameObject, BakedDraw.Setup>(holder, setup));
                        rep.drawn++;
                        continue;
                    }
                }
                // (Moving the copy's baked piece by giving it its own "batch root" was tried: it fades, warps and
                //  can crash the game on big things. A part we can't rebuild is left out and reported instead.)
                var mats = cpyR[i].sharedMaterials;
                var shadows = cpyR[i].shadowCastingMode; var receive = cpyR[i].receiveShadows;
                UnityEngine.Object.DestroyImmediate(cpyR[i]);
                var mf = holder.GetComponent<MeshFilter>();
                if (mf != null) UnityEngine.Object.DestroyImmediate(mf);
                if (mesh == null) { rep.missing++; continue; }
                if (fromCollider) rep.viaCollider++; else if (twin) rep.twins++; else rep.extracted++;
                holder.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = holder.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mesh.subMeshCount < mats.Length ? mats.Take(Math.Max(1, mesh.subMeshCount)).ToArray() : mats;
                mr.shadowCastingMode = shadows; mr.receiveShadows = receive;
            }
            if (!keepScripts)
            {
                // Just the look and the collision: no game scripts, no trigger areas.
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) if (c.isTrigger) UnityEngine.Object.DestroyImmediate(c);
            }
            foreach (var d in draws) d.Key.AddComponent<BakedDraw>().Init(d.Value);   // after the clean-up above
            // Copies of small props often have no collision of their own (the zone's shared collision has it):
            // give them a box that fits their look, so you can't walk through them.
            if (UseBox && !go.GetComponentsInChildren<Collider>(true).Any(c => !c.isTrigger))
            {
                var box = LocalBox(src);
                if (box.size.sqrMagnitude > 0.0001f) { var bc = go.AddComponent<BoxCollider>(); bc.center = box.center; bc.size = box.size; rep.boxed = true; }
            }
            go.SetActive(true);
            return go;
        }


        /// <summary>The box around everything t shows, in t's own space.</summary>
        static Bounds LocalBox(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            bool any = false; var b = new Bounds();
            foreach (var r in rs)
            {
                if (r is ParticleRenderer || r is ParticleSystemRenderer || !r.enabled || !r.gameObject.activeSelf) continue;
                var w = r.bounds; var c = w.center; var e = w.extents;
                for (int i = 0; i < 8; i++)
                {
                    var p = t.InverseTransformPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        public static bool HasMovedBakedParts(GameObject g) { return false; }   // (the batch-root approach is gone)

        /// <summary>The model a pre-baked renderer had before baking.</summary>
        static Mesh OriginalMesh(MeshRenderer r, out bool fromCollider)
        {
            fromCollider = false;
            // 1) Buildings usually collide with their own model: use that.
            var mc = r.GetComponent<MeshCollider>();
            if (mc != null && mc.sharedMesh != null && !mc.sharedMesh.name.StartsWith("Combined Mesh"))
            {
                fromCollider = true;
                return mc.sharedMesh;
            }
            // 2) Cut its piece out of the baked mesh (only if the game left that mesh readable).
            try
            {
                var mf = r.GetComponent<MeshFilter>();
                var combined = mf != null ? mf.sharedMesh : null;
                if (combined == null || StaticBatchIndex == null || unreadable.Contains(combined)) return null;
                int first = (int)StaticBatchIndex.GetValue(r, null);
                var root = StaticBatchRoot != null ? StaticBatchRoot.GetValue(r, null) as Transform : null;
                var verts = combined.vertices; var normals = combined.normals; var uv = combined.uv; var uv2 = combined.uv2;
                var colors = combined.colors; var tangents = combined.tangents;
                if (verts == null || verts.Length == 0) { unreadable.Add(combined); return null; }
                var map = new Dictionary<int, int>();
                var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nuv = new List<Vector2>(); var nuv2 = new List<Vector2>();
                var nc = new List<Color>(); var nt = new List<Vector4>();
                var subs = new List<int[]>();
                for (int s = 0; s < r.sharedMaterials.Length; s++)
                {
                    if (first + s >= combined.subMeshCount) break;
                    var tris = combined.GetTriangles(first + s);
                    var outTris = new int[tris.Length];
                    for (int i = 0; i < tris.Length; i++)
                    {
                        int v = tris[i], j;
                        if (!map.TryGetValue(v, out j))
                        {
                            j = nv.Count; map[v] = j;
                            var world = root != null ? root.TransformPoint(verts[v]) : verts[v];
                            nv.Add(r.transform.InverseTransformPoint(world));
                            if (normals.Length > v) nn.Add(r.transform.InverseTransformDirection(root != null ? root.TransformDirection(normals[v]) : normals[v]));
                            if (uv.Length > v) nuv.Add(uv[v]);
                            if (uv2.Length > v) nuv2.Add(uv2[v]);
                            if (colors.Length > v) nc.Add(colors[v]);
                            if (tangents.Length > v) nt.Add(tangents[v]);
                        }
                        outTris[i] = j;
                    }
                    subs.Add(outTris);
                }
                if (nv.Count == 0) return null;
                var m = new Mesh { name = r.name + " (copy)" };
                m.vertices = nv.ToArray();
                if (nn.Count == nv.Count) m.normals = nn.ToArray();
                if (nuv.Count == nv.Count) m.uv = nuv.ToArray();
                if (nuv2.Count == nv.Count) m.uv2 = nuv2.ToArray();
                if (nc.Count == nv.Count) m.colors = nc.ToArray();
                if (nt.Count == nv.Count) m.tangents = nt.ToArray();
                m.subMeshCount = subs.Count;
                for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s);
                m.RecalculateBounds();
                return m;
            }
            catch { return null; }   // not readable: nothing we can do for this part
        }
        static readonly HashSet<Mesh> unreadable = new HashSet<Mesh>();

        // Everything loaded, listed once for a whole copy (not once per part: big houses have hundreds).
        static UnityEngine.Object[] allFilters, allMeshes; static float listedAt = -100f;
        static void ListLoaded()
        {
            if (Time.realtimeSinceStartup - listedAt < 10f && allFilters != null) return;
            allFilters = Resources.FindObjectsOfTypeAll(typeof(MeshFilter));
            allMeshes = Resources.FindObjectsOfTypeAll(typeof(Mesh));
            listedAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// The same model somewhere else in memory, in one piece: another copy of the prop the game didn't
        /// bake, or the model itself if another scene or prefab keeps it loaded. We only hand it to the
        /// copy (no reading), so it works even when the game's meshes aren't readable. A candidate has to
        /// fit exactly where the baked part is (same box in the world) to be used.
        /// </summary>
        static Mesh LoadedTwin(MeshRenderer r)
        {
            string name = Clean(r.gameObject.name);
            string key = name + "|" + r.bounds.size.ToString("F2");
            Mesh hit;
            if (twinCache.TryGetValue(key, out hit)) return hit;
            hit = null;
            var want = r.bounds.size;
            Func<Mesh, bool> fits = m =>
            {
                if (m == null || m.name.StartsWith("Combined Mesh")) return false;
                var got = WorldBox(m.bounds, r.transform.localToWorldMatrix).size;
                return Close(got.x, want.x) && Close(got.y, want.y) && Close(got.z, want.z);
            };
            // 1) a not-baked object with the same name
            ListLoaded();
            foreach (var o in allFilters)
            {
                var mf = o as MeshFilter;
                if (mf == null || Clean(mf.gameObject.name) != name) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr != null && mr.isPartOfStaticBatch) continue;
                if (fits(mf.sharedMesh)) { hit = mf.sharedMesh; break; }
            }
            // 2) a loaded model with a matching name
            if (hit == null)
            {
                string bare = name.EndsWith("_prefab") ? name.Substring(0, name.Length - 7) : name;
                foreach (var o in allMeshes)
                {
                    var m = o as Mesh;
                    if (m == null) continue;   // unloaded since we listed them
                    var mn = Clean(m.name);
                    if (mn != name && mn != bare && !mn.StartsWith(bare)) continue;
                    if (fits(m)) { hit = m; break; }
                }
            }
            twinCache[key] = hit;
            return hit;
        }

        static readonly Dictionary<string, Mesh> twinCache = new Dictionary<string, Mesh>();
        /// <summary>A new zone: forget what was found (or not found) in the last one.</summary>
        public static void ClearCaches() { twinCache.Clear(); unreadable.Clear(); allFilters = allMeshes = null; }

        static string Clean(string n)
        {
            if (n == null) return "";
            n = n.Replace("(Clone)", "").Trim();
            int i = n.IndexOf(" Instance", StringComparison.Ordinal); if (i > 0) n = n.Substring(0, i);
            return n.ToLowerInvariant();
        }

        static bool Close(float a, float b) { return Mathf.Abs(a - b) <= Mathf.Max(0.02f, 0.03f * Mathf.Max(a, b)); }

        public static string BatchRootOf(Renderer r)
        {
            var t = StaticBatchRoot != null ? StaticBatchRoot.GetValue(r, null) as Transform : null;
            return t == null ? null : t.name + " at " + t.position;
        }

        public static Bounds WorldBoxOf(Bounds local, Matrix4x4 m) { return WorldBox(local, m); }

        static Bounds WorldBox(Bounds local, Matrix4x4 m)
        {
            var c = local.center; var e = local.extents;
            var b = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return b;
        }
    }

    /// <summary>
    /// Which pieces of its baked mesh each baked part owns, per scene, from Mods\WorldLoader\batches\
    /// (made by tools\batch_index.py from the game's scene files). Looked up by name and place.
    /// </summary>
    public static class BakedIndex
    {
        static string zone;
        static readonly Dictionary<string, List<KeyValuePair<Vector3, int[]>>> byName = new Dictionary<string, List<KeyValuePair<Vector3, int[]>>>();

        public static int[] Pieces(Renderer r)
        {
            Load();
            List<KeyValuePair<Vector3, int[]>> list;
            if (!byName.TryGetValue(r.gameObject.name, out list)) return null;
            var p = r.transform.position;
            int[] best = null; float bestD = 0.3f * 0.3f;
            foreach (var kv in list) { float d = (kv.Key - p).sqrMagnitude; if (d < bestD) { bestD = d; best = kv.Value; } }
            return best;
        }

        static void Load()
        {
            var z = Game.Zone;
            if (z == zone) return;
            zone = z; byName.Clear();
            var f = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(ModLoader.ModsDir, "WorldLoader"), "batches"), z + ".json");
            if (!System.IO.File.Exists(f)) { ModLoader.Log("WorldLoader: no baked index for " + z + " (" + f + ")"); return; }
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(f));
                foreach (var kv in o)
                {
                    var parts = kv.Key.Split('|');
                    if (parts.Length != 4) continue;
                    var pos = new Vector3(F(parts[1]), F(parts[2]), F(parts[3]));
                    var pieces = ((Newtonsoft.Json.Linq.JArray)kv.Value).Select(x => (int)x).ToArray();
                    List<KeyValuePair<Vector3, int[]>> list;
                    if (!byName.TryGetValue(parts[0], out list)) byName[parts[0]] = list = new List<KeyValuePair<Vector3, int[]>>();
                    list.Add(new KeyValuePair<Vector3, int[]>(pos, pieces));
                }
            }
            catch (Exception e) { ModLoader.Log("WorldLoader: reading " + f + " failed: " + e.Message); }
        }

        static float F(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// Draws a copied baked part's own pieces of the game's baked mesh, at the copy, every frame. The
    /// baked mesh can't be read, but it can be drawn; this way the copy is an ordinary object (it isn't
    /// hidden or shown along with the original).
    /// </summary>
    public class BakedDraw : MonoBehaviour
    {
        public class Setup { public Mesh mesh; public int[] pieces; public Material[] materials; public Matrix4x4 fromBatch;
                             public bool shadows, receive; public int layer; }
        Setup s;
        static readonly List<BakedDraw> all = new List<BakedDraw>();
        public void Init(Setup setup) { s = setup; if (!all.Contains(this)) all.Add(this); }
        void OnDestroy() { all.Remove(this); }

        void LateUpdate() { Draw(null); }

        void Draw(Camera cam)
        {
            if (s == null || s.mesh == null || !isActiveAndEnabled) return;
            var m = transform.localToWorldMatrix * s.fromBatch;
            for (int i = 0; i < s.pieces.Length && i < s.materials.Length; i++)
            {
                int sub = s.pieces[i];
                if (sub < 0 || sub >= s.mesh.subMeshCount || s.materials[i] == null) continue;
                Graphics.DrawMesh(s.mesh, m, s.materials[i], s.layer, cam, sub, null, s.shadows && Copier.DrawShadows, s.receive && Copier.DrawReceive);
            }
        }

        /// <summary>Before a camera is rendered by hand (the editor's game view): queue every copy for it.</summary>
        public static void DrawAllFor(Camera cam) { foreach (var b in all) if (b != null) b.Draw(cam); }
    }
}
