using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NineLivesModLoader
{
    /// <summary>
    /// The editor's Test tools, for playing what you make again and again: give yourself items, gold and exp,
    /// see where a quest stands, take it, reset it (as if never done), and go to an NPC. Live-link requests:
    ///   GET  /test/state                      gold, exp, zone
    ///   POST /test/give   {item, count}       an item into the bag
    ///   POST /test/gold   {amount}            /test/exp {amount}
    ///   GET  /test/quest?id=N                 received / cleared / blocked, goal progress
    ///   POST /test/quest/take  {id}           take it now (its requirements still apply)
    ///   POST /test/quest/reset {id, flags}    drop it from the quest log and clear its "done" flags (and any listed)
    ///   POST /test/tonpc  {cid}               go to that NPC, if it's in this zone
    /// Changes are the game's own (the bag, gold, flags, quest log), so they're saved like anything you do in game.
    /// </summary>
    static class TestTools
    {
        internal static void Register()
        {
            LiveLink.Route("/test/state", State);
            LiveLink.Route("/test/give", Give);
            LiveLink.Route("/test/gold", r => Add(r, true));
            LiveLink.Route("/test/exp", r => Add(r, false));
            LiveLink.Route("/test/quest", QuestState);
            LiveLink.Route("/test/quest/take", Take);
            LiveLink.Route("/test/quest/reset", Reset);
            LiveLink.Route("/test/tonpc", ToNpc);
        }

        const string NotIn = "{\"error\":\"Not in the world yet.\"}";
        static string Err(string m) { return "{\"error\":" + LiveLink.Quote(m) + "}"; }
        // Whole numbers read exactly (LiveLink.Num gives a float, which can't hold a 9-digit quest id).
        static int Int(LiveRequest r, string key) { var q = r.Get(key); int v; if (q != null && int.TryParse(q, out v)) return v; return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Long(r.Body, key))); }
        static long Long(string json, string key)
        {
            if (json == null) return 0;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return 0;
            i = json.IndexOf(':', i) + 1;
            while (i > 0 && i < json.Length && json[i] == ' ') i++;
            int j = i;
            if (j < json.Length && (json[j] == '-' || json[j] == '+')) j++;
            while (j < json.Length && char.IsDigit(json[j])) j++;
            long v; long.TryParse(json.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return v;
        }

        static string State(LiveRequest r)
        {
            if (!Game.InWorld) return NotIn;
            var s = Game.Stats;
            return "{\"zone\":" + LiveLink.Quote(Game.Zone) + ",\"gold\":" + s.gold.read() + ",\"exp\":" + s.exp.read() + ",\"name\":" + LiveLink.Quote(s.dispName) + "}";
        }

        static string Give(LiveRequest r)
        {
            if (!Game.InWorld) return NotIn;
            int item = Int(r, "item"), count = Math.Max(1, Math.Min(999, Int(r, "count")));
            if (item <= 0) return Err("Which item?");
            var name = Game.ItemName(item);
            if (string.IsNullOrEmpty(name)) return Err("The game has no item " + item + ".");
            if (!Game.GiveItem(item, count)) return Err("It didn't fit: the bag may be full.");
            return "{\"ok\":true,\"item\":" + item + ",\"count\":" + count + ",\"name\":" + LiveLink.Quote(name) + "}";
        }

        static string Add(LiveRequest r, bool gold)
        {
            if (!Game.InWorld) return NotIn;
            var amount = Long(r.Body, "amount");
            if (amount == 0) return Err("How much?");
            var s = Game.Stats;
            // never below zero
            var have = gold ? s.gold.read() : s.exp.read();
            if (have + amount < 0) amount = -have;
            if (gold) Game.GiveGold(amount); else Game.GiveExp(amount);
            return "{\"ok\":true,\"" + (gold ? "gold" : "exp") + "\":" + (gold ? s.gold.read() : s.exp.read()) + "}";
        }

        static Quest Find(int id)
        {
            var ctrl = QuestController.GetQuestCtrl();
            Quest q;
            return ctrl != null && ctrl.quests != null && ctrl.quests.TryGetValue(id, out q) ? q : null;
        }

        static string Describe(Quest q)
        {
            var ctrl = QuestController.GetQuestCtrl();
            var st = QuestController.getQuestStatus(q);
            ReceivedQuest rq = null;
            if (ctrl != null && ctrl.receivedQuests != null) ctrl.receivedQuests.TryGetValue(q.id, out rq);
            var sb = new StringBuilder("{\"id\":" + q.id + ",\"title\":" + LiveLink.Quote(q.title));
            sb.Append(",\"received\":").Append(rq != null ? "true" : "false");
            sb.Append(",\"cleared\":").Append(QuestController.checkStatus(st, QuestStatus.Cleared) ? "true" : "false");
            sb.Append(",\"blocked\":").Append(QuestController.checkStatus(st, QuestStatus.Blocked) ? "true" : "false");
            sb.Append(",\"giver\":").Append(q.giverCID).Append(",\"ender\":").Append(q.enderCID);
            sb.Append(",\"clearFlag\":").Append(q.clearFlagIndex).Append(",\"clearWorkFlag\":").Append(q.clearWorkFlagIndex);
            if (rq != null && rq.reg != null) sb.Append(",\"progress\":[").Append(string.Join(",", rq.reg.Take(q.targets != null ? q.targets.Length : 0).Select(x => x.ToString()).ToArray())).Append("]");
            return sb.Append("}").ToString();
        }

        static string QuestState(LiveRequest r)
        {
            if (!Game.InWorld) return NotIn;
            var q = Find(Int(r, "id"));
            return q == null ? Err("The game has no quest " + Int(r, "id") + " (a pack quest loads with its pack: is the pack on?).") : Describe(q);
        }

        static string Take(LiveRequest r)
        {
            if (!Game.InWorld) return NotIn;
            var q = Find(Int(r, "id"));
            if (q == null) return Err("The game has no quest " + Int(r, "id") + ".");
            var ctrl = QuestController.GetQuestCtrl();
            if (ctrl.receivedQuests != null && ctrl.receivedQuests.ContainsKey(q.id)) return "{\"ok\":true,\"already\":true,\"quest\":" + Describe(q) + "}";
            var st = QuestController.getQuestStatus(q);
            if (QuestController.checkStatus(st, QuestStatus.Cleared)) return Err("It's done already: reset it first.");
            if (QuestController.checkStatus(st, QuestStatus.Blocked)) return Err("Its requirements aren't met yet (flags or items it needs).");
            bool ok = ctrl.questReceive(q.id);
            return ok ? "{\"ok\":true,\"quest\":" + Describe(q) + "}" : Err("The game wouldn't give it (switched off in its pack?).");
        }

        /// <summary>As if never done: off the quest log (progress gone) and its "done" flags cleared, with any others listed (setFlagsOnClear).</summary>
        static string Reset(LiveRequest r)
        {
            if (!Game.InWorld) return NotIn;
            var q = Find(Int(r, "id"));
            if (q == null) return Err("The game has no quest " + Int(r, "id") + ".");
            var ctrl = QuestController.GetQuestCtrl();
            if (ctrl.receivedQuests != null && ctrl.receivedQuests.ContainsKey(q.id)) ctrl.questCancel(q.id);
            var cleared = new List<int>();
            if (q.clearFlagIndex > 0 && FlagController.flags.Get(q.clearFlagIndex)) { FlagController.setByIndex(q.clearFlagIndex, false); cleared.Add(q.clearFlagIndex); }
            if (q.clearWorkFlagIndex > 0 && FlagController.flags_work.Get(q.clearWorkFlagIndex)) FlagController.setByIndex_work(q.clearWorkFlagIndex, false);
            // more flags to clear, from the editor (the quest's setFlagsOnClear): {"flags":[9001,9002]}
            var body = r.Body ?? "";
            int i = body.IndexOf("\"flags\"", StringComparison.Ordinal);
            if (i >= 0)
            {
                int a = body.IndexOf('[', i), b = a < 0 ? -1 : body.IndexOf(']', a);
                if (a > 0 && b > a)
                    foreach (var part in body.Substring(a + 1, b - a - 1).Split(','))
                    {
                        int f;
                        if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out f) && f > 0 && f < FlagController.flags.Length && FlagController.flags.Get(f))
                        { FlagController.setByIndex(f, false); cleared.Add(f); }
                    }
            }
            var map = GUI_Map.getGUIInstance(); if (map != null) map.updateQuestStatus();
            return "{\"ok\":true,\"flagsCleared\":[" + string.Join(",", cleared.Select(x => x.ToString()).ToArray()) + "],\"quest\":" + Describe(q) + "}";
        }

        static string ToNpc(LiveRequest r)
        {
            if (!Game.InWorld) return NotIn;
            int cid = Int(r, "cid");
            if (cid <= 0) return Err("Which NPC?");
            var me = Game.Stats;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                if (s == null || s == me || s.isDead || s.getCID() != cid) continue;
                var t = s.transform;
                Game.Teleport(LiveLink.Ground(t.position + t.forward * 2.5f));
                return "{\"ok\":true,\"name\":" + LiveLink.Quote(s.dispName) + "}";
            }
            return Err("That NPC isn't in this zone (" + Game.Zone.Replace("Zone_", "") + ").");
        }
    }
}
