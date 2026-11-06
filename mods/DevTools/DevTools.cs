using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NineLivesModLoader;
using UnityEngine;

namespace DevTools
{
    // An in-game command box for testing (F7). Type "help" for the list.
    [ModInfo("devtools", "DevTools", "1.0.0", Author = "Nine Lives offline project",
        Description = "In-game command box (F7) for testing: give items, gold, exp, teleport, flags.")]
    public class DevTools : Mod
    {
        KeyCode openKey;
        bool open, focusNext;
        string input = "";
        readonly List<string> output = new List<string>();
        readonly List<string> history = new List<string>();
        int historyPos = -1;
        Dictionary<string, int> itemsByName;   // built the first time it's needed

        public override void Load()
        {
            openKey = Config.Get("OpenKey", KeyCode.F7, "Opens and closes the command box");
            Log("ready: press " + openKey + " in game");
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(openKey)) Toggle(!open);
        }

        void Toggle(bool on)
        {
            open = on;
            focusNext = on;
            // While typing, stop the character reacting to the keys.
            var p = Game.Player;
            if (p != null) p.enabled = !on;
            if (on && output.Count == 0) Print("Type a command and press Enter. \"help\" lists them. Esc closes.");
        }

        public override void OnGUI()
        {
            if (!open) return;
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape || e.keyCode == openKey) { Toggle(false); e.Use(); return; }
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Run(input); input = ""; e.Use(); }
                else if (e.keyCode == KeyCode.UpArrow && history.Count > 0) { historyPos = Math.Max(0, (historyPos < 0 ? history.Count : historyPos) - 1); input = history[historyPos]; e.Use(); }
                else if (e.keyCode == KeyCode.DownArrow && historyPos >= 0) { historyPos++; input = historyPos < history.Count ? history[historyPos] : ""; if (historyPos >= history.Count) historyPos = -1; e.Use(); }
            }

            float w = Mathf.Min(720, Screen.width - 40), lineH = 20;
            float h = lineH * output.Count + 40;
            var area = new Rect((Screen.width - w) / 2f, Screen.height - h - 40, w, h);
            GUI.Box(area, "");
            for (int i = 0; i < output.Count; i++)
                GUI.Label(new Rect(area.x + 8, area.y + 4 + i * lineH, w - 16, lineH), output[i]);
            GUI.SetNextControlName("devtools_input");
            input = GUI.TextField(new Rect(area.x + 6, area.yMax - 28, w - 12, 22), input);
            if (focusNext) { GUI.FocusControl("devtools_input"); focusNext = false; }
        }

        void Print(string line)
        {
            output.Add(line);
            while (output.Count > 10) output.RemoveAt(0);
        }

        // ------------------------------------------------------------ commands
        void Run(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            history.Add(line); historyPos = -1;
            Print("> " + line);
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToArray();
            try
            {
                switch (cmd)
                {
                    case "help": Help(); break;
                    case "give": Give(args); break;
                    case "find": Find(string.Join(" ", args)); break;
                    case "gold": NeedWorld(); Game.GiveGold(Long(args, 0)); Print("Gold is now " + Game.Stats.gold.read()); break;
                    case "exp": NeedWorld(); Game.GiveExp(Long(args, 0)); Print("Exp is now " + Game.Stats.exp.read()); break;
                    case "pos": NeedWorld(); var p = Game.PlayerPosition; Print(Game.Zone + "  " + F(p.x) + " " + F(p.y) + " " + F(p.z)); Log("pos " + Game.Zone + " " + p); break;
                    case "tp": NeedWorld(); Game.Teleport(new Vector3(Float(args, 0), Float(args, 1), Float(args, 2))); Print("Teleported"); break;
                    case "flag": FlagCmd(args); break;
                    case "clear": output.Clear(); break;
                    default: Print("Unknown command. Type help."); break;
                }
            }
            catch (Exception ex) { Print(ex is ArgumentException ? ex.Message : "Error: " + ex.Message); }
        }

        void Help()
        {
            Print("give <item name or id> [count]   e.g. give heartberry 4");
            Print("find <text>        list items whose name contains the text");
            Print("gold <amount>      exp <amount>       (negative takes away)");
            Print("pos                show your position      tp <x> <y> <z>  teleport");
            Print("flag <n> [on|off]  show or set a progress flag (mods: 9000-9999)");
            Print("clear              clear this box.  Up/Down: history.  Esc: close");
        }

        void Give(string[] args)
        {
            NeedWorld();
            if (args.Length == 0) throw new ArgumentException("give <item name or id> [count]");
            int count = 1, n;
            var nameParts = args.ToList();
            if (nameParts.Count > 1 && int.TryParse(nameParts[nameParts.Count - 1], out n)) { count = n; nameParts.RemoveAt(nameParts.Count - 1); }
            int id = ResolveItem(string.Join(" ", nameParts.ToArray()));
            if (id == 0) return;
            if (Game.GiveItem(id, count)) Print("Gave " + count + " x " + Game.ItemName(id) + " (" + id + ")");
            else Print("Couldn't give it: is your bag full?");
        }

        int ResolveItem(string text)
        {
            int id;
            if (int.TryParse(text, out id))
            {
                if (Game.ItemName(id) == null) { Print("No item with id " + id); return 0; }
                return id;
            }
            var items = Items();
            var t = text.ToLowerInvariant();
            if (items.TryGetValue(t, out id)) return id;
            var hits = items.Where(kv => kv.Key.Contains(t)).ToList();
            if (hits.Count == 1) return hits[0].Value;
            if (hits.Count == 0) Print("No item called \"" + text + "\". Try: find " + text);
            else Print("Which one? " + string.Join(", ", hits.Take(6).Select(h => Game.ItemName(h.Value) + " (" + h.Value + ")").ToArray()) + (hits.Count > 6 ? " …" : ""));
            return 0;
        }

        void Find(string text)
        {
            if (text.Length == 0) throw new ArgumentException("find <text>");
            var t = text.ToLowerInvariant();
            var hits = Items().Where(kv => kv.Key.Contains(t)).Take(8).ToList();
            if (hits.Count == 0) Print("Nothing found.");
            foreach (var h in hits) Print("  " + h.Value + "  " + Game.ItemName(h.Value));
        }

        Dictionary<string, int> Items()
        {
            if (itemsByName != null) return itemsByName;
            itemsByName = new Dictionary<string, int>();
            foreach (var id in Item.getItemXMLIDList())
            {
                var name = Game.ItemName(id);
                // First id wins for duplicate names (e.g. materials before their quest copies).
                if (!string.IsNullOrEmpty(name) && !itemsByName.ContainsKey(name.ToLowerInvariant())) itemsByName[name.ToLowerInvariant()] = id;
            }
            return itemsByName;
        }

        void FlagCmd(string[] args)
        {
            NeedWorld();
            int idx = (int)Long(args, 0);
            if (args.Length > 1)
            {
                bool on = args[1].ToLowerInvariant() != "off" && args[1] != "0" && args[1].ToLowerInvariant() != "false";
                Game.SetFlag(idx, on);
            }
            Print("Flag " + idx + " is " + (Game.GetFlag(idx) ? "on" : "off"));
        }

        static void NeedWorld() { if (!Game.InWorld) throw new ArgumentException("You need to be in the world for that."); }
        static long Long(string[] a, int i)
        {
            long v;
            if (a.Length <= i || !long.TryParse(a[i], out v)) throw new ArgumentException("Expected a number.");
            return v;
        }
        static float Float(string[] a, int i)
        {
            float v;
            if (a.Length <= i || !float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) throw new ArgumentException("tp <x> <y> <z>");
            return v;
        }
        static string F(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
    }
}
