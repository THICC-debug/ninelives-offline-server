using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace WorldLoader
{
    /// <summary>
    /// Build mode (F8, or from the editor): click one of your placed things or NPCs in the game and drag it
    /// along the ground. While one is picked: [ ] turn it, PageUp/PageDown raise or lower it, + - resize,
    /// Ctrl+D copy, Delete remove. Alt+click anything else in the world to pick it in the editor.
    /// The game only shows the change: every edit is sent to the editor (/world/build/events), which keeps
    /// it as an unsaved change of the pack, so Save and Discard work as usual.
    /// The editor's game view drives the same pointer (/world/view/pointer).
    /// </summary>
    public partial class WorldLoader
    {
        const int MouseKey = 0x4E4C5702;          // our key in the game's "the mouse is busy" bookkeeping
        bool building, mouseHeld;

        // what's picked
        Transform selT; string selKind, selId, selName;
        // dragging
        bool dragging, dragMoved; Vector3 grabOffset; float heightOffset; Vector2 downAt;
        float nextGlow;

        readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        int eventNo;

        // ------------------------------------------------------------ every frame
        void BuildUpdate()
        {
            if (EditorMode.On && Game.InWorld && !building) { building = true; Push(new Dictionary<string, object> { { "type", "build" }, { "on", true } }); }   // the editor's 3D view: always building
            else if (Input.GetKeyDown(KeyCode.F8) && Game.InWorld && !EditorMode.On) SetBuilding(!building);
            if (!building) return;
            if (!Game.InWorld) { DropNewCarry(); SetBuilding(false); return; }
            if (selT == null && selId != null) { DropNewCarry(); Deselect(); }

            var cam = Camera.main;
            if (cam == null) { EditorMode.MouseBusy = false; return; }
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            if (EditorMode.On) { ToolsUpdate(cam, ray); return; }   // the editor's 3D view: Unity-style tools (Tools.cs)
            string kind, id; Transform root;
            bool overMine = Hit(ray, out root, out kind, out id);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            // Keep the game from also acting on the click (walking there, targeting, talking).
            bool want = overMine || dragging || (alt && Input.GetMouseButton(0));
            if (want && !Universe.isLockUI) mouseHeld = Universe.GUIOnMouseEnter(MouseKey, mouseHeld);
            else if (mouseHeld) ReleaseMouse();

            if (Input.GetMouseButtonDown(0)) PointerDown(ray, alt, Input.mousePosition);
            else if (dragging && Input.GetMouseButton(0)) PointerMove(ray, Input.mousePosition);
            if (dragging && Input.GetMouseButtonUp(0)) PointerUp();
            if (dragging)
            {
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.01f) Turn(wheel > 0 ? 15f : -15f);
            }
            Keys();
            if (selT != null && Time.realtimeSinceStartup > nextGlow) { nextGlow = Time.realtimeSinceStartup + 2.4f; Glow(selT); }
        }

        /// <summary>Leaving the world (a zone change) while carrying something new: the editor drops it too.</summary>
        void DropNewCarry()
        {
            if (!carrying || !carryNew || carryKind == null) return;
            Push(new Dictionary<string, object> { { "type", "delete" }, { "kind", carryKind }, { "id", carryId }, { "cancel", true } });
            carrying = false;
        }

        void SetBuilding(bool on)
        {
            building = on;
            if (!on) { if (dragging) PointerUp(); Deselect(); if (mouseHeld) ReleaseMouse(); }
            Messages.Show(on ? "Build mode on (F8 to leave)" : "Build mode off", 2.5f);
            Push(new Dictionary<string, object> { { "type", "build" }, { "on", on } });
        }

        void ReleaseMouse()
        {
            mouseHeld = Universe.GUIOnMouseOut(MouseKey);
            Universe.GUIForceClearDragUsing(MouseKey);
        }

        // ------------------------------------------------------------ pointer (the mouse in game, or the editor's game view)
        void PointerDown(Ray ray, bool alt, Vector2 screen)
        {
            string kind, id; Transform root;
            if (Hit(ray, out root, out kind, out id))
            {
                Select(root, kind, id);
                dragging = true; dragMoved = false; downAt = screen;
                RaycastHit g;
                if (GroundUnder(ray, out g)) grabOffset = selT.position - g.point; else grabOffset = Vector3.zero;
                grabOffset.y = 0;
                RaycastHit below;
                heightOffset = GroundBelow(selT.position, out below) ? selT.position.y - below.point.y : 0f;
                return;
            }
            if (alt) { PickScenery(ray); return; }
            Deselect();
        }

        void PointerMove(Ray ray, Vector2 screen)
        {
            if (!dragging || selT == null) return;
            if (!dragMoved && (screen - downAt).magnitude < 4f) return;
            dragMoved = true;
            RaycastHit g;
            if (!GroundUnder(ray, out g)) return;
            var p = g.point + grabOffset;
            RaycastHit below;
            p.y = GroundBelow(p, out below) ? below.point.y + heightOffset : selT.position.y;
            selT.position = p;
        }

        void PointerUp()
        {
            bool moved = dragMoved;
            dragging = false; dragMoved = false;
            if (moved) Report("move");
        }

        // ------------------------------------------------------------ keys while something is picked
        void Keys()
        {
            if (selT == null) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (Input.GetKeyDown(KeyCode.LeftBracket)) Turn(shift ? -5f : -15f);
            if (Input.GetKeyDown(KeyCode.RightBracket)) Turn(shift ? 5f : 15f);
            if (Input.GetKeyDown(KeyCode.PageUp)) Raise(shift ? 0.5f : 0.1f);
            if (Input.GetKeyDown(KeyCode.PageDown)) Raise(shift ? -0.5f : -0.1f);
            if (selKind == "world" && (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))) Resize(1.1f);
            if (selKind == "world" && (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))) Resize(1f / 1.1f);
            if (ctrl && Input.GetKeyDown(KeyCode.D) && selKind == "world") { Push(new Dictionary<string, object> { { "type", "copy" }, { "kind", "world" }, { "id", selId } }); Messages.Show("Copied " + selName, 2f); }
            if (Input.GetKeyDown(KeyCode.Delete))
            {
                Push(new Dictionary<string, object> { { "type", "delete" }, { "kind", selKind }, { "id", Person(selKind) ? (object)NpcId(selT) : selId } });
                Messages.Show("Removed " + selName + " (Discard changes in the editor brings it back)", 3f);
                if (selKind == "world") Remove(selId); else selT.gameObject.SetActive(false);
                Deselect();
            }
        }

        void Turn(float deg)
        {
            if (selT == null) return;
            if (Person(selKind)) selT.rotation = Quaternion.Euler(0, Mathf.Round(selT.eulerAngles.y + deg), 0);
            else selT.Rotate(0, deg, 0, Space.World);
            if (!dragging) Report("move");
        }
        void Raise(float m) { if (selT == null) return; selT.position += Vector3.up * m; heightOffset += m; if (!dragging) Report("move"); }
        void Resize(float f) { if (selT == null) return; selT.localScale = Vector3.one * Mathf.Clamp(selT.localScale.x * f, 0.05f, 50f); Report("move"); }

        // ------------------------------------------------------------ picking
        void Select(Transform root, string kind, string id)
        {
            selT = root; selKind = kind; selId = id;
            selName = Person(kind) ? NpcName(root) : id;
            EditorMode.Selected = selName;
            nextGlow = 0;
            Push(new Dictionary<string, object> { { "type", "select" }, { "kind", kind }, { "id", Person(kind) ? (object)NpcId(root) : id } });
        }
        void Deselect() { selT = null; selKind = selId = selName = null; dragging = false; active = hot = Handle.None; carrying = false; menuOpen = moveOpen = false; EditorMode.Selected = null; EditorMode.HasPivot = false; EditorMode.MouseBusy = false; }

        /// <summary>Is the ray on one of ours (a placed copy, a custom NPC)?</summary>
        bool Hit(Ray ray, out Transform root, out string kind, out string id)
        {
            root = null; kind = id = null;
            var player = Game.Player != null ? Game.Player.transform : null;
            foreach (var h in Physics.RaycastAll(ray, 300f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide).OrderBy(h => h.distance))
            {
                if (player != null && h.transform.IsChildOf(player)) continue;
                for (var t = h.transform; t != null; t = t.parent)
                {
                    if (t.name.StartsWith("WorldObj_", StringComparison.Ordinal)) { root = t; kind = "world"; id = t.name.Substring(9); return true; }
                    if (t.name.StartsWith("ModNPC_", StringComparison.Ordinal)) { root = t; kind = "npc"; id = t.name; return true; }
                    if (t.name.StartsWith("ModEnemy_", StringComparison.Ordinal)) { root = t; kind = "enemy"; id = t.name; return true; }   // EnemyLoader's
                }
                if (!h.collider.isTrigger) return false;   // something solid in front: not ours
            }
            return false;
        }

        static int NpcId(Transform t) { var p = t.name.Split('_'); int id; return p.Length > 1 && int.TryParse(p[1], out id) ? id : 0; }
        static string NpcName(Transform t)
        {
            if (t.name.StartsWith("ModEnemy_", StringComparison.Ordinal)) { var st = t.GetComponentInChildren<CommonStats>(); return st != null && !string.IsNullOrEmpty(st.dispName) ? st.dispName : "enemy"; }
            var p = t.name.Split(new[] { '_' }, 3); return p.Length > 2 ? p[2] : t.name;
        }
        /// <summary>Custom NPCs and EnemyLoader's monsters: an id number, turned only around up.</summary>
        static bool Person(string kind) { return kind == "npc" || kind == "enemy"; }

        /// <summary>Where the ray meets the ground, ignoring what's being dragged and the player.</summary>
        bool GroundUnder(Ray ray, out RaycastHit hit)
        {
            var player = Game.Player != null ? Game.Player.transform : null;
            foreach (var h in Physics.RaycastAll(ray, 400f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (selT != null && h.transform.IsChildOf(selT)) continue;
                if (player != null && h.transform.IsChildOf(player)) continue;
                hit = h; return true;
            }
            hit = default(RaycastHit); return false;
        }
        bool GroundBelow(Vector3 p, out RaycastHit hit) { return GroundUnder(new Ray(p + Vector3.up * 3f, Vector3.down), out hit); }

        void PickScenery(Ray ray)
        {
            var player = Game.Player.transform;
            var hits = Physics.RaycastAll(ray, 400f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                              .Where(h => !h.transform.IsChildOf(player)).OrderBy(h => h.distance).ToList();
            if (hits.Count == 0) return;
            var chain = new List<Transform>();
            for (var t = hits[0].transform; t != null; t = t.parent) chain.Add(t);
            int sug = Suggest(chain);
            Glow(chain[sug]);
            Messages.Show("Picked " + chain[sug].name + ": see the editor's inspector", 2.5f);
            Push(new Dictionary<string, object> { { "type", "pick" }, { "zone", Game.Zone }, { "suggested", sug },
                { "chain", chain.Select(t => Describe(t, Game.PlayerPosition)).ToList() } });
        }

        // ------------------------------------------------------------ events for the editor
        void Report(string type)
        {
            if (selT == null) return;
            var e = new Dictionary<string, object> { { "type", type }, { "kind", selKind }, { "zone", Game.Zone },
                { "x", R(selT.position.x) }, { "y", R(selT.position.y) }, { "z", R(selT.position.z) } };
            if (Person(selKind)) { e["id"] = NpcId(selT); e["rotation"] = R(selT.eulerAngles.y); }
            else
            {
                e["id"] = selId;
                var a = selT.eulerAngles;
                e["rx"] = R(Wrap(a.x)); e["ry"] = R(a.y); e["rz"] = R(Wrap(a.z)); e["scale"] = R(selT.localScale.x);
            }
            Push(e);
        }
        static double R(float v) { return Math.Round(v, 2); }
        static float Wrap(float a) { return a > 180f ? a - 360f : a; }

        void Push(Dictionary<string, object> e)
        {
            e["n"] = ++eventNo;
            events.Add(e);
            if (events.Count > 200) events.RemoveRange(0, events.Count - 200);
        }

        // ------------------------------------------------------------ on screen
        static GUIStyle hintStyle, menuTitleStyle, menuButtonStyle;
        string lastHint; float hintSince;

        void BuildGUI()
        {
            if (!building || !Game.InWorld) return;
            if (EditorMode.On)
            {
                var hint = carrying ? "Click to put it down   ·   wheel or , . turns it   ·   Shift+click keeps placing   ·   Esc cancels"
                    : selT == null ? "Click one of your placed things or NPCs (right-click for a menu)   ·   Q select  W move  E rotate  R scale   ·   double-click scenery to inspect it"
                    : selName + "   ·   drag it, or its handles   ·   right-click for more   ·   Ctrl+D copy   ·   Del delete   ·   F frame   ·   Ctrl = snap";
                var hs = hintStyle ?? (hintStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleCenter });
                // The general tips fade out after a while (they'd cover the view); tips for what you're doing stay.
                if (hint != lastHint) { lastHint = hint; hintSince = Time.realtimeSinceStartup; }
                float alpha = selT == null && !carrying ? Mathf.Clamp01(1f - (Time.realtimeSinceStartup - hintSince - 10f)) : 1f;
                if (alpha > 0f)
                {
                    var hc = new GUIContent(hint);
                    float hw = Mathf.Min(hs.CalcSize(hc).x + 24, Screen.width - 16), hh = hs.CalcHeight(hc, hw);   // wraps in a narrow view
                    var oldColor = GUI.color; GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.Box(new Rect((Screen.width - hw) / 2f, 8f, hw, hh + 6), hc, hs);
                    GUI.color = oldColor;
                }
                MenuGUI();
                return;
            }
            var help = selT == null
                ? (EditorMode.On ? "Click one of your placed things or NPCs to move it   ·   Alt+click anything to pick it in the editor"
                                 : "BUILD MODE   ·   click one of your placed things or NPCs   ·   Alt+click anything to pick it in the editor   ·   F8 to leave")
                : "Picked: " + selName + "   ·   drag to move (wheel turns)   ·   [ ] turn   ·   PgUp PgDn up/down" + (selKind == "world" ? "   ·   + - size   ·   Ctrl+D copy" : "") + "   ·   Del remove";
            var st = new GUIStyle(GUI.skin.box) { fontSize = 14, wordWrap = false, alignment = TextAnchor.MiddleCenter };
            var size = st.CalcSize(new GUIContent(help));
            GUI.Box(new Rect((Screen.width - size.x - 24) / 2f, 8f, size.x + 24, size.y + 10), help, st);
            if (selT != null && Camera.main != null)
            {
                var sp = Camera.main.WorldToScreenPoint(selT.position + Vector3.up * 2.4f);
                if (sp.z > 0)
                {
                    var lbl = "▼ " + selName;
                    var ls = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                    ls.normal.textColor = new Color(1f, 0.85f, 0.3f);
                    GUI.Label(new Rect(sp.x - 150, Screen.height - sp.y - 14, 300, 28), lbl, ls);
                }
            }
        }

        // ------------------------------------------------------------ requests from the editor
        string BuildLive(LiveRequest r)
        {
            var tl = ToolsLive(r); if (tl != null) return tl;
            switch (r.Path)
            {
                case "/world/build":   // GET: state; POST ?on=1|0
                    if (r.Method == "POST") SetBuilding(r.Get("on") == "1");
                    return "{\"on\":" + (building ? "true" : "false") + ",\"last\":" + eventNo + "}";
                case "/world/build/events":
                {
                    int after = 0; int.TryParse(r.Get("after") ?? "0", out after);
                    if (after > eventNo) after = 0;   // the game was restarted
                    return JsonConvert.SerializeObject(new Dictionary<string, object> {
                        { "on", building }, { "last", eventNo }, { "events", events.Where(e => (int)e["n"] > after).ToList() } });
                }
                case "/world/view":   // the game camera as a picture (no game UI)
                {
                    int w = 640; int.TryParse(r.Get("w") ?? "640", out w); w = Mathf.Clamp(w, 160, 1280);
                    var cam = Camera.main;
                    if (cam == null) return "{\"error\":\"No camera.\"}";
                    int h = Mathf.RoundToInt(w * (float)Screen.height / Mathf.Max(1, Screen.width));
                    return "{\"w\":" + w + ",\"h\":" + h + ",\"build\":" + (building ? "true" : "false") + ",\"jpg\":\"" + Convert.ToBase64String(Snap(cam, w, h, -1)) + "\"}";
                }
                case "/world/view/pointer":   // the editor's game view: u, v (0-1 from top left), act = down|move|up
                {
                    var cam = Camera.main;
                    if (cam == null) return "{\"error\":\"No camera.\"}";
                    if (!building) SetBuilding(true);
                    float u = 0, v = 0;
                    float.TryParse(r.Get("u"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out u);
                    float.TryParse(r.Get("v"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
                    var ray = cam.ViewportPointToRay(new Vector3(u, 1f - v, 0));
                    var screen = new Vector2(u * Screen.width, (1f - v) * Screen.height);
                    switch (r.Get("act"))
                    {
                        case "down": PointerDown(ray, r.Get("alt") == "1", screen); break;
                        case "move": PointerMove(ray, screen); break;
                        case "up": if (dragging) PointerUp(); break;
                        case "turn": { float d; if (!float.TryParse(r.Get("deg") ?? "15", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) d = 15; Turn(d); break; }
                    }
                    return "{\"ok\":true,\"picked\":" + (selT != null ? LiveLink.Quote(selName) : "null") + "}";
                }
                case "/world/thumb":   // a small picture of one thing, on its own
                {
                    var t = Copier.Find(r.Get("path"));
                    if (t == null) return "{\"error\":\"Not found in this zone.\"}";
                    int s = 128; int.TryParse(r.Get("size") ?? "128", out s); s = Mathf.Clamp(s, 32, 512);
                    return "{\"jpg\":\"" + Convert.ToBase64String(Thumb(t, s)) + "\"}";
                }
                case "/world/npcthumb":   // a small picture of an NPC (from the front)
                {
                    int cid; int.TryParse(r.Get("cid"), out cid);
                    CommonStats who = null;
                    foreach (var o in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
                    {
                        var c = (CommonStats)o;
                        if (c != null && c.getCID() == cid && c.gameObject.scene.IsValid() && !c.gameObject.name.StartsWith("ModNPC_", StringComparison.Ordinal)) { who = c; break; }
                    }
                    if (who == null) return "{\"error\":\"That NPC isn't in this zone.\"}";
                    int s = 128; int.TryParse(r.Get("size") ?? "128", out s); s = Mathf.Clamp(s, 32, 512);
                    return "{\"jpg\":\"" + Convert.ToBase64String(Thumb(who.transform, s, true)) + "\"}";
                }
                case "/world/catalog":   // the kinds of things in this zone, by folder
                    return JsonConvert.SerializeObject(Catalog());
            }
            return null;
        }

        // ------------------------------------------------------------ pictures
        static Texture2D snapTex;
        static byte[] Snap(Camera cam, int w, int h, int mask)
        {
            var rt = RenderTexture.GetTemporary(w, h, 24);
            var prevTarget = cam.targetTexture; var prevMask = cam.cullingMask;
            try
            {
                cam.targetTexture = rt;
                if (mask != -1) cam.cullingMask = mask;
                else BakedDraw.DrawAllFor(cam);   // copies draw themselves late in the frame: queue them for this picture
                cam.Render();
            }
            finally { cam.targetTexture = prevTarget; cam.cullingMask = prevMask; }
            var prevActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                if (snapTex == null || snapTex.width != w || snapTex.height != h) { if (snapTex != null) UnityEngine.Object.Destroy(snapTex); snapTex = new Texture2D(w, h, TextureFormat.RGB24, false); }
                snapTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                snapTex.Apply(false);
                return snapTex.EncodeToJPG(72);
            }
            finally { RenderTexture.active = prevActive; RenderTexture.ReleaseTemporary(rt); }
        }

        const int ThumbLayer = 31;
        static byte[] Thumb(Transform t, int size, bool front = false)
        {
            var b = Copier.BoundsOf(t);
            float radius = Mathf.Max(0.3f, b.extents.magnitude);
            var go = new GameObject("WorldLoader thumb camera");
            var layers = new List<KeyValuePair<GameObject, int>>();
            var shown = new List<Renderer>();
            Light key = null, fill = null; bool changedLight = false, fog = false;
            Color ambient = Color.black; float ambientI = 1f; var ambMode = UnityEngine.Rendering.AmbientMode.Flat;
            try
            {
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    layers.Add(new KeyValuePair<GameObject, int>(r.gameObject, r.gameObject.layer)); r.gameObject.layer = ThumbLayer;
                    if (!r.enabled && EditorTools.HiddenByEditor(r)) { r.enabled = true; shown.Add(r); }
                }
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.33f, 0.35f, 0.38f);
                cam.fieldOfView = 30f; cam.nearClipPlane = 0.05f; cam.farClipPlane = radius * 8f + 10f;
                // things: from above and to the side; people: from the front, a little above
                var dir = front ? Quaternion.Euler(10f, t.eulerAngles.y + 200f, 0) * Vector3.forward : Quaternion.Euler(28f, t.eulerAngles.y + 150f, 0) * Vector3.forward;
                cam.transform.position = b.center - dir * (radius * 0.9f / Mathf.Sin(15f * Mathf.Deg2Rad));
                cam.transform.LookAt(b.center);
                // A little photo studio: a key light, a fill light, bright ambient and no fog, only for the picture.
                key = AddLight(go, cam.transform.rotation * Quaternion.Euler(-25f, 35f, 0), 1.15f, new Color(1f, 0.96f, 0.9f));
                fill = AddLight(go, cam.transform.rotation * Quaternion.Euler(10f, -140f, 0), 0.55f, new Color(0.8f, 0.86f, 1f));
                ambient = RenderSettings.ambientLight; ambientI = RenderSettings.ambientIntensity; fog = RenderSettings.fog; ambMode = RenderSettings.ambientMode;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.64f); RenderSettings.ambientIntensity = 1f; RenderSettings.fog = false;
                changedLight = true;
                return Snap(cam, size, size, 1 << ThumbLayer);
            }
            finally
            {
                if (changedLight) { RenderSettings.ambientMode = ambMode; RenderSettings.ambientLight = ambient; RenderSettings.ambientIntensity = ambientI; RenderSettings.fog = fog; }
                foreach (var kv in layers) if (kv.Key != null) kv.Key.layer = kv.Value;
                foreach (var r in shown) if (r != null) r.enabled = false;
                if (key != null) UnityEngine.Object.Destroy(key.gameObject);
                if (fill != null) UnityEngine.Object.Destroy(fill.gameObject);
                UnityEngine.Object.Destroy(go);
            }
        }

        static Light AddLight(GameObject parent, Quaternion rot, float intensity, Color color)
        {
            var l = new GameObject("WorldLoader thumb light").AddComponent<Light>();
            l.type = LightType.Directional; l.transform.rotation = rot; l.intensity = intensity; l.color = color;
            l.cullingMask = 1 << ThumbLayer; l.shadows = LightShadows.None;
            return l;
        }

        /// <summary>Kinds of things (by name) in this zone, grouped by their folder, with one example each.</summary>
        /// <summary>
        /// Only real props in Assets: not things held by a character or monster (on a skeleton's "Bip01…" bones,
        /// or under anything animated), not effects (only particles), not things with no size.
        /// </summary>
        static bool IsProp(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent)
            {
                if (p.name.StartsWith("Bip0", StringComparison.Ordinal) || p.GetComponent<Animator>() != null || p.GetComponent<Animation>() != null) return false;
                if (p.GetComponent<CommonStats>() != null) return false;
            }
            bool solid = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                if (!(r is ParticleRenderer) && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer)) { solid = true; break; }
            if (!solid) return false;
            return Copier.BoundsOf(t).size.magnitude >= 0.15f;
        }

        object Catalog()
        {
            var me = Game.PlayerPosition;
            var kinds = new Dictionary<string, Dictionary<string, object>>();
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name.StartsWith("WorldObj_", StringComparison.Ordinal) || root.name.StartsWith("ModNPC_", StringComparison.Ordinal)) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var n = t.name;
                    if (!(n.EndsWith("_prefab", StringComparison.OrdinalIgnoreCase) || n.EndsWith(" prefab", StringComparison.OrdinalIgnoreCase))) continue;
                    if (Copier.IsCharacter(t) || t.GetComponentInChildren<Renderer>(true) == null) continue;
                    if (!IsProp(t)) continue;
                    var name = n.Replace("(Clone)", "").Trim();
                    var folder = t.parent != null ? t.parent.name.TrimStart('_').Replace('_', ' ') : "";
                    var key = folder + "/" + name;
                    float d = Vector3.Distance(t.position, me);
                    Dictionary<string, object> k;
                    if (!kinds.TryGetValue(key, out k))
                        kinds[key] = k = new Dictionary<string, object> { { "name", name }, { "folder", folder }, { "count", 0 }, { "dist", float.MaxValue } };
                    k["count"] = (int)k["count"] + 1;
                    if (d < (float)k["dist"]) { k["dist"] = d; k["path"] = Copier.PathOf(t); k["size"] = R(Copier.BoundsOf(t).size.magnitude); k["ry"] = R(t.eulerAngles.y); }
                }
            }
            return new Dictionary<string, object> { { "zone", Game.Zone },
                { "kinds", kinds.Values.OrderBy(k => (string)k["folder"]).ThenBy(k => (string)k["name"]).Select(k => { k["dist"] = R((float)k["dist"]); return k; }).ToList() } };
        }
    }
}
