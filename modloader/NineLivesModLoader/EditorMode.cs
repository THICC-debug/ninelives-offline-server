using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace NineLivesModLoader
{
    /// <summary>
    /// Editor mode: the Nine Lives Editor starts the game in the background (Ninelives.exe -nleditor) as
    /// its 3D view. The game logs in by itself with the editor's own account (the offline server gives it
    /// a "Builder" character), enters the world, hides the character and hands the camera to the editor,
    /// which flies it around freely (POST /editor/cam). No sound, and it keeps running unfocused.
    /// </summary>
    public static class EditorMode
    {
        public const string Account = "nle-editor@local.test";
        public const string Character = "Builder";

        /// <summary>True when the game was started by the editor as its 3D view.</summary>
        public static bool On { get; private set; }
        public static string State { get; private set; }

        static float nextTry; static string lastError;
        static PlayerController setUpFor;
        static Transform cam;
        static Vector3 move; static bool fast; static float moveUntil;
        static float yaw, pitch;
        static float speed = 12f; static Vector3 velocity;
        static GUIStyle readoutStyle; static float nextUiSweep;
        static bool jumped;
        static float pendingYaw, pendingPitch;
        static float lookScale = 1f;   // the 3D tab's Look speed

        /// <summary>What build mode has picked (WorldLoader sets it), for the readout.</summary>
        public static string Selected;
        /// <summary>The selection's middle and size (WorldLoader sets them): Alt+drag orbits it, F frames it.</summary>
        public static bool HasPivot; public static Vector3 Pivot; public static float PivotSize;
        /// <summary>Build tools are using the left mouse button / wheel (a handle, carrying something): don't orbit or zoom.</summary>
        public static bool MouseBusy;
        /// <summary>Shown in the readout: the build tool and snapping (WorldLoader sets it).</summary>
        public static string ToolText;
        /// <summary>Where the right mouse button went down (before the camera grabbed the mouse).</summary>
        public static Vector2 RmbDownPos;
        /// <summary>Set by the build menu's "Frame": fly to the selection next frame.</summary>
        public static bool FrameRequest;
        static float rmbDownTime; static bool rmbLooking, lookedThisPress;

        internal static void Init()
        {
            On = Environment.GetCommandLineArgs().Any(a => a.Equals("-nleditor", StringComparison.OrdinalIgnoreCase));
            State = On ? "starting" : "off";
            if (!On) return;
            Application.runInBackground = true;
            ModLoader.Log("Editor mode: the game is the Nine Lives Editor's 3D view");
        }

        internal static void Update()
        {
            if (!On) return;
            Application.runInBackground = true;
            AudioListener.volume = 0f;
            try
            {
                if (Game.InWorld) InWorld();
                else if (Time.realtimeSinceStartup >= nextTry) { nextTry = Time.realtimeSinceStartup + 2f; GetIn(); }
                if (!Game.InWorld || cam == null) FreeCursor();   // loading a zone, disconnected: never leave the mouse grabbed
            }
            catch (Exception e) { FreeCursor(); if (e.Message != lastError) { lastError = e.Message; ModLoader.Log("Editor mode: " + e); } nextTry = Time.realtimeSinceStartup + 5f; }
        }

        // ------------------------------------------------------------ log in and enter the world by ourselves
        static readonly System.Reflection.FieldInfo NameList = AccessTools.Field(typeof(UI_Menu_CharacterSelecter), "playerNameList");
        static readonly System.Reflection.FieldInfo Items = AccessTools.Field(typeof(UI_Menu_CharacterSelecter), "playerItems");
        static readonly System.Reflection.FieldInfo SelectedField = AccessTools.Field(typeof(UI_Menu_CharacterSelecter), "selectedItemNumber");

        static void GetIn()
        {
            var login = UnityEngine.Object.FindObjectOfType<UI_Login>();
            if (login != null && login.inputEmail != null && login.inputPass != null && login.loginButton != null && login.loginButton.interactable)
            {
                State = "logging in";
                if (login.toggleNewCharacter != null) login.toggleNewCharacter.isOn = false;
                login.inputEmail.text = Account;
                login.inputPass.text = "offline";   // the offline server takes any password
                login.onButtonLogin();
                return;
            }
            var sel = UI_Menu_CharacterSelecter.GetInstance();
            if (sel == null || NameList == null || Items == null) return;
            var names = NameList.GetValue(sel) as System.Collections.IList;
            var items = Items.GetValue(sel) as System.Collections.IList;
            if (names == null || items == null || names.Count == 0 || items.Count < names.Count) { State = "waiting for the character list"; return; }
            int i = -1;
            for (int k = 0; k < names.Count; k++) if ((names[k] as string) == Character) { i = k; break; }
            if (i < 0) { State = "waiting for the Builder character (is the offline server up to date?)"; return; }
            State = "entering the world";
            var go = items[i] as GameObject ?? (items[i] is Component ? ((Component)items[i]).gameObject : null);
            var t = go != null ? go.GetComponent<UnityEngine.UI.Toggle>() : null;
            if (t != null) t.isOn = true;
            if (SelectedField != null) SelectedField.SetValue(sel, i);   // what onChangeCharacterSelect would set
            sel.doEnterTheWorld();
            nextTry = Time.realtimeSinceStartup + 15f;   // loading the zone takes a while
        }

        // ------------------------------------------------------------ in the world: no character, a free camera
        static void InWorld()
        {
            var p = Game.Player;
            if (p != setUpFor)
            {
                var c = Camera.main;
                if (c == null) return;
                var pcc = c.GetComponent<PlayerCameraController>();
                if (pcc != null) { try { pcc.setPlayerLayerVisibility(false); } catch { } pcc.enabled = false; }
                p.enabled = false;   // the character stays where it is
                // A clean view: none of the game's own windows and bars, and clicking NPCs does nothing.
                try { var root = Universe.GetGUIRootCtrl(); if (root != null) root.forceDisableGUIs = true; } catch { }
                try { var ui = Universe.GetUIRoot(); if (ui != null) ui.transform.localScale = Vector3.zero; } catch { }
                foreach (var mrc in UnityEngine.Object.FindObjectsOfType<MouseRayCaster>()) mrc.enabled = false;
                cam = c.transform;
                var e = cam.eulerAngles; yaw = e.y; pitch = e.x > 180f ? e.x - 360f : e.x;
                setUpFor = p;
                State = "in the world";
                if (jumped) { jumped = false; LookAt(p.transform.position); }   // a new zone: start where the character arrived
                ModLoader.Log("Editor mode: in the world (" + Game.Zone + "), camera free");
            }
            if (cam == null) { setUpFor = null; return; }
            try { var root = Universe.GetGUIRootCtrl(); if (root != null) root.forceDisableGUIs = true; } catch { }   // zone changes turn them back on
            if (Time.realtimeSinceStartup >= nextUiSweep)   // the game's other UI (side buttons etc.) is on canvases
            {
                nextUiSweep = Time.realtimeSinceStartup + 2f;
                foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>()) if (cv.enabled) cv.enabled = false;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            // Input in the game window itself (it sits inside the editor): WASD, Q/E, Shift, right-drag to
            // look, the wheel sets the speed. Plus what the editor's picture view sends (/editor/cam).
            // Like Unity's scene view: hold the right mouse button to look and fly with WASD / Q E (the wheel sets
            // the speed meanwhile); the arrow keys fly any time; middle-drag pans; the wheel zooms; Alt+left-drag
            // orbits the selection; F frames it. (Without the right button, W E R are the build tools.)
            // A quick right-click is for the build menu (WorldLoader): only start looking once the button has been
            // held a moment or the mouse moved, and remember where it was pressed.
            if (Input.GetMouseButtonDown(1)) { RmbDownPos = Input.mousePosition; rmbDownTime = Time.realtimeSinceStartup; rmbLooking = false; lookedThisPress = false; }
            if (Input.GetMouseButton(1) && !rmbLooking &&
                (Time.realtimeSinceStartup - rmbDownTime > 0.2f || ((Vector2)Input.mousePosition - RmbDownPos).magnitude > 4f)) rmbLooking = lookedThisPress = true;
            bool rmb = Input.GetMouseButton(1) && rmbLooking, mmb = Input.GetMouseButton(2);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            var want = Vector3.zero;
            if (Input.GetKey(KeyCode.UpArrow)) want.z += 1;
            if (Input.GetKey(KeyCode.DownArrow)) want.z -= 1;
            if (Input.GetKey(KeyCode.RightArrow)) want.x += 1;
            if (Input.GetKey(KeyCode.LeftArrow)) want.x -= 1;
            if (Input.GetKey(KeyCode.PageUp) && !HasPivot) want.y += 1;
            if (Input.GetKey(KeyCode.PageDown) && !HasPivot) want.y -= 1;
            if (rmb)
            {
                if (Input.GetKey(KeyCode.W)) want.z += 1;
                if (Input.GetKey(KeyCode.S)) want.z -= 1;
                if (Input.GetKey(KeyCode.D)) want.x += 1;
                if (Input.GetKey(KeyCode.A)) want.x -= 1;
                if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Space)) want.y += 1;
                if (Input.GetKey(KeyCode.Q)) want.y -= 1;
            }
            bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Time.realtimeSinceStartup < moveUntil) { want += move; boost |= fast; }
            // Look sent from outside (the editor's picture view, or the editor reading the mouse when the game
            // can't): used unless the game reads the right mouse button itself (then it'd count twice).
            if (!rmb) { yaw = (yaw + pendingYaw * lookScale) % 360f; pitch = Mathf.Clamp(pitch + pendingPitch * lookScale, -89f, 89f); }
            pendingYaw = pendingPitch = 0;
            float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
            bool orbiting = alt && Input.GetMouseButton(0) && !MouseBusy;
            if (rmb || orbiting)
            {
                if (Cursor.lockState != CursorLockMode.Locked) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
                if (orbiting)
                {
                    var pivot = HasPivot ? Pivot : cam.position + cam.forward * 10f;
                    float dist = Vector3.Distance(cam.position, pivot);
                    yaw = (yaw + mx * 1.6f * lookScale) % 360f;
                    pitch = Mathf.Clamp(pitch - my * 1.6f * lookScale, -89f, 89f);
                    cam.position = pivot - Quaternion.Euler(pitch, yaw, 0) * Vector3.forward * dist;
                }
                else
                {
                    yaw = (yaw + mx * 0.8f * lookScale) % 360f;
                    pitch = Mathf.Clamp(pitch - my * 0.8f * lookScale, -89f, 89f);
                }
            }
            else if (Cursor.lockState == CursorLockMode.Locked) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (mmb)   // pan
            {
                float k = Mathf.Max(0.04f, speed * 0.012f);
                cam.position -= (cam.right * mx + cam.up * my) * k * 3f;
            }
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.01f)
            {
                if (rmb)
                {
                    speed = Mathf.Clamp(speed * (wheel > 0 ? 1.25f : 0.8f), 1f, 150f);
                    Messages.Show("Flying speed: " + speed.ToString("0") + " m/s", 1.2f);
                }
                else if (!MouseBusy)   // zoom: move toward what's under the view's middle (or the selection)
                {
                    float d = HasPivot ? Vector3.Distance(cam.position, Pivot) : 15f;
                    cam.position += cam.forward * Mathf.Sign(wheel) * Mathf.Clamp(d * 0.15f, 0.5f, 25f);
                }
            }
            if (((Input.GetKeyDown(KeyCode.F) && !rmb) || FrameRequest) && HasPivot)   // frame the selection
            {
                float d = Mathf.Max(3f, PivotSize * 1.7f + 2f);
                cam.position = Pivot - Quaternion.Euler(pitch, yaw, 0) * Vector3.forward * d;
            }
            FrameRequest = false;
            var flat = Quaternion.Euler(0, yaw, 0);
            var target = (flat * new Vector3(want.x, 0, want.z) + Vector3.up * want.y);
            if (target.sqrMagnitude > 1f) target.Normalize();
            target *= speed * (boost ? 3.5f : 1f);
            velocity = Vector3.Lerp(velocity, target, 1f - Mathf.Exp(-10f * dt));   // ease in and out
            cam.position += velocity * dt;
            cam.rotation = Quaternion.Euler(pitch, yaw, 0);
        }

        static void FreeCursor() { if (Cursor.lockState != CursorLockMode.None) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } rmbLooking = false; }
        /// <summary>The right mouse button is being held to look around (not a quick right-click).</summary>
        public static bool RmbLooking { get { return rmbLooking && Input.GetMouseButton(1); } }
        /// <summary>This (or the last) right-button press turned the camera: on release it isn't a right-click.</summary>
        public static bool RmbLooked { get { return lookedThisPress; } }

        internal static void ZoneChanging() { jumped = true; FreeCursor(); }
        internal static void ZoneLoaded() { setUpFor = null; cam = null; velocity = Vector3.zero; Selected = null; }

        /// <summary>A small readout in the corner: speed, where the camera is.</summary>
        internal static void OnGUI()
        {
            if (!On || cam == null || !Game.InWorld) return;
            var p = cam.position;
            var t = "  " + Game.Zone.Replace("Zone_", "") + "   x " + p.x.ToString("0") + "  y " + p.y.ToString("0") + "  z " + p.z.ToString("0") +
                    "   speed " + speed.ToString("0") + " m/s" + (string.IsNullOrEmpty(ToolText) ? "" : "   ·   " + ToolText) +
                    (string.IsNullOrEmpty(Selected) ? "" : "   ·   selected: " + Selected) +
                    "   ·   hold right mouse + WASD fly · middle-drag pan · wheel zoom · Alt+drag orbit · F frame  ";
            var st = readoutStyle ?? (readoutStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleLeft, wordWrap = true });
            var c = new GUIContent(t);
            float w = Mathf.Min(st.CalcSize(c).x + 8, Screen.width - 16), h = st.CalcHeight(c, w);
            GUI.Box(new Rect(8, Screen.height - h - 10, w, h + 2), c, st);
        }

        /// <summary>From the editor: fly (mx right, my up, mz forward: -1 to 1) and look (degrees).</summary>
        internal static string Cam(LiveRequest r)
        {
            if (!On || cam == null) return "{\"error\":\"The camera is the game's own (not in editor mode).\"}";
            move = new Vector3(LiveLink.Num(r.Body, "mx"), LiveLink.Num(r.Body, "my"), LiveLink.Num(r.Body, "mz"));
            fast = LiveLink.Num(r.Body, "fast") > 0;
            moveUntil = Time.realtimeSinceStartup + 0.35f;
            pendingYaw += LiveLink.Num(r.Body, "yaw");
            pendingPitch += LiveLink.Num(r.Body, "pitch");
            return "{\"ok\":true}";
        }

        /// <summary>The 3D tab's Look speed (0.1 to 3; 1 = normal).</summary>
        internal static string Look(LiveRequest r)
        {
            float v; if (float.TryParse(r.Get("scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) lookScale = Mathf.Clamp(v, 0.1f, 3f);
            return "{\"scale\":" + lookScale.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>Look at a spot from a little way off (the editor's "Take me there").</summary>
        internal static void LookAt(Vector3 spot)
        {
            if (cam == null) return;
            // Behind and above the spot; if a wall or tree is in the way, the clearest of 8 directions
            // (starting with the current one), and never inside what's in the way.
            var eye = spot + Vector3.up * 1.5f;
            Vector3 best = spot + Quaternion.Euler(0, yaw, 0) * Vector3.back * 9f + Vector3.up * 6f; float bestFree = -1f;
            for (int i = 0; i < 8; i++)
            {
                var want = spot + Quaternion.Euler(0, yaw + i * 45f, 0) * Vector3.back * 9f + Vector3.up * 6f;
                var dir = want - eye; float len = dir.magnitude; RaycastHit hit;
                float free = Physics.Raycast(eye, dir / len, out hit, len, ~0, QueryTriggerInteraction.Ignore) ? Mathf.Max(0f, hit.distance - 0.6f) : len;
                if (free > bestFree + 0.5f) { bestFree = free; best = eye + dir / len * free; }
                if (free >= len) break;
            }
            cam.position = best;
            var d = spot - cam.position;
            yaw = Quaternion.LookRotation(d).eulerAngles.y;
            pitch = Mathf.Clamp(Mathf.Asin(-d.normalized.y) * Mathf.Rad2Deg, -89f, 89f);
        }

        /// <summary>The camera (where you "are" in editor mode), or null when the camera is the game's.</summary>
        public static Transform Camera_ { get { return On && cam != null ? cam : null; } }
    }
}
