using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(t.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        /// <summary>Is this part of a character (NPC, monster, player)? Those are NPCLoader's job.</summary>
        public static bool IsCharacter(Transform t) { return t.GetComponentInParent<CommonStats>() != null; }

        // ------------------------------------------------------------ copies
        public class Report { public int renderers, batched, viaCollider, extracted, missing; public string Text()
            { return renderers + " part(s)" + (batched > 0 ? ", " + batched + " pre-baked (" + viaCollider + " rebuilt from collision, " + extracted + " cut out, " + missing + " missing)" : ""); } }

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
            var srcR = src.GetComponentsInChildren<MeshRenderer>(true);
            var cpyR = go.GetComponentsInChildren<MeshRenderer>(true);
            rep.renderers = srcR.Length + src.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            for (int i = 0; i < srcR.Length && i < cpyR.Length; i++)
            {
                if (!srcR[i].isPartOfStaticBatch) continue;
                rep.batched++;
                bool fromCollider;
                var mesh = OriginalMesh(srcR[i], out fromCollider);
                var holder = cpyR[i].gameObject;
                var mats = cpyR[i].sharedMaterials;
                var shadows = cpyR[i].shadowCastingMode; var receive = cpyR[i].receiveShadows;
                UnityEngine.Object.DestroyImmediate(cpyR[i]);
                var mf = holder.GetComponent<MeshFilter>();
                if (mf != null) UnityEngine.Object.DestroyImmediate(mf);
                if (mesh == null) { rep.missing++; continue; }
                if (fromCollider) rep.viaCollider++; else rep.extracted++;
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
            go.SetActive(true);
            return go;
        }

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
                if (combined == null || StaticBatchIndex == null) return null;
                int first = (int)StaticBatchIndex.GetValue(r, null);
                var root = StaticBatchRoot != null ? StaticBatchRoot.GetValue(r, null) as Transform : null;
                var verts = combined.vertices; var normals = combined.normals; var uv = combined.uv; var uv2 = combined.uv2;
                var colors = combined.colors; var tangents = combined.tangents;
                if (verts == null || verts.Length == 0) return null;
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
    }
}
