using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NineLivesModLoader;
using UnityEngine;

namespace ScriptLoader
{
    /// <summary>
    /// Dialogues: conversations with choices, from the "dialogs" list in script packs.
    /// { id, name, npc (opens when you talk to this NPC), start, nodes: [ { id, speaker, text, do: [actions],
    ///   choices: [ { text, if: [conditions], do: [actions], goto: node id (none = end) } ] } ] }
    /// Shown in a window drawn like the game's own quest window; keys 1-9 pick a choice, Esc closes.
    /// </summary>
    public partial class ScriptLoader
    {
        class Dialog { public string Pack, Id, Name; public int Npc; public JObject Data; }
        readonly Dictionary<string, Dialog> dialogs = new Dictionary<string, Dialog>();   // "pack/id" -> dialog

        Dialog open; JObject node; List<JObject> shown = new List<JObject>();
        Vector3 openedAt; int openedNpc;
        GUIStyle boxStyle, nameStyle, textStyle, choiceStyle;

        void LoadDialogs(string pack, JObject p)
        {
            var list = p["dialogs"] as JArray;
            if (list == null) return;
            foreach (var d in list.OfType<JObject>())
            {
                if (d.Value<bool?>("enabled") == false) continue;
                var id = d.Value<string>("id");
                if (string.IsNullOrEmpty(id)) { problems.Add(pack + ": a dialogue has no id"); continue; }
                dialogs[pack + "/" + id] = new Dialog { Pack = pack, Id = id, Name = d.Value<string>("name"), Npc = d.Value<int?>("npc") ?? 0, Data = d };
            }
        }

        Dialog FindDialog(string id, string preferPack)
        {
            Dialog d;
            if (preferPack != null && dialogs.TryGetValue(preferPack + "/" + id, out d)) return d;
            if (dialogs.TryGetValue(id, out d)) return d;
            return dialogs.Values.FirstOrDefault(x => x.Id == id);
        }

        /// <summary>Talking to an NPC that has a dialogue opens it.</summary>
        internal bool OpenForNpc(int cid)
        {
            var d = dialogs.Values.FirstOrDefault(x => x.Npc == cid);
            if (d == null) return false;
            Open(d, cid);
            return true;
        }

        internal string OpenDialog(string id, string preferPack)
        {
            var d = FindDialog(id, preferPack);
            if (d == null) return "no dialogue \"" + id + "\"";
            Open(d, 0);
            return null;
        }

        /// <summary>Open a dialogue straight from its JSON (the editor's "Play in the game").</summary>
        internal void OpenRaw(JObject data, string pack)
        {
            Open(new Dialog { Pack = pack, Id = data.Value<string>("id") ?? "test", Name = data.Value<string>("name"), Data = data }, 0);
        }

        void Open(Dialog d, int npc)
        {
            open = d; openedNpc = npc; openedAt = Game.PlayerPosition;
            var nodes = d.Data["nodes"] as JArray;
            var start = d.Data.Value<string>("start");
            var first = nodes == null ? null : (nodes.OfType<JObject>().FirstOrDefault(n => n.Value<string>("id") == start) ?? nodes.OfType<JObject>().FirstOrDefault());
            if (first == null) { open = null; Log(d.Pack + "/" + d.Id + ": the dialogue has no lines"); return; }
            Show(first);
        }

        void Show(JObject n)
        {
            node = n;
            var steps = n["do"] as JArray;
            if (steps != null && steps.Count > 0) RunSteps(steps, open.Pack);
            shown = (n["choices"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(c => (c["if"] as JArray ?? new JArray()).OfType<JObject>().All(Check)).ToList();
        }

        void Choose(int i)
        {
            if (open == null) return;
            if (shown.Count == 0) { Close(); return; }      // just "Close"
            if (i < 0 || i >= shown.Count) return;
            var c = shown[i];
            var steps = c["do"] as JArray;
            if (steps != null && steps.Count > 0) RunSteps(steps, open.Pack);
            var next = c.Value<string>("goto");
            var nodes = open.Data["nodes"] as JArray;
            var n = string.IsNullOrEmpty(next) || nodes == null ? null : nodes.OfType<JObject>().FirstOrDefault(x => x.Value<string>("id") == next);
            if (n == null) Close(); else Show(n);
        }

        void Close() { open = null; node = null; shown.Clear(); if (mouseHeld) { try { ReleaseMouse(); } catch { } } }

        /// <summary>Keys, and closing when the player walks away. Called every frame.</summary>
        void DialogUpdate()
        {
            if (open == null) return;
            if (!Game.InWorld || Vector3.Distance(Game.PlayerPosition, openedAt) > 8f) { Close(); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            for (int k = 0; k < 9; k++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + k) || Input.GetKeyDown(KeyCode.Keypad1 + k)) { Choose(shown.Count == 0 ? 0 : k); return; }
            if (shown.Count == 0 && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))) Close();
        }

        internal string drawError;
        string speakerName; Dialog speakerFor; int speakerNpc;

        public override void OnGUI()
        {
            if (open == null || node == null) return;
            try { DrawDialog(); }
            catch (Exception e)
            {
                var msg = e.GetType().Name + ": " + e.Message;
                if (msg != drawError) { drawError = msg; Log("dialogue box failed to draw: " + e); }
            }
        }

        // ------------------------------------------------ the game's own window look (like its quest and talk window)
        const int MouseKey = 0x4E4C5301;      // our key in the game's "mouse is over a window" bookkeeping
        int windowId = -1; bool mouseHeld; Vector2 scroll; JObject scrollFor;
        Texture talkIcon;

        void DrawDialog()
        {
            GUI.depth = -1000;   // on top of the game's own windows
            var root = Universe.GetGUIRootCtrl();
            var qp = GUI_QuestPanel.getGUIInstance();
            if (root == null || root.CommonSkin == null || qp == null || qp.transitionButtonStyle == null) { DrawPlain(); return; }
            if (root.forceDisableGUIs) return;   // the game's UI is hidden (F11)
            if (windowId < 0) windowId = root.GetNextWindowID();

            var oldMatrix = GUI.matrix; var oldSkin = GUI.skin;
            root.setCommonGUIScaling();
            // Exactly where the game puts its talk/quest window (beside the stats panel's spot);
            // one window further right when the NPC's own quest window is open too.
            var sp = qp.parentCtrl;
            var r = sp != null ? CommonHelper.rateOfScreenToPoint(sp.windowRect) : new Rect(0f, 0f, 270f, 313f);
            r.x = sp != null && sp.Opened ? r.x + sp.windowRect.width : 0f;
            if (qp.isShowingQuest()) r.x = qp.detailPanelRect.xMax;
            bool over = GUIRoot.AreaTest(r);
            if (!Universe.isLockUI)
            {
                Universe.GUIDragUsing(MouseKey, over);
                if (over) mouseHeld = Universe.GUIOnMouseEnter(MouseKey, mouseHeld);
                else if (mouseHeld) ReleaseMouse();
            }
            GUI.Window(windowId, r, id => DrawGameWindow(root.CommonSkin, qp, r), string.Empty, root.CommonSkin.window);
            GUI.BringWindowToFront(windowId);
            GUI.matrix = oldMatrix; GUI.skin = oldSkin;
        }

        void ReleaseMouse()
        {
            mouseHeld = Universe.GUIOnMouseOut(MouseKey);
            Universe.GUIForceClearDragUsing(MouseKey);
        }

        void DrawGameWindow(GUISkin skin, GUI_QuestPanel qp, Rect r)
        {
            if (open == null || node == null) return;
            // close button, top right
            GUILayout.BeginArea(new Rect(0f, 0f, r.width, r.height));
            GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
            bool closed = GUILayout.Button(string.Empty, skin.GetStyle("buttonClose"));
            GUILayout.EndHorizontal(); GUILayout.EndArea();
            if (closed) { Close(); ReleaseMouse(); return; }

            int who = openedNpc > 0 ? openedNpc : open.Npc;
            if (speakerFor != open || speakerNpc != who) { speakerFor = open; speakerNpc = who; speakerName = who > 0 ? NpcName(who) : ""; }
            var speaker = Fill(node.Value<string>("speaker") ?? speakerName ?? "", null);
            var text = Fill(node.Value<string>("text") ?? "", null);
            if (scrollFor != node) { scrollFor = node; scroll = Vector2.zero; }

            var area = qp.areaRect;   // the game's inner area (6, 8, 240, 300)
            GUILayout.BeginArea(new Rect(area.x, area.y, area.width, r.height - area.y));
            if (talkIcon == null) talkIcon = GUI_MarkIconLoader.load(QuestController.GetQuestIconID(QuestType.Message));
            GUILayout.Label(new GUIContent(speaker.Length > 0 ? speaker : (open.Name ?? ""), talkIcon), skin.GetStyle("tabPanelSelectionList"));
            GUI.skin = skin;
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Width(area.width));
            GUILayout.Label(text, qp.detailDescriptionStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            var bg = GUI.backgroundColor;
            GUI.backgroundColor = qp.closeButtonBGColor;
            int pick = -1;
            if (shown.Count == 0) { if (GUILayout.Button(qp.closeButtonText, qp.transitionButtonStyle)) pick = 0; }
            else for (int i = 0; i < shown.Count; i++)
                if (GUILayout.Button(Fill(shown[i].Value<string>("text") ?? "…", null), qp.transitionButtonStyle)) pick = i;
            GUI.backgroundColor = bg;
            GUILayout.Space(qp.bottomSpace);
            GUILayout.EndArea();
            if (pick >= 0) { Choose(pick); if (open == null) ReleaseMouse(); }
        }

        // ------------------------------------------------ fallback: a plain box at the bottom (if the game's UI isn't there)
        void DrawPlain()
        {
            if (boxStyle == null)
            {
                boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(18, 18, 14, 14) };
                nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
                nameStyle.normal.textColor = new Color(1f, 0.78f, 0.45f);
                textStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
                textStyle.normal.textColor = Color.white;
                choiceStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 12, 7, 7), wordWrap = true };
            }
            float w = Mathf.Min(760f, Screen.width - 40f);
            int who = openedNpc > 0 ? openedNpc : open.Npc;
            if (speakerFor != open || speakerNpc != who) { speakerFor = open; speakerNpc = who; speakerName = who > 0 ? NpcName(who) : ""; }
            var speaker = Fill(node.Value<string>("speaker") ?? speakerName ?? "", null);
            var text = Fill(node.Value<string>("text") ?? "", null);
            var labels = shown.Count > 0 ? shown.Select((c, i) => (i + 1) + ".  " + Fill(c.Value<string>("text") ?? "…", null)).ToList() : new List<string> { "Close" };
            float textH = textStyle.CalcHeight(new GUIContent(text), w - 36f);
            float choicesH = labels.Sum(l => choiceStyle.CalcHeight(new GUIContent(l), w - 36f) + 6f);
            float h = 28f + (speaker.Length > 0 ? 26f : 0f) + textH + 14f + choicesH;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height - h - 110f, w, h);
            GUI.Box(rect, GUIContent.none, boxStyle);
            GUI.Box(rect, GUIContent.none, boxStyle);   // twice: darker
            GUILayout.BeginArea(new Rect(rect.x + 18f, rect.y + 14f, w - 36f, h - 28f));
            if (speaker.Length > 0) GUILayout.Label(speaker, nameStyle);
            GUILayout.Label(text, textStyle);
            GUILayout.Space(8f);
            for (int i = 0; i < labels.Count; i++)
                if (GUILayout.Button(labels[i], choiceStyle)) Choose(i);
            GUILayout.EndArea();
        }

        static string NpcName(int cid)
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s != null && s.getCID() == cid && s.gameObject.scene.IsValid()) return s.dispName;
            }
            return "";
        }
    }
}
