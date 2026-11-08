using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NineLivesModLoader
{
    /// <summary>
    /// Helpers for building, used by the editor's 3D view: go to another zone, hide NPCs, bright light,
    /// no fog. POST /editor/zone {zone}, POST /editor/toggle?name=npcs|bright|fog&amp;on=1, GET /editor/tools.
    /// </summary>
    public static class EditorTools
    {
        public const string AnyArrival = "__nle_any__";
        static bool hideNpcs, bright, noFog;
        static float nextNpcPass;
        static readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        static readonly HashSet<int> hiddenNpcs = new HashSet<int>();
        static Color savedAmbient; static float savedAmbientIntensity; static bool savedFog; static bool haveSaved;
        static UnityEngine.Rendering.AmbientMode savedAmbientMode;

        internal static void Init(Harmony h)
        {
            // Going to a zone needs an arrival point's name; ours means "the first one there is".
            h.Patch(AccessTools.Method(typeof(PortalController), "getArrivePoint", new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(EditorTools), nameof(AnyArrivalPoint)));
            // The game's HUD (side tabs, bars, chat) is drawn every frame by GUIRoot.OnGUI: not in the 3D view.
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("-nleditor", StringComparison.OrdinalIgnoreCase)))
                h.Patch(AccessTools.Method(typeof(GUIRoot), "OnGUI"), prefix: new HarmonyMethod(typeof(EditorTools), nameof(NoHud)));
        }

        static bool NoHud() { return !(Game.InWorld && !Universe.levelLoading); }   // loading screens still show

        static bool AnyArrivalPoint(string pointName_, string zoneName_, ref PortalArrivePointDataSet __result)
        {
            if (pointName_ != AnyArrival) return true;
            var p = PortalController.arrivePoints.Where(kv => kv.Key.EndsWith("__" + zoneName_, StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault();
            __result = p ?? new PortalArrivePointDataSet(zoneName_, new Vector3(1000, 400, 1000), Vector3.zero);
            return false;
        }

        // ------------------------------------------------------------ requests
        internal static string Zone(LiveRequest r)
        {
            if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";
            var zone = r.Get("zone") ?? JsonField(r.Body, "zone");
            if (string.IsNullOrEmpty(zone)) return "{\"error\":\"Which zone?\"}";
            if (zone == Game.Zone) return "{\"ok\":true,\"message\":\"Already there.\"}";
            if (!Application.CanStreamedLevelBeLoaded(zone)) return "{\"error\":" + LiveLink.Quote("There's no zone called " + zone + ".") + "}";
            if (Universe.levelLoading) return "{\"error\":\"Already going somewhere.\"}";
            Universe.doZoneJump(zone, AnyArrival, Game.Player.gameObject);
            EditorMode.ZoneChanging();
            return "{\"ok\":true,\"message\":" + LiveLink.Quote("Going to " + zone + "…") + "}";
        }

        internal static string Toggle(LiveRequest r)
        {
            bool on = r.Get("on") == "1";
            switch (r.Get("name"))
            {
                case "npcs": hideNpcs = on; if (!on) ShowNpcs(); else nextNpcPass = 0; break;
                case "bright": if (!on && bright) RestoreLight(); bright = on; ApplyLight(); break;
                case "fog": if (!on && noFog) RestoreLight(); noFog = on; ApplyLight(); break;
                default: return "{\"error\":\"unknown toggle\"}";
            }
            return Tools(r);
        }

        internal static string Tools(LiveRequest r)
        {
            return "{\"npcs\":" + B(hideNpcs) + ",\"bright\":" + B(bright) + ",\"fog\":" + B(noFog) + ",\"zone\":" + LiveLink.Quote(Game.Zone) +
                   ",\"loading\":" + B(Universe.levelLoading) + "}";
        }
        static string B(bool b) { return b ? "true" : "false"; }
        static string JsonField(string json, string key)
        {
            if (json == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ------------------------------------------------------------ every frame
        internal static void Update()
        {
            if (!Game.InWorld) return;
            if (hideNpcs && Time.realtimeSinceStartup >= nextNpcPass) { nextNpcPass = Time.realtimeSinceStartup + 1.5f; HideNpcs(); }
            if (bright || noFog) ApplyLight();   // areas change the light as you go: keep ours
        }

        internal static void ZoneLoaded() { hiddenRenderers.Clear(); hiddenNpcs.Clear(); haveSaved = false; nextNpcPass = 0; }

        static void HideNpcs()
        {
            var me = Game.Stats;
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s == null || s == me || !s.gameObject.scene.IsValid()) continue;
                if (hiddenNpcs.Contains(s.GetInstanceID())) continue;   // done before
                bool any = false;
                foreach (var rend in s.GetComponentsInChildren<Renderer>(true))
                    if (rend.enabled) { rend.enabled = false; hiddenRenderers.Add(rend); any = true; }
                if (any) hiddenNpcs.Add(s.GetInstanceID());   // (no look yet: try again next pass)
            }
        }

        /// <summary>Did the "hide NPCs" toggle switch this renderer off? (Pictures of NPCs show them anyway.)</summary>
        public static bool HiddenByEditor(Renderer r) { return hideNpcs && r != null && hiddenRenderers.Contains(r); }

        static void ShowNpcs()
        {
            foreach (var rend in hiddenRenderers) if (rend != null) rend.enabled = true;
            hiddenRenderers.Clear(); hiddenNpcs.Clear();
        }

        static void ApplyLight()
        {
            if (!bright && !noFog) return;
            if (!haveSaved) { savedAmbient = RenderSettings.ambientLight; savedAmbientIntensity = RenderSettings.ambientIntensity; savedFog = RenderSettings.fog; savedAmbientMode = RenderSettings.ambientMode; haveSaved = true; }
            if (bright) { RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.75f, 0.75f, 0.72f); RenderSettings.ambientIntensity = 1.3f; }
            if (noFog) RenderSettings.fog = false;
        }

        /// <summary>Put back the game's own light and fog (what it was when our toggles went on).</summary>
        static void RestoreLight()
        {
            if (!haveSaved) return;
            if (bright) { RenderSettings.ambientMode = savedAmbientMode; RenderSettings.ambientLight = savedAmbient; RenderSettings.ambientIntensity = savedAmbientIntensity; }
            if (noFog) RenderSettings.fog = savedFog;
            if (!(bright && noFog)) haveSaved = false;   // nothing of ours left on
        }
    }
}
