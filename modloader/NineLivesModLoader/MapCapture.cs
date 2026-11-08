using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace NineLivesModLoader
{
    /// <summary>
    /// Top-down pictures of each zone for the Nine Lives Editor's map: Mods\maps\&lt;zone&gt;.png plus
    /// &lt;zone&gt;.json with the area it covers. Taken the first time you're in a zone (and again on request).
    /// North (+Z) is up, east (+X) is right.
    /// </summary>
    static class MapCapture
    {
        const int Size = 2048;
        static float captureAt = -1;

        public static string Dir { get { return Path.Combine(ModLoader.ModsDir, "maps"); } }

        internal static void Init()
        {
            Events.PlayerSpawned += () =>
            {
                if (!File.Exists(Path.Combine(Dir, Game.Zone + ".png"))) captureAt = Time.realtimeSinceStartup + 6f;   // let the zone settle
            };
            Events.Update += () =>
            {
                if (captureAt > 0 && Time.realtimeSinceStartup >= captureAt)
                {
                    captureAt = -1;
                    try { ModLoader.Log(Capture()); } catch (Exception e) { ModLoader.Log("Map capture failed: " + e.Message); }
                }
            };
            LiveLink.Route("/map/capture", r => { try { return "{\"ok\":true,\"message\":" + LiveLink.Quote(Capture()) + "}"; } catch (Exception e) { return "{\"error\":" + LiveLink.Quote(e.Message) + "}"; } });
            LiveLink.Route("/map/height", r =>
            {
                float x, z, from;
                float.TryParse(r.Get("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                float.TryParse(r.Get("z"), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                RaycastHit hit;
                // "from": the height it's at now: the floor just below that (inside a house: its floor, not the roof)
                if (float.TryParse(r.Get("from"), NumberStyles.Float, CultureInfo.InvariantCulture, out from) &&
                    Physics.Raycast(new Vector3(x, from + 3f, z), Vector3.down, out hit, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return "{\"y\":" + Math.Round(hit.point.y, 2).ToString(CultureInfo.InvariantCulture) + "}";
                float y;
                if (!TryHeight(x, z, out y)) return "{\"y\":null}";
                return "{\"y\":" + Math.Round(y, 2).ToString(CultureInfo.InvariantCulture) + "}";
            });
        }

        /// <summary>Height of the ground (or a floor) at x, z.</summary>
        public static float Height(float x, float z) { float y; TryHeight(x, z, out y); return y; }

        static bool TryHeight(float x, float z, out float y)
        {
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(x, 3000f, z), Vector3.down, out hit, 6000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) { y = hit.point.y; return true; }
            var t = Terrain.activeTerrain;
            if (t != null) { y = t.SampleHeight(new Vector3(x, 0, z)) + t.GetPosition().y; return true; }
            y = 0; return false;
        }

        /// <summary>Photograph the zone from above.</summary>
        public static string Capture()
        {
            if (!Game.InWorld) return "Not in the world yet.";
            // The area: the terrain, or the game's own idea of the world size.
            Vector3 min, size;
            var terrain = Terrain.activeTerrain;
            if (terrain != null) { min = terrain.GetPosition(); size = terrain.terrainData.size; }
            else
            {
                var root = Universe.GetWorldRootCtrl();
                size = root != null ? root.getWorldSize() : new Vector3(2000, 600, 2000);
                min = Vector3.zero;
            }
            float side = Mathf.Max(size.x, size.z);
            var center = new Vector3(min.x + size.x / 2f, 0, min.z + size.z / 2f);
            float top = min.y + size.y + 50f;

            var go = new GameObject("NineLivesMapCamera");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = side / 2f;
            cam.aspect = 1f;
            cam.transform.position = new Vector3(center.x, top, center.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.nearClipPlane = 1f;
            cam.farClipPlane = size.y + 2000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.1f, 1f);
            cam.cullingMask = ~0;
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            // Even, bright lighting and no fog, so a map taken at night or in fog is still readable.
            bool fog = RenderSettings.fog;
            var ambient = RenderSettings.ambientLight; var ambientMode = RenderSettings.ambientMode; var ambientIntensity = RenderSettings.ambientIntensity;
            var prev = RenderTexture.active;
            try
            {
                RenderSettings.fog = false;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.75f, 0.75f, 0.75f);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
                try
                {
                    tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                    tex.Apply();
                    Directory.CreateDirectory(Dir);
                    File.WriteAllBytes(Path.Combine(Dir, Game.Zone + ".png"), tex.EncodeToPNG());
                }
                finally { UnityEngine.Object.Destroy(tex); }
            }
            finally
            {
                RenderTexture.active = prev;
                RenderSettings.fog = fog; RenderSettings.ambientLight = ambient; RenderSettings.ambientMode = ambientMode; RenderSettings.ambientIntensity = ambientIntensity;
                cam.targetTexture = null;
                UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(go);
            }
            // What the picture covers (a square: the longer side of the terrain).
            Func<float, string> F = v => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(Dir, Game.Zone + ".json"),
                "{\n  \"zone\": " + LiveLink.Quote(Game.Zone) + ",\n  \"minX\": " + F(center.x - side / 2f) + ", \"minZ\": " + F(center.z - side / 2f) +
                ",\n  \"maxX\": " + F(center.x + side / 2f) + ", \"maxZ\": " + F(center.z + side / 2f) + ",\n  \"pixels\": " + Size +
                ",\n  \"taken\": " + LiveLink.Quote(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) + "\n}\n");
            return "Map of " + Game.Zone + " saved (" + Math.Round(side) + " m across)";
        }
    }
}
