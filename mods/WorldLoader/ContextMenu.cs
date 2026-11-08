using System;
using System.Collections.Generic;
using NineLivesModLoader;
using UnityEngine;

namespace WorldLoader
{
    /// <summary>
    /// Right-click menu in the editor's 3D view (a quick right-click; holding the button still flies the camera):
    /// on an NPC: Add dialogue, Make a quest, Properties, Move ▸, Frame, Delete;
    /// on a placed thing: Properties, Move ▸, Copy, Frame, Delete.
    /// Move ▸ opens the moving tools: Move, Rotate, Scale, Pick up and carry, Drop to the ground.
    /// Editor actions (dialogue, quest, properties) go to the editor as a "menu" event.
    /// </summary>
    public partial class WorldLoader
    {
        bool menuOpen, moveOpen;
        Vector2 menuAt;               // top-left, in GUI coordinates (y down)
        Rect menuRect, subRect;
        const float MenuW = 210f, RowH = 26f;

        struct MenuItem { public string Label, Action; public MenuItem(string l, string a) { Label = l; Action = a; } }

        List<MenuItem> MainItems()
        {
            var l = new List<MenuItem>();
            if (selKind == "npc") { l.Add(new MenuItem("Add dialogue…", "dialogue")); l.Add(new MenuItem("Make a quest…", "quest")); }
            l.Add(new MenuItem("Properties", "properties"));
            l.Add(new MenuItem("Move  ▸", "submenu"));
            l.Add(new MenuItem("Copy   (Ctrl+D)", "copy"));
            l.Add(new MenuItem("Frame   (F)", "frame"));
            l.Add(new MenuItem("Delete   (Del)", "delete"));
            return l;
        }
        List<MenuItem> MoveItems()
        {
            var l = new List<MenuItem> { new MenuItem("Move   (W)", "move"), new MenuItem("Rotate   (E)", "rotate") };
            if (selKind == "world") l.Add(new MenuItem("Scale   (R)", "scale"));
            if (selKind != "enemy") l.Add(new MenuItem("Pick up and carry", "carry"));
            l.Add(new MenuItem("Drop to the ground", "ground"));
            return l;
        }

        /// <summary>Called from ToolsUpdate. Returns true when the menu took this frame's mouse input.</summary>
        bool MenuUpdate(Camera cam)
        {
            // a quick right-click (no drag): open the menu on what's under the mouse
            if (Input.GetMouseButtonUp(1) && active == Handle.None && !EditorMode.RmbLooked && Time.realtimeSinceStartup - rmbDownAt < 0.25f && ((Vector2)Input.mousePosition - rmbDownPos).magnitude < 5f)
            {
                var ray = cam.ScreenPointToRay(EditorMode.RmbDownPos);
                string kind, id; Transform root;
                if (Hit(ray, out root, out kind, out id))
                {
                    if (root != selT) Select(root, kind, id);
                    menuOpen = true; moveOpen = false;
                    menuAt = new Vector2(EditorMode.RmbDownPos.x, Screen.height - EditorMode.RmbDownPos.y);
                    // keep it on screen
                    float h = MainItems().Count * RowH + 34f;
                    menuAt.x = Mathf.Min(menuAt.x, Screen.width - MenuW * 2 - 8); menuAt.y = Mathf.Min(menuAt.y, Screen.height - h - 8);
                }
                else menuOpen = false;
                return true;
            }
            if (!menuOpen) return false;
            if (selT == null || Input.GetKeyDown(KeyCode.Escape)) { menuOpen = false; return true; }
            EditorMode.MouseBusy = true;
            var m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool inside = menuRect.Contains(m) || (moveOpen && subRect.Contains(m));
            if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(2)) && !inside) { menuOpen = false; return Input.GetMouseButtonDown(0); }
            return inside || Input.GetMouseButton(0);
        }

        void MenuGUI()
        {
            if (!menuOpen || selT == null) return;
            var items = MainItems();
            if (menuTitleStyle == null)
            {
                menuTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                menuTitleStyle.normal.textColor = new Color(1f, 0.82f, 0.4f);
                menuButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 8, 3, 3) };
            }
            var title = menuTitleStyle; var btn = menuButtonStyle;
            menuRect = new Rect(menuAt.x, menuAt.y, MenuW, items.Count * RowH + 34f);
            GUI.depth = -2000;
            GUI.Box(menuRect, GUIContent.none); GUI.Box(menuRect, GUIContent.none);
            GUI.Label(new Rect(menuRect.x + 10, menuRect.y + 4, MenuW - 20, 22), (Person(selKind) ? "NPC: " : selKind == "enemy" ? "Enemy: " : "") + selName, title);
            var mouse = Event.current.mousePosition;
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rect(menuRect.x + 4, menuRect.y + 28 + i * RowH, MenuW - 8, RowH - 2);
                if (items[i].Action == "submenu" && r.Contains(mouse)) moveOpen = true;
                if (GUI.Button(r, items[i].Label, btn)) { if (items[i].Action == "submenu") moveOpen = !moveOpen; else Do(items[i].Action); }
                if (items[i].Action == "submenu" && moveOpen)
                {
                    var sub = MoveItems();
                    subRect = new Rect(menuRect.xMax - 2, r.y - 4, MenuW, sub.Count * RowH + 8f);
                    GUI.Box(subRect, GUIContent.none); GUI.Box(subRect, GUIContent.none);
                    for (int k = 0; k < sub.Count; k++)
                        if (GUI.Button(new Rect(subRect.x + 4, subRect.y + 4 + k * RowH, MenuW - 8, RowH - 2), sub[k].Label, btn)) Do(sub[k].Action);
                }
            }
        }

        void Do(string action)
        {
            menuOpen = false; moveOpen = false;
            if (selT == null) return;
            object id = Person(selKind) ? (object)NpcId(selT) : selId;
            switch (action)
            {
                case "dialogue": case "quest": case "properties":
                    Push(new Dictionary<string, object> { { "type", "menu" }, { "action", action }, { "kind", selKind }, { "id", id } });
                    break;
                case "move": SetTool(Tool.Move); break;
                case "rotate": SetTool(Tool.Rotate); break;
                case "scale": SetTool(Tool.Scale); break;
                case "carry": StartCarry(selT, selKind, selKind == "world" ? selId : selT.name, false); break;
                case "ground":
                {
                    RaycastHit below;
                    if (GroundBelow(selT.position + Vector3.up * 0.5f, out below)) { selT.position = new Vector3(selT.position.x, below.point.y, selT.position.z); Report("move"); }
                    break;
                }
                case "copy": CopyAndCarry(); break;
                case "frame": EditorMode.FrameRequest = true; break;
                case "delete":
                    Push(new Dictionary<string, object> { { "type", "delete" }, { "kind", selKind }, { "id", id } });
                    Messages.Show("Deleted " + selName + (Person(selKind) ? "  (Discard brings it back until you save)" : "  (Ctrl+Z brings it back)"), 2.5f);
                    if (selKind == "world") Remove(selId); else selT.gameObject.SetActive(false);
                    Deselect();
                    break;
            }
        }
    }
}
