using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using NineLivesModLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TravelMod
{
    // 1) Remembers where you were and puts you back there when you log in.
    //    (The original game always started you at the zone's spawn point.)
    // 2) F9 teleports to the next spawn point / binding stone in the zone, Shift+F9 to the previous one.
    // 3) F10 teleports to named places (add more in Places below).
    public class TravelMod : IMod
    {
        internal static string SavePath;

        public void Load()
        {
            SavePath = Path.Combine(Path.Combine(ModLoader.ModsDir, "TravelMod"), "positions.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            new Harmony("ninelives.travelmod").PatchAll(typeof(TravelMod).Assembly);
            ModLoader.Log("TravelMod ready: positions are remembered, F9 spawn points, F10 named places");
        }
    }

    // ------------------------------------------------------------ saved positions
    // positions.txt: one line per character: csKey|scene|x|y|z
    static class Positions
    {
        static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(TravelMod.SavePath))
                    foreach (var line in File.ReadAllLines(TravelMod.SavePath))
                    {
                        int i = line.IndexOf('|');
                        if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch (Exception e) { ModLoader.Log("TravelMod: couldn't read positions: " + e.Message); }
            return d;
        }

        public static void Save(Vector3 p)
        {
            string key = Universe.characterSessionKey;
            if (string.IsNullOrEmpty(key)) return;
            var d = Read();
            d[key] = SceneManager.GetActiveScene().name + "|" + p.x.ToString("R") + "|" + p.y.ToString("R") + "|" + p.z.ToString("R");
            var lines = new List<string>();
            foreach (var kv in d) lines.Add(kv.Key + "|" + kv.Value);
            try { File.WriteAllLines(TravelMod.SavePath, lines.ToArray()); }
            catch (Exception e) { ModLoader.Log("TravelMod: couldn't save position: " + e.Message); }
        }

        public static bool TryGet(out Vector3 p)
        {
            p = Vector3.zero;
            string key = Universe.characterSessionKey;
            string v;
            if (string.IsNullOrEmpty(key) || !Read().TryGetValue(key, out v)) return false;
            var f = v.Split('|');
            if (f.Length != 4 || f[0] != SceneManager.GetActiveScene().name) return false;  // saved in another zone
            p = new Vector3(float.Parse(f[1]), float.Parse(f[2]), float.Parse(f[3]));
            return true;
        }
    }

    // The game saves every ~20 s (playtime) and on bigger saves: store the position each time.
    [HarmonyPatch(typeof(PlayerController), "saveCharacterPlaytimeAsync")]
    static class SaveOnPlaytime { static void Postfix(PlayerController __instance) => Positions.Save(__instance.transform.position); }

    [HarmonyPatch(typeof(PlayerController), "saveCharacterAsync")]
    static class SaveOnSave { static void Postfix(PlayerController __instance) => Positions.Save(__instance.transform.position); }

    // On entering the world the game asks the graveyard keeper for the start point, then
    // that point's respawn position. Swap in the saved position for that one call.
    [HarmonyPatch(typeof(WorldRoot_GraveyardKeeper), nameof(WorldRoot_GraveyardKeeper.getStartGraveyard))]
    static class MarkEntering { internal static bool entering; static void Postfix() => entering = true; }

    [HarmonyPatch(typeof(Graveyard), nameof(Graveyard.getRespawnPosition))]
    static class RestorePosition
    {
        static void Postfix(ref Vector3 __result)
        {
            if (!MarkEntering.entering) return;
            MarkEntering.entering = false;
            Vector3 saved;
            if (Positions.TryGet(out saved))
            {
                __result = saved;
                ModLoader.Log("TravelMod: restored last position " + saved);
            }
        }
    }

    // ------------------------------------------------------------ teleport keys
    [HarmonyPatch(typeof(Universe), nameof(Universe.initResolution))]
    static class CreateHost
    {
        static void Postfix()
        {
            if (TeleportKeys.Instance != null) return;
            var go = new GameObject("TravelMod");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TeleportKeys.Instance = go.AddComponent<TeleportKeys>();
        }
    }

    public class TeleportKeys : MonoBehaviour
    {
        internal static TeleportKeys Instance;
        int index = -1;
        string message;
        float messageUntil;

        // Named places, found in the level data (scene name -> name, position).
        static readonly Dictionary<string, KeyValuePair<string, Vector3>[]> Places = new Dictionary<string, KeyValuePair<string, Vector3>[]>
        {
            { "Zone_CrimsonIsles", new[] {
                new KeyValuePair<string, Vector3>("Crimson Crest (binding stone)", new Vector3(1282.5f, 145f, 1121.1f)),
            } },
        };
        int placeIndex = -1;

        static string NameFor(Vector3 pos)
        {
            KeyValuePair<string, Vector3>[] list;
            if (Places.TryGetValue(SceneManager.GetActiveScene().name, out list))
                foreach (var p in list)
                    if ((p.Value - pos).sqrMagnitude < 15f * 15f) return p.Key;
            return null;
        }

        void Update()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            try
            {
                if (Input.GetKeyDown(KeyCode.F9)) Teleport(shift ? -1 : 1);
                else if (Input.GetKeyDown(KeyCode.F10)) TeleportToPlace(shift ? -1 : 1);
            }
            catch (Exception e) { ModLoader.Log("TravelMod: teleport failed: " + e); }
        }

        void TeleportToPlace(int step)
        {
            KeyValuePair<string, Vector3>[] list;
            if (!Places.TryGetValue(SceneManager.GetActiveScene().name, out list)) { Show("No named places in this zone yet"); return; }
            placeIndex = ((placeIndex + step) % list.Length + list.Length) % list.Length;
            MovePlayer(list[placeIndex].Value);
            Show("Teleported to " + list[placeIndex].Key + "  (" + (placeIndex + 1) + "/" + list.Length + ")  -  F10 next");
            ModLoader.Log("TravelMod: teleported to " + list[placeIndex].Key);
        }

        static void MovePlayer(Vector3 pos)
        {
            var player = Universe.getCurrentPlayerController();
            if (player == null) return;
            // The player moves with a CharacterController, which fights direct position changes.
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.transform.position = pos;
            if (cc != null) cc.enabled = true;
        }

        void Teleport(int step)
        {
            var player = Universe.getCurrentPlayerController();
            if (player == null) { Show("Not in the world yet"); return; }

            var points = new List<Graveyard>();
            foreach (var o in FindObjectsOfType(typeof(Graveyard))) points.Add((Graveyard)o);
            if (points.Count == 0) { Show("No spawn points in this zone"); return; }
            points.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            index = ((index + step) % points.Count + points.Count) % points.Count;
            var target = points[index];
            Vector3 pos = target.getRespawnPosition();
            MovePlayer(pos);

            string label = NameFor(pos) ?? "spawn point";
            Show("Teleported to " + label + "  (" + (index + 1) + "/" + points.Count + ")  -  F9 next, Shift+F9 back, F10 places");
            ModLoader.Log("TravelMod: teleported to " + label + " at " + pos);
        }

        void Show(string text) { message = text; messageUntil = Time.realtimeSinceStartup + 5f; }

        void OnGUI()
        {
            if (message == null || Time.realtimeSinceStartup > messageUntil) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16 };
            var size = style.CalcSize(new GUIContent(message));
            GUI.Box(new Rect((Screen.width - size.x - 20) / 2f, 60, size.x + 20, size.y + 10), message, style);
        }
    }
}
