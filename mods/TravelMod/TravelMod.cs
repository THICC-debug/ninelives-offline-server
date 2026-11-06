using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using NineLivesModLoader;
using UnityEngine;

namespace TravelMod
{
    // 1) Remembers where you were and puts you back there when you log in.
    //    (The original game always started you at the zone's spawn point.)
    // 2) F9 teleports to the next spawn point / binding stone in the zone, Shift+F9 to the previous one.
    // 3) F10 teleports to named places (add more in Places below).
    // Keys can be changed in Mods\config\travelmod.cfg.
    [ModInfo("travelmod", "TravelMod", "1.0.0", Author = "Nine Lives offline project",
        Description = "Remembers your position across logins. F9 teleports between spawn points, F10 to named places.")]
    public class TravelMod : Mod
    {
        internal static string SavePath;
        KeyCode spawnKey, placeKey;
        int index = -1, placeIndex = -1;

        // Named places, found in the level data (scene name -> name, position).
        static readonly Dictionary<string, KeyValuePair<string, Vector3>[]> Places = new Dictionary<string, KeyValuePair<string, Vector3>[]>
        {
            { "Zone_CrimsonIsles", new[] {
                new KeyValuePair<string, Vector3>("Crimson Crest (binding stone)", new Vector3(1282.5f, 145f, 1121.1f)),
                new KeyValuePair<string, Vector3>("Crimson Crest alchemy shop (Towner)", new Vector3(1268f, 147.5f, 1067f)),
                new KeyValuePair<string, Vector3>("Crimson Crest Herb Vendor", new Vector3(1285f, 147.5f, 1076f)),
            } },
        };

        public override void Load()
        {
            SavePath = Path.Combine(Path.Combine(ModLoader.ModsDir, "TravelMod"), "positions.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            spawnKey = Config.Get("SpawnPointKey", KeyCode.F9, "Teleport to the next spawn point (hold Shift for the previous one)");
            placeKey = Config.Get("NamedPlaceKey", KeyCode.F10, "Teleport to the next named place");
            new Harmony("ninelives.travelmod").PatchAll(typeof(TravelMod).Assembly);
            Log("ready: positions are remembered, " + spawnKey + " spawn points, " + placeKey + " named places");
        }

        public override void OnUpdate()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(spawnKey)) Try(() => ToSpawnPoint(shift ? -1 : 1));
            else if (Input.GetKeyDown(placeKey)) Try(() => ToPlace(shift ? -1 : 1));
        }

        void Try(Action a) { try { a(); } catch (Exception e) { Log("teleport failed: " + e); } }

        void ToSpawnPoint(int step)
        {
            if (!Game.InWorld) { Messages.Show("Not in the world yet"); return; }
            var points = new List<Graveyard>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(Graveyard))) points.Add((Graveyard)o);
            if (points.Count == 0) { Messages.Show("No spawn points in this zone"); return; }
            points.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            index = ((index + step) % points.Count + points.Count) % points.Count;
            var pos = points[index].getRespawnPosition();
            Game.Teleport(pos);
            string label = NameFor(pos) ?? "spawn point";
            Messages.Show("Teleported to " + label + "  (" + (index + 1) + "/" + points.Count + ")");
            Log("teleported to " + label + " at " + pos);
        }

        void ToPlace(int step)
        {
            KeyValuePair<string, Vector3>[] list;
            if (!Game.InWorld) { Messages.Show("Not in the world yet"); return; }
            if (!Places.TryGetValue(Game.Zone, out list)) { Messages.Show("No named places in this zone yet"); return; }
            placeIndex = ((placeIndex + step) % list.Length + list.Length) % list.Length;
            Game.Teleport(list[placeIndex].Value);
            Messages.Show("Teleported to " + list[placeIndex].Key + "  (" + (placeIndex + 1) + "/" + list.Length + ")");
            Log("teleported to " + list[placeIndex].Key);
        }

        static string NameFor(Vector3 pos)
        {
            KeyValuePair<string, Vector3>[] list;
            if (Places.TryGetValue(Game.Zone, out list))
                foreach (var p in list)
                    if ((p.Value - pos).sqrMagnitude < 15f * 15f) return p.Key;
            return null;
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
            d[key] = Game.Zone + "|" + p.x.ToString("R") + "|" + p.y.ToString("R") + "|" + p.z.ToString("R");
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
            if (f.Length != 4 || f[0] != Game.Zone) return false;  // saved in another zone
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
}
