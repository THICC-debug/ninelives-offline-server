using System;
using System.Collections.Generic;
using System.Linq;
using NineLivesModLoader;
using UnityEngine;

namespace WorldLoader
{
    /// <summary>
    /// The editor's 3D view tools, like Unity / Roblox Studio / the Creation Kit:
    ///   Q select · W move · E rotate · R scale (handles drawn on the selected thing), a box around the selection,
    ///   snapping to a grid and to angle steps (Ctrl flips it while you drag), and placing by carrying: a new thing
    ///   follows the mouse over the ground until you click (Shift+click keeps stamping copies, Esc cancels).
    ///   Ctrl+D copies, Del deletes, Ctrl+Z undo, Ctrl+S save, double-click scenery to inspect it.
    /// Only in editor mode (the game as the editor's 3D view); the F8 build mode in a normal game is unchanged.
    /// </summary>
    public partial class WorldLoader
    {
        enum Tool { Select, Move, Rotate, Scale }
        enum Handle { None, Body, X, Y, Z, RotX, RotY, RotZ, Scale }
        Tool tool = Tool.Move;
        bool snap; float grid = 0.5f, angleStep = 15f;
        Handle hot, active;
        Vector3 startPos; Quaternion startRot; float startScale; float startT; float startAngle, lastAngle, turned; Vector2 startMouse;
        bool carrying, carryNew; string carryKind; object carryId; Vector3 carryPrevPos; Quaternion carryPrevRot; float carryYaw, carryYaw0;
        float rmbDownAt; Vector2 rmbDownPos;
        float lastClick; Vector2 lastClickPos;
        static Material lineMat;
        GizmoDrawer drawer;

        static readonly Color ColX = new Color(0.95f, 0.3f, 0.3f), ColY = new Color(0.45f, 0.9f, 0.35f), ColZ = new Color(0.35f, 0.6f, 1f),
                              ColHot = new Color(1f, 0.85f, 0.2f), ColBox = new Color(1f, 0.8f, 0.25f, 0.9f);

        // ------------------------------------------------------------ every frame (editor mode)
        void ToolsUpdate(Camera cam, Ray ray)
        {
            if (drawer == null) { var g = new GameObject("WorldLoader gizmos"); UnityEngine.Object.DontDestroyOnLoad(g); drawer = g.AddComponent<GizmoDrawer>(); drawer.owner = this; }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool rmb = Input.GetMouseButton(1);
            bool snapNow = snap ^ ctrl;

            // whole-editor keys
            if (ctrl && !rmb && !carrying && active == Handle.None && Input.GetKeyDown(KeyCode.Z)) Push(new Dictionary<string, object> { { "type", "undo" } });
            if (ctrl && !rmb && !carrying && Input.GetKeyDown(KeyCode.S)) Push(new Dictionary<string, object> { { "type", "save" } });
            if (!rmb && !ctrl && !carrying)
            {
                if (Input.GetKeyDown(KeyCode.Q)) SetTool(Tool.Select);
                if (Input.GetKeyDown(KeyCode.W)) SetTool(Tool.Move);
                if (Input.GetKeyDown(KeyCode.E)) SetTool(Tool.Rotate);
                if (Input.GetKeyDown(KeyCode.R)) SetTool(Tool.Scale);
            }
            if (Input.GetMouseButtonDown(1)) { rmbDownAt = Time.realtimeSinceStartup; rmbDownPos = Input.mousePosition; }

            if (carrying) { CarryUpdate(cam, ray, shift, snapNow); PublishPivot(); return; }
            if (MenuUpdate(cam)) { PublishPivot(); return; }   // the right-click menu (ContextMenu.cs)

            hot = active != Handle.None ? active : (selT != null && !rmb && !alt ? HandleUnder(cam, Input.mousePosition) : Handle.None);
            EditorMode.MouseBusy = active != Handle.None;

            if (Input.GetMouseButtonDown(0) && !alt && !rmb)
            {
                Vector2 m = Input.mousePosition;
                bool dbl = Time.realtimeSinceStartup - lastClick < 0.35f && (m - lastClickPos).magnitude < 6f;
                lastClick = Time.realtimeSinceStartup; lastClickPos = m;
                string kind, id; Transform root;
                if (hot != Handle.None) BeginDrag(hot, cam, ray);
                else if (Hit(ray, out root, out kind, out id))
                {
                    if (root != selT) Select(root, kind, id);
                    BeginDrag(Handle.Body, cam, ray);   // dragging the thing itself moves it along the ground
                }
                else if (dbl) PickScenery(ray);
                else Deselect();
            }
            if (active != Handle.None)
            {
                if (Input.GetMouseButton(0)) Drag(cam, ray, snapNow);
                if (Input.GetMouseButtonUp(0)) EndDrag();
                if (active == Handle.Body)
                {
                    float wheel = Input.GetAxis("Mouse ScrollWheel");
                    if (Mathf.Abs(wheel) > 0.01f) { Spin(wheel > 0 ? Step(snapNow, 15f) : -Step(snapNow, 15f)); dragMoved = true; }
                }
            }
            if (selT != null && !rmb && active == Handle.None) SelectionKeys(ctrl, shift);
            PublishPivot();
        }

        float Step(bool snapNow, float free) { return snapNow ? angleStep : free; }

        void SetTool(Tool t)
        {
            tool = t;
            Push(new Dictionary<string, object> { { "type", "tool" }, { "tool", t.ToString().ToLowerInvariant() } });
            UpdateToolText();
        }
        void UpdateToolText()
        {
            EditorMode.ToolText = "tool: " + tool.ToString().ToLowerInvariant() + (snap ? "   snap " + grid.ToString("0.##") + " m / " + angleStep.ToString("0") + "°" : "   snap off");
        }

        int boundsFrame = -1; Transform boundsFor; Bounds boundsCache;
        Bounds SelBounds()
        {
            if (boundsFrame != Time.frameCount || boundsFor != selT) { boundsCache = WorldBounds(selT); boundsFrame = Time.frameCount; boundsFor = selT; }
            return boundsCache;
        }

        void PublishPivot()
        {
            if (selT == null) { EditorMode.HasPivot = false; return; }
            var b = SelBounds();
            EditorMode.HasPivot = true; EditorMode.Pivot = b.center; EditorMode.PivotSize = b.extents.magnitude;
        }

        // ------------------------------------------------------------ keys while something is picked
        void SelectionKeys(bool ctrl, bool shift)
        {
            if (ctrl && Input.GetKeyDown(KeyCode.D)) { CopyAndCarry(); return; }
            if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
            {
                Push(new Dictionary<string, object> { { "type", "delete" }, { "kind", selKind }, { "id", Person(selKind) ? (object)NpcId(selT) : selId } });
                Messages.Show("Deleted " + selName + (Person(selKind) ? "  (Discard brings it back until you save)" : "  (Ctrl+Z brings it back)"), 2.5f);
                if (selKind == "world") Remove(selId); else selT.gameObject.SetActive(false);
                Deselect(); return;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { Deselect(); return; }
            if (Input.GetKeyDown(KeyCode.Comma) || Input.GetKeyDown(KeyCode.LeftBracket)) { Spin(shift ? -5f : -Step(snap, 15f)); Report("move"); }
            if (Input.GetKeyDown(KeyCode.Period) || Input.GetKeyDown(KeyCode.RightBracket)) { Spin(shift ? 5f : Step(snap, 15f)); Report("move"); }
            if (Input.GetKeyDown(KeyCode.PageUp)) { selT.position += Vector3.up * (snap ? grid : shift ? 0.5f : 0.1f); Report("move"); }
            if (Input.GetKeyDown(KeyCode.PageDown)) { selT.position -= Vector3.up * (snap ? grid : shift ? 0.5f : 0.1f); Report("move"); }
            if (selKind == "world" && (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))) Resize(1.1f);
            if (selKind == "world" && (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))) Resize(1f / 1.1f);
        }

        void Spin(float deg)
        {
            if (selT == null) return;
            if (Person(selKind)) selT.rotation = Quaternion.Euler(0, Mathf.Round(selT.eulerAngles.y + deg), 0);
            else selT.Rotate(0, deg, 0, Space.World);
        }

        void CopyAndCarry()
        {
            if (selT == null) return;
            Push(new Dictionary<string, object> { { "type", "copy" }, { "kind", selKind }, { "id", Person(selKind) ? (object)NpcId(selT) : selId }, { "carry", true } });
            Messages.Show("Copied " + selName + ": click to put it down", 2f);
        }

        // ------------------------------------------------------------ handles
        float HandleLen(Camera cam, Vector3 pivot) { return Mathf.Max(0.4f, Vector3.Distance(cam.transform.position, pivot) * 0.16f); }

        Handle HandleUnder(Camera cam, Vector2 mouse)
        {
            if (selT == null || tool == Tool.Select) return Handle.None;
            var pv = selT.position; float L = HandleLen(cam, pv);
            var p0 = cam.WorldToScreenPoint(pv); if (p0.z <= 0) return Handle.None;
            Handle best = Handle.None; float bestD = 10f;
            Action<Handle, float> consider = (h, d) => { if (d < bestD) { bestD = d; best = h; } };
            if (tool == Tool.Move)
            {
                var view = (pv - cam.transform.position).normalized;
                foreach (var a in new[] { Handle.X, Handle.Y, Handle.Z })
                    if (Mathf.Abs(Vector3.Dot(view, AxisOf(a))) < 0.9f) consider(a, SegDist(mouse, p0, cam.WorldToScreenPoint(pv + AxisOf(a) * L)));
                if (((Vector2)p0 - mouse).magnitude < 9f) consider(Handle.Body, 0f);
            }
            else if (tool == Tool.Rotate)
            {
                foreach (var a in Person(selKind) ? new[] { Handle.RotY } : new[] { Handle.RotX, Handle.RotY, Handle.RotZ })
                    if (Mathf.Abs(Vector3.Dot((pv - cam.transform.position).normalized, AxisOf(a))) > 0.15f) consider(a, RingDist(cam, mouse, pv, AxisOf(a), L * 0.85f));
            }
            else if (tool == Tool.Scale && selKind == "world")
            {
                var top = cam.WorldToScreenPoint(pv + Vector3.up * L);
                consider(Handle.Scale, Mathf.Min(SegDist(mouse, p0, top), ((Vector2)top - mouse).magnitude - 4f));
            }
            return best;
        }

        static Vector3 AxisOf(Handle h)
        {
            switch (h) { case Handle.X: case Handle.RotX: return Vector3.right; case Handle.Y: case Handle.RotY: return Vector3.up; default: return Vector3.forward; }
        }
        static float SegDist(Vector2 m, Vector3 a3, Vector3 b3)
        {
            if (a3.z <= 0 || b3.z <= 0) return 999f;
            Vector2 a = a3, b = b3, ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(m - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
            return (a + ab * t - m).magnitude;
        }
        static float RingDist(Camera cam, Vector2 m, Vector3 c, Vector3 axis, float r)
        {
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.forward : Vector3.up).normalized, v = Vector3.Cross(axis, u);
            float best = 999f; Vector3 prev = Vector3.zero;
            for (int i = 0; i <= 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                var p = cam.WorldToScreenPoint(c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r);
                if (i > 0) best = Mathf.Min(best, SegDist(m, prev, p));
                prev = p;
            }
            return best;
        }

        // ------------------------------------------------------------ dragging
        void BeginDrag(Handle h, Camera cam, Ray ray)
        {
            if (selT == null) return;
            active = h; dragMoved = false; downAt = Input.mousePosition;
            startPos = selT.position; startRot = selT.rotation; startScale = selT.localScale.x; startMouse = Input.mousePosition;
            if (h == Handle.Body)
            {
                RaycastHit g;
                grabOffset = GroundUnder(ray, out g) ? selT.position - g.point : Vector3.zero; grabOffset.y = 0;
                RaycastHit below;
                heightOffset = GroundBelow(selT.position, out below) ? selT.position.y - below.point.y : 0f;
            }
            else if (h == Handle.X || h == Handle.Y || h == Handle.Z) startT = AxisParam(ray, startPos, AxisOf(h));
            else if (h == Handle.RotX || h == Handle.RotY || h == Handle.RotZ)
            {
                var p0 = cam.WorldToScreenPoint(selT.position);
                startAngle = lastAngle = Mathf.Atan2(Input.mousePosition.y - p0.y, Input.mousePosition.x - p0.x) * Mathf.Rad2Deg;
                turned = 0f;
            }
        }

        void Drag(Camera cam, Ray ray, bool snapNow)
        {
            if (selT == null) { active = Handle.None; return; }
            Vector2 m = Input.mousePosition;
            if (!dragMoved && (m - downAt).magnitude < 3f) return;
            dragMoved = true;
            switch (active)
            {
                case Handle.Body:
                {
                    RaycastHit g; if (!GroundUnder(ray, out g)) return;
                    var p = g.point + grabOffset;
                    if (snapNow) { p.x = Snap(p.x, grid); p.z = Snap(p.z, grid); }
                    RaycastHit below;
                    p.y = GroundBelow(p, out below) ? below.point.y + heightOffset : selT.position.y;
                    selT.position = p; break;
                }
                case Handle.X: case Handle.Y: case Handle.Z:
                {
                    var axis = AxisOf(active);
                    if (Mathf.Abs(Vector3.Dot(ray.direction.normalized, axis)) > 0.98f) break;   // looking along the arrow
                    float t = Mathf.Clamp(AxisParam(ray, startPos, axis) - startT, -300f, 300f);
                    var p = startPos + axis * t;
                    if (snapNow) { if (active == Handle.X) p.x = Snap(p.x, grid); else if (active == Handle.Y) p.y = Snap(p.y, grid); else p.z = Snap(p.z, grid); }
                    selT.position = p; break;
                }
                case Handle.RotX: case Handle.RotY: case Handle.RotZ:
                {
                    var axis = AxisOf(active);
                    var p0 = cam.WorldToScreenPoint(startPos);
                    float ang = Mathf.Atan2(m.y - p0.y, m.x - p0.x) * Mathf.Rad2Deg;
                    turned += Mathf.DeltaAngle(lastAngle, ang); lastAngle = ang;
                    float delta = turned;
                    if (Vector3.Dot(startPos - cam.transform.position, axis) > 0) delta = -delta;
                    if (snapNow) delta = Snap(delta, angleStep);
                    selT.rotation = Person(selKind) ? Quaternion.Euler(0, Mathf.Round(startRot.eulerAngles.y - delta), 0)
                                                     : Quaternion.AngleAxis(-delta, axis) * startRot;
                    break;
                }
                case Handle.Scale:
                {
                    float f = Mathf.Exp(((m.y - startMouse.y) + (m.x - startMouse.x)) * 0.006f);
                    float s = Mathf.Clamp(startScale * f, 0.05f, 50f);
                    if (snapNow) s = Mathf.Max(0.1f, Snap(s, 0.1f));
                    selT.localScale = Vector3.one * s; break;
                }
            }
        }

        void EndDrag()
        {
            bool moved = dragMoved; active = Handle.None; dragMoved = false;
            if (moved) Report("move");
        }

        static float Snap(float v, float step) { return step <= 0 ? v : Mathf.Round(v / step) * step; }

        /// <summary>Where along the axis line (through origin) the mouse ray passes closest.</summary>
        static float AxisParam(Ray ray, Vector3 origin, Vector3 axis)
        {
            var w0 = origin - ray.origin;
            float a = Vector3.Dot(axis, axis), b = Vector3.Dot(axis, ray.direction), c = Vector3.Dot(ray.direction, ray.direction);
            float d = Vector3.Dot(axis, w0), e = Vector3.Dot(ray.direction, w0);
            float den = a * c - b * b;
            return Mathf.Abs(den) < 1e-6f ? 0f : (b * e - c * d) / den;
        }

        // ------------------------------------------------------------ carrying (placing something new, or a copy)
        void StartCarry(Transform t, string kind, string id, bool isNew)
        {
            Select(t, kind, id);
            carrying = true; carryNew = isNew;
            carryKind = kind; carryId = Person(kind) ? (object)NpcId(t) : id;
            carryPrevPos = t.position; carryPrevRot = t.rotation; carryYaw = carryYaw0 = t.eulerAngles.y;
            active = Handle.None;
            Messages.Show("Click to put it down  ·  wheel or , . turns it  ·  Shift+click keeps placing  ·  Esc cancels", 3f);
        }

        void CarryUpdate(Camera cam, Ray ray, bool shift, bool snapNow)
        {
            EditorMode.MouseBusy = true;
            if (selT == null) { DropNewCarry(); carrying = false; return; }
            RaycastHit g;
            bool flying = Input.GetMouseButton(1);   // the camera has the mouse (locked in the middle): leave it where it is
            if (!flying && GroundUnder(ray, out g))
            {
                var p = g.point;
                if (snapNow) { p.x = Snap(p.x, grid); p.z = Snap(p.z, grid); RaycastHit below; if (GroundBelow(p, out below)) p.y = below.point.y; }
                selT.position = p;
            }
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            float step = Step(snapNow, 15f);
            if (flying) wheel = 0f;   // (the wheel sets the flying speed then)
            if (wheel > 0.01f || Input.GetKeyDown(KeyCode.Period) || Input.GetKeyDown(KeyCode.RightBracket)) carryYaw += step;
            if (wheel < -0.01f || Input.GetKeyDown(KeyCode.Comma) || Input.GetKeyDown(KeyCode.LeftBracket)) carryYaw -= step;
            selT.rotation = Person(selKind) ? Quaternion.Euler(0, carryYaw, 0) : Quaternion.Euler(0, carryYaw - carryYaw0, 0) * carryPrevRot;

            bool cancel = Input.GetKeyDown(KeyCode.Escape) ||
                          (Input.GetMouseButtonUp(1) && !EditorMode.RmbLooked && Time.realtimeSinceStartup - rmbDownAt < 0.25f && ((Vector2)Input.mousePosition - rmbDownPos).magnitude < 5f);
            if (cancel) { CancelCarry(); return; }
            if (Input.GetMouseButtonDown(0))
            {
                carrying = false;
                Report("move");
                if (shift && selKind == "world") CopyAndCarry();   // stamp: keep placing copies
            }
        }

        void CancelCarry()
        {
            if (!carrying) return;
            carrying = false;
            if (selT == null) return;
            if (carryNew)
            {
                Push(new Dictionary<string, object> { { "type", "delete" }, { "kind", selKind }, { "id", Person(selKind) ? (object)NpcId(selT) : selId }, { "cancel", true } });
                if (selKind == "world") Remove(selId); else selT.gameObject.SetActive(false);
                Deselect();
            }
            else { selT.position = carryPrevPos; selT.rotation = carryPrevRot; }
        }

        /// <summary>Before the editor changes the selection: finish a drag, put down / cancel a carry, close the menu.</summary>
        void Interrupt()
        {
            if (active != Handle.None) EndDrag();
            if (carrying) { if (carryNew) { carrying = false; Report("move"); } else CancelCarry(); }
            menuOpen = false;
        }

        /// <summary>From the editor: start carrying one of ours (a new thing from Assets, a copy).</summary>
        string Carry(LiveRequest r)
        {
            var kind = r.Get("kind") ?? "world"; var id = r.Get("id") ?? "";
            var t = Find(kind, id);
            if (t == null) return "{\"error\":\"Not in the world (yet).\"}";
            Interrupt();
            StartCarry(t, kind, kind == "world" ? id : t.name, r.Get("new") == "1");
            return "{\"ok\":true}";
        }

        Transform Find(string kind, string id)
        {
            if (kind == "world") { GameObject g; return copies.TryGetValue(id, out g) && g != null ? g.transform : null; }
            foreach (var c in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
            {
                var s = (CommonStats)c;
                if (s != null && s.gameObject.scene.IsValid() && s.gameObject.activeInHierarchy && s.gameObject.name.StartsWith("ModNPC_" + id + "_", StringComparison.Ordinal)) return s.transform;
            }
            return null;
        }

        string ToolsState()
        {
            return "{\"tool\":\"" + tool.ToString().ToLowerInvariant() + "\",\"snap\":" + (snap ? "true" : "false") +
                   ",\"grid\":" + grid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"angle\":" + angleStep.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        string ToolsLive(LiveRequest r)
        {
            switch (r.Path)
            {
                case "/world/tool":
                {
                    var n = (r.Get("name") ?? "").ToLowerInvariant();
                    if (n == "select") SetTool(Tool.Select); else if (n == "move") SetTool(Tool.Move); else if (n == "rotate") SetTool(Tool.Rotate); else if (n == "scale") SetTool(Tool.Scale);
                    return ToolsState();
                }
                case "/world/snap":
                {
                    if (r.Get("on") != null) snap = r.Get("on") == "1";
                    float v;
                    if (float.TryParse(r.Get("grid"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0) grid = v;
                    if (float.TryParse(r.Get("angle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0) angleStep = v;
                    UpdateToolText();
                    return ToolsState();
                }
                case "/world/tools": return ToolsState();
                case "/world/carry":
                    if (r.Get("cancel") == "1") { CancelCarry(); return "{\"ok\":true}"; }
                    if (r.Get("drop") == "1") { if (carrying) { carrying = false; Report("move"); } return "{\"ok\":true}"; }
                    return Carry(r);
                case "/world/select":   // from the editor's outliner / inspector: select it here too (handles and box)
                {
                    var t = Find(r.Get("kind") ?? "world", r.Get("id") ?? "");
                    if (t == null) { Interrupt(); Deselect(); return "{\"ok\":false}"; }
                    var kind = r.Get("kind") ?? "world";
                    if (t != selT) Interrupt();
                    if (t != selT) { selT = t; selKind = kind; selId = kind == "world" ? r.Get("id") : t.name; selName = Person(kind) ? NpcName(t) : selId; EditorMode.Selected = selName; }
                    return "{\"ok\":true}";
                }
            }
            return null;
        }

        // ------------------------------------------------------------ drawing the handles and the selection box
        static Bounds WorldBounds(Transform t)
        {
            bool any = false; var b = new Bounds(t.position, Vector3.zero);
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleRenderer || r is ParticleSystemRenderer || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            foreach (var c in t.GetComponentsInChildren<Collider>())
            {
                if (c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            return b;
        }

        internal void DrawGizmos(Camera cam)
        {
            if (selT == null || !EditorMode.On) return;
            if (lineMat == null)
            {
                lineMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                lineMat.SetInt("_ZWrite", 0);
                lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            lineMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, cam.pixelWidth, 0, cam.pixelHeight);
            GL.Begin(GL.QUADS);
            try { DrawShapes(cam); }
            finally { GL.End(); GL.PopMatrix(); }
        }

        void DrawShapes(Camera cam)
        {
            // the selection box
            var b = SelBounds(); var c = b.center; var e = b.extents;
            var k = new Vector3[8];
            for (int i = 0; i < 8; i++) k[i] = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            for (int i = 0; i < 12; i++) Line(cam, k[edges[i, 0]], k[edges[i, 1]], ColBox, 1.6f);
            if (!carrying && tool != Tool.Select)
            {
                var pv = selT.position; float L = HandleLen(cam, pv);
                if (tool == Tool.Move)
                {
                    foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z })
                    {
                        var col = (hot == h) ? ColHot : h == Handle.X ? ColX : h == Handle.Y ? ColY : ColZ;
                        var end = pv + AxisOf(h) * L;
                        Line(cam, pv, end, col, 3.5f);
                        Square(cam, end, col, 7f);
                    }
                    Square(cam, pv, hot == Handle.Body ? ColHot : Color.white, 6f);
                }
                else if (tool == Tool.Rotate)
                {
                    foreach (var h in Person(selKind) ? new[] { Handle.RotY } : new[] { Handle.RotX, Handle.RotY, Handle.RotZ })
                        Ring(cam, pv, AxisOf(h), L * 0.85f, hot == h ? ColHot : h == Handle.RotX ? ColX : h == Handle.RotY ? ColY : ColZ, 3f);
                }
                else if (tool == Tool.Scale && selKind == "world")
                {
                    var top = pv + Vector3.up * L;
                    var col = hot == Handle.Scale ? ColHot : Color.white;
                    Line(cam, pv, top, col, 3.5f); Square(cam, top, col, 9f);
                }
            }
        }

        static void Line(Camera cam, Vector3 a, Vector3 b, Color col, float w)
        {
            var p = cam.WorldToScreenPoint(a); var q = cam.WorldToScreenPoint(b);
            if (p.z <= 0 || q.z <= 0) return;
            var d = ((Vector2)q - (Vector2)p); if (d.sqrMagnitude < 1e-4f) return;
            var n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
            GL.Color(col);
            GL.Vertex3(p.x - n.x, p.y - n.y, 0); GL.Vertex3(p.x + n.x, p.y + n.y, 0);
            GL.Vertex3(q.x + n.x, q.y + n.y, 0); GL.Vertex3(q.x - n.x, q.y - n.y, 0);
        }
        static void Square(Camera cam, Vector3 at, Color col, float r)
        {
            var p = cam.WorldToScreenPoint(at); if (p.z <= 0) return;
            GL.Color(col);
            GL.Vertex3(p.x - r, p.y - r, 0); GL.Vertex3(p.x + r, p.y - r, 0); GL.Vertex3(p.x + r, p.y + r, 0); GL.Vertex3(p.x - r, p.y + r, 0);
        }
        static void Ring(Camera cam, Vector3 c, Vector3 axis, float r, Color col, float w)
        {
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.forward : Vector3.up).normalized, v = Vector3.Cross(axis, u);
            Vector3 prev = c + u * r;
            for (int i = 1; i <= 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                var p = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                Line(cam, prev, p, col, w); prev = p;
            }
        }
    }

    /// <summary>Draws the build handles after the game's camera has drawn the world.</summary>
    public class GizmoDrawer : MonoBehaviour
    {
        internal WorldLoader owner;
        void OnRenderObject()
        {
            var cam = Camera.current;
            if (owner == null || cam == null || cam != Camera.main) return;
            try { owner.DrawGizmos(cam); } catch { }
        }
    }
}
