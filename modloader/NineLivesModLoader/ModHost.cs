using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NineLivesModLoader
{
    /// <summary>
    /// A hidden GameObject that drives the events, draws messages, and shows the mod list (F11).
    /// It's created when the login screen comes up, the first moment Unity is safely running.
    /// </summary>
    public class ModHost : MonoBehaviour
    {
        static ModHost instance;
        static Harmony harmony;

        internal static void Install()
        {
            harmony = new Harmony("ninelives.modloader");
            harmony.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.initResolution)),
                postfix: new HarmonyMethod(typeof(ModHost), nameof(CreateAfterLogin)));
        }

        static void CreateAfterLogin()
        {
            if (instance != null) return;
            var go = new GameObject("NineLivesModHost");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ModHost>();
            ModLoader.Log("Game started: events and the F11 mod list are running");
            LiveLink.Start();
            MapCapture.Init();
            Events.FireGameStarted();
        }

        PlayerController lastPlayer;
        bool showList;
        Vector2 scroll;
        Rect window = new Rect(60, 60, 560, 420);

        void OnLevelWasLoaded(int level)
        {
            lastPlayer = null;
            Events.FireZoneLoaded(SceneManager.GetActiveScene().name);
        }

        void Update()
        {
            var p = Universe.getCurrentPlayerController();
            if (p != null && p != lastPlayer) { lastPlayer = p; Events.FirePlayerSpawned(); }
            if (Input.GetKeyDown(KeyCode.F11)) showList = !showList;
            LiveLink.Pump();
            Events.FireUpdate();
        }

        void OnGUI()
        {
            Events.FireGUI();
            DrawMessages();
            if (showList) window = GUI.Window(0x4E4C, window, DrawList, "Mods  (F11 to close)");
        }

        static void DrawMessages()
        {
            float now = Time.realtimeSinceStartup;
            Messages.Active.RemoveAll(m => m.Until < now);
            if (Messages.Active.Count == 0) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16, wordWrap = false };
            float y = 60;
            foreach (var m in Messages.Active)
            {
                var size = style.CalcSize(new GUIContent(m.Text));
                GUI.Box(new Rect((Screen.width - size.x - 20) / 2f, y, size.x + 20, size.y + 10), m.Text, style);
                y += size.y + 16;
            }
        }

        void DrawList(int id)
        {
            GUILayout.Label("Nine Lives Mod Loader " + ModLoader.Version + "  -  switching a mod on or off applies the next time you start the game.");
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var m in ModLoader.Mods)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(m.Enabled, "  " + m.Name + "  " + m.Version + (m.Author != null ? "  by " + m.Author : ""));
                if (on != m.Enabled) { m.Enabled = on; ModLoader.WriteEnabled(); }
                GUILayout.FlexibleSpace();
                GUILayout.Label(m.Loaded ? "running" : (m.Problem ?? "off"));
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(m.Description)) GUILayout.Label(m.Description);
                GUILayout.EndVertical();
            }
            if (ModLoader.Mods.Count == 0) GUILayout.Label("No mods installed. Put mod DLLs in the Mods folder.");
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }
    }
}
