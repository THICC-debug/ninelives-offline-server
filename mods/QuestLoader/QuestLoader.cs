using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace QuestLoader
{
    [ModInfo("questloader", "QuestLoader", "1.2.0", Author = "Nine Lives offline project",
        Description = "Loads custom quests from Mods\\Quests and exports the game's quests, items and NPCs for the quest editor.")]
    public class QuestLoader : Mod
    {
        internal static QuestLoader Instance;
        internal static string ExportDir, PacksDir;
        static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
        };
        // The game's Newtonsoft is old: formatting is its own argument.
        static string ToJson(object o) { return JsonConvert.SerializeObject(o, Formatting.Indented, Json); }

        bool exported;
        float scanNpcsAt = -1;
        /// <summary>Quests added by packs (not replacements of originals).</summary>
        internal static readonly HashSet<int> NewQuestIds = new HashSet<int>();
        /// <summary>Original quests a pack currently replaces.</summary>
        static readonly HashSet<int> ReplacedIds = new HashSet<int>();
        /// <summary>The game's own quests, as they were before any pack, for restoring on reload.</summary>
        static readonly Dictionary<int, QuestDef> OriginalDefs = new Dictionary<int, QuestDef>();

        public override void Load()
        {
            Instance = this;
            ExportDir = Path.Combine(Path.Combine(ModLoader.ModsDir, "QuestLoader"), "export");
            PacksDir = Path.Combine(ModLoader.ModsDir, "Quests");
            Directory.CreateDirectory(ExportDir);
            Directory.CreateDirectory(PacksDir);
            new Harmony("ninelives.questloader").PatchAll(typeof(QuestLoader).Assembly);
            Commands.Register("reloadquests", "reloadquests", "reload quest packs from Mods\\Quests", args => Reload());
            Log("ready - quest packs go in Mods\\Quests");
        }

        // ------------------------------------------------------------ quests built by the game
        internal void OnQuestsBuilt(QuestControllerBase ctrl)
        {
            if (!exported)
            {
                exported = true;   // the game rebuilds quests per zone; export the originals once
                try { ExportQuests(ctrl); } catch (Exception e) { Log("quest export failed: " + e); }
                try { ExportItems(); } catch (Exception e) { Log("item export failed: " + e); }
            }
            try { Log(ApplyPacks(ctrl, false)); } catch (Exception e) { Log("loading quest packs failed: " + e); }
        }

        void ExportQuests(QuestControllerBase ctrl)
        {
            var list = ctrl.quests.Values.OrderBy(q => q.id).Select(Convert.ToDef).ToList();
            foreach (var d in list) OriginalDefs[d.id] = d;
            File.WriteAllText(Path.Combine(ExportDir, "quests.json"), ToJson(list));
            Log("exported " + list.Count + " original quests");
        }

        /// <summary>Re-read every pack and update the live game. Used by the reloadquests command.</summary>
        public string Reload()
        {
            var ctrl = QuestController.GetQuestCtrl();
            if (ctrl == null) return "Not in the world yet.";
            string summary = ApplyPacks(ctrl, true);
            Log("reload: " + summary);
            Messages.Show("Quest packs reloaded");
            return summary;
        }

        void ExportItems()
        {
            var path = Path.Combine(ExportDir, "items.json");
            if (File.Exists(path)) return;   // item data never changes
            var items = new List<ItemInfo>();
            foreach (var id in Item.getItemXMLIDList())
            {
                var name = Game.ItemName(id);
                if (!string.IsNullOrEmpty(name)) items.Add(new ItemInfo { id = id, name = name });
            }
            items.Sort((a, b) => a.id.CompareTo(b.id));
            File.WriteAllText(path, ToJson(items));
            Log("exported " + items.Count + " item names");
        }

        /// <summary>
        /// Apply every pack to the controller. reload = the controller already has pack quests in it
        /// (remove ones that were deleted, restore originals no longer replaced). Existing quests are
        /// updated in place, so quests the player has accepted keep working.
        /// </summary>
        string ApplyPacks(QuestControllerBase ctrl, bool reload)
        {
            var problems = new List<string>();
            var defs = new List<KeyValuePair<string, QuestDef>>();
            foreach (var file in Directory.GetFiles(PacksDir, "*.json").OrderBy(f => f))
            {
                QuestPack pack;
                try { pack = JsonConvert.DeserializeObject<QuestPack>(File.ReadAllText(file)); }
                catch (Exception e) { problems.Add(Path.GetFileName(file) + ": " + e.Message); continue; }
                if (pack == null || pack.quests == null) continue;
                foreach (var def in pack.quests) if (def != null) defs.Add(new KeyValuePair<string, QuestDef>(Path.GetFileName(file), def));
            }
            var ids = new HashSet<int>(defs.Select(kv => kv.Value.id));

            int removed = 0, restored = 0;
            if (reload)
            {
                foreach (var id in NewQuestIds.Where(i => !ids.Contains(i)).ToList())
                {
                    Quest q;
                    if (ctrl.quests.TryGetValue(id, out q))
                    {
                        Convert.Unlink(ctrl, q);
                        ctrl.quests.Remove(id);
                        var qc = ctrl as QuestController;
                        if (qc != null && qc.receivedQuests != null && qc.receivedQuests.ContainsKey(id))
                        {
                            if (PackGone(id))
                            {
                                try { qc.questCancel(id); problems.Add("quest " + id + ": its pack was deleted, so it left your quest log"); }
                                catch (Exception e) { problems.Add("quest " + id + " was removed but couldn't leave your quest log: " + e.Message); }
                            }
                            else problems.Add("quest " + id + " is in your quest log but its pack is off or has a problem: it comes back when the pack loads again");
                            // (a stand-in keeps the game happy until then)
                            Convert.Apply(ctrl, StandIn(id));
                        }
                    }
                    NewQuestIds.Remove(id);
                    removed++;
                }
                foreach (var id in ReplacedIds.Where(i => !ids.Contains(i)).ToList())
                {
                    QuestDef orig;
                    if (OriginalDefs.TryGetValue(id, out orig)) { Convert.Apply(ctrl, orig); restored++; }
                    ReplacedIds.Remove(id);
                }
            }
            else
            {
                NewQuestIds.Clear();   // a fresh controller (new zone) only has the game's own quests
                ReplacedIds.Clear();
            }

            int added = 0, replaced = 0, updated = 0;
            foreach (var kv in defs)
            {
                var def = kv.Value;
                try
                {
                    bool isOriginal = OriginalDefs.Count > 0 ? OriginalDefs.ContainsKey(def.id) : ctrl.quests.ContainsKey(def.id) && !NewQuestIds.Contains(def.id);
                    bool wasThere = isOriginal ? ReplacedIds.Contains(def.id) : NewQuestIds.Contains(def.id);
                    // An accepted quest keeps its progress per goal, by position: changed goals scramble it.
                    Quest had; var rqc = ctrl as QuestController;
                    if (reload && rqc != null && rqc.receivedQuests != null && rqc.receivedQuests.ContainsKey(def.id) && ctrl.quests.TryGetValue(def.id, out had)
                        && (had.targets == null ? 0 : had.targets.Length) != (def.targets == null ? 0 : def.targets.Count(t => t != null)))
                        problems.Add("\"" + def.title + "\" is in your quest log and its goals changed: abandon it and take it again so its progress matches");
                    Convert.Apply(ctrl, def);
                    if (isOriginal) ReplacedIds.Add(def.id); else { NewQuestIds.Add(def.id); PackOf[def.id] = kv.Key; }
                    if (reload && wasThere) updated++; else if (isOriginal) replaced++; else added++;
                }
                catch (Exception e) { problems.Add(kv.Key + ": quest " + def.id + " failed: " + e.Message); }
            }

            try { KeepKnownIds(ctrl); } catch (Exception e) { problems.Add("remembering quest ids failed: " + e.Message); }

            var summary = "quest packs: " + added + " added, " + replaced + " replaced" +
                          (reload ? ", " + updated + " updated, " + removed + " removed, " + restored + " originals restored" : "");
            if (reload)
            {
                // Finished quests aren't offered again, so edits to them aren't visible. Say so.
                var finished = defs.Select(kv => kv.Value).Where(d => d.clearFlag > 0 && Game.GetFlag(d.clearFlag)).ToList();
                foreach (var d in finished.Take(3))
                    summary += "\n\"" + d.title + "\" is already finished, so it isn't offered. To replay it: flag " + d.clearFlag + " off";
            }
            foreach (var p in problems) { Log(p); }
            if (problems.Count > 0) summary += "\n" + problems.Count + " problem(s): " + problems[0] + (problems.Count > 1 ? " (more in Mods\\ModLoader.log)" : "");
            return summary;
        }

        // ------------------------------------------------------------ quests whose pack was removed
        // The server keeps the ids of accepted quests, and the game looks each one up when you log in with
        // no check: an id that's gone stops the whole quest system starting. So every pack quest id ever
        // loaded is remembered, a removed one gets a hidden stand-in, and the stand-in is then cancelled
        // (the game tells the server, and it's gone for good).
        // Only a quest whose pack file is gone for good is cancelled: a pack that's switched off, or that
        // has a mistake in it right now, keeps the player's quest (and its progress) for when it's back.
        static readonly HashSet<int> Placeholders = new HashSet<int>();
        static readonly Dictionary<int, string> PackOf = new Dictionary<int, string>();   // quest id -> pack file name

        static QuestDef StandIn(int id)
        {
            return new QuestDef
            {
                id = id, title = "(quest from a missing pack)", objective = "Its quest pack is deleted, switched off or has a problem.", enabled = false,
                types = new List<string> { "Normal" }, targets = new List<Target>(), rewards = new List<Reward>(),
            };
        }

        /// <summary>Was this quest's pack deleted (no file, on or off)? Unknown pack: no, never cancel.</summary>
        bool PackGone(int id)
        {
            string pack;
            if (!PackOf.TryGetValue(id, out pack) || string.IsNullOrEmpty(pack)) return false;
            var f = Path.Combine(PacksDir, pack);
            return !File.Exists(f) && !File.Exists(f + ".disabled");
        }

        void KeepKnownIds(QuestControllerBase ctrl)
        {
            var path = Path.Combine(ExportDir, "known_quests.json");   // { "id": "pack file" }
            bool changed = !File.Exists(path);
            if (File.Exists(path))
            {
                try
                {
                    foreach (var kv in JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>())
                    {
                        int i; if (int.TryParse(kv.Key, out i) && !PackOf.ContainsKey(i)) PackOf[i] = kv.Value;
                    }
                }
                catch { changed = true; }   // damaged: rebuilt
            }
            var old = Path.Combine(ExportDir, "known_ids.json");   // the first version: ids only (their packs unknown: never cancelled)
            if (File.Exists(old))
            {
                try { foreach (var i in JsonConvert.DeserializeObject<List<int>>(File.ReadAllText(old)) ?? new List<int>()) if (!PackOf.ContainsKey(i)) { PackOf[i] = null; changed = true; } }
                catch { }
            }
            foreach (var i in NewQuestIds) if (!PackOf.ContainsKey(i)) changed = true;   // (PackOf already holds them; written below)
            if (changed || NewQuestIds.Count > 0)
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(PackOf.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            foreach (var id in PackOf.Keys.ToList())
            {
                if (ctrl.quests.ContainsKey(id)) continue;
                Convert.Apply(ctrl, StandIn(id));   // so logging in with it accepted doesn't stop the quest system
                if (PackGone(id)) Placeholders.Add(id);
            }
        }

        void CancelPlaceholders()
        {
            var qc = QuestController.GetQuestCtrl() as QuestController;
            if (qc == null || !qc.initialized || qc.receivedQuests == null) return;
            foreach (var id in Placeholders.ToList())
            {
                if (qc.receivedQuests.ContainsKey(id) && PackGone(id)) { try { qc.questCancel(id); Log("quest " + id + ": its pack was deleted, so it left your quest log"); } catch (Exception e) { Log("quest " + id + ": " + e.Message); } }
                Placeholders.Remove(id);
            }
        }

        // ------------------------------------------------------------ NPC list for the editor
        public override void OnZoneLoaded(string zone) { scanNpcsAt = Time.realtimeSinceStartup + 8f; }   // let NPCs spawn

        public override void OnUpdate()
        {
            if (Placeholders.Count > 0 && Time.frameCount % 30 == 0) CancelPlaceholders();
            if (scanNpcsAt < 0 || Time.realtimeSinceStartup < scanNpcsAt) return;
            scanNpcsAt = -1;
            try { ScanNpcs(); } catch (Exception e) { Log("NPC scan failed: " + e.Message); }
        }

        void ScanNpcs()
        {
            var path = Path.Combine(ExportDir, "npcs.json");
            var known = new Dictionary<string, NpcInfo>();
            List<NpcInfo> old = null;
            if (File.Exists(path)) { try { old = JsonConvert.DeserializeObject<List<NpcInfo>>(File.ReadAllText(path)); } catch { } }   // damaged: start a fresh list
            foreach (var n in old ?? new List<NpcInfo>()) if (n != null) known[n.zone + "|" + n.cid] = n;
            int found = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(CommonStats)))
            {
                var s = (CommonStats)o;
                int cid;
                if (s == null || string.IsNullOrEmpty(s.cid) || !int.TryParse(s.cid, out cid) || cid <= 0) continue;
                var p = s.transform.position;
                known[Game.Zone + "|" + cid] = new NpcInfo { cid = cid, name = s.dispName, zone = Game.Zone, x = p.x, y = p.y, z = p.z };
                found++;
            }
            var list = known.Values.OrderBy(n => n.cid).ToList();
            File.WriteAllText(path, ToJson(list));
            Log("NPC list: " + found + " seen in " + Game.Zone + ", " + list.Count + " known in total");
        }
    }

    [HarmonyPatch(typeof(Quest_Startup), nameof(Quest_Startup.setQuests))]
    static class AfterQuestsBuilt
    {
        static void Postfix(QuestControllerBase questCtrl_)
        {
            if (QuestLoader.Instance != null) QuestLoader.Instance.OnQuestsBuilt(questCtrl_);
        }
    }

    // An NPC offers the FIRST unfinished quest in its list, even if that one is still locked
    // (e.g. "Crafting?" waits for a recipe item), which hides anything after it, including
    // quests added by packs. When the game's pick has nothing to offer (locked or just a chat
    // line), offer the first available pack quest instead. Original quests behave as before.
    [HarmonyPatch(typeof(QuestController), nameof(QuestController.getTopActiveQuest), new[] { typeof(int), typeof(CommonStats) })]
    static class OfferPackQuests
    {
        static void Postfix(int cid_, ref ActiveQuestResultSet __result)
        {
            try { Offer(cid_, ref __result); }
            catch (Exception e) { ModLoader.Log("QuestLoader: offering pack quests failed: " + e.Message); }
        }
        static void Offer(int cid_, ref ActiveQuestResultSet __result)
        {
            if (QuestLoader.NewQuestIds.Count == 0) return;
            if (__result.quest != null && !__result.isBlocked && !__result.isMessage) return;
            var list = QuestController.getQuestsByCID(cid_);
            if (list == null) return;
            Quest best = null;
            QuestStatus bestStatus = 0;
            foreach (var q in list)
            {
                if (!QuestLoader.NewQuestIds.Contains(q.id)) continue;
                var st = QuestController.getQuestStatus(q);
                bool usable = QuestController.checkStatus(st, QuestStatus.Enable)
                              && !QuestController.checkStatus(st, QuestStatus.Cleared)
                              && !QuestController.checkStatus(st, QuestStatus.Blocked)
                              && !QuestController.checkStatus(st, QuestStatus.Message);
                if (usable && (best == null || q.priority > best.priority)) { best = q; bestStatus = st; }
            }
            if (best == null) return;
            __result = new ActiveQuestResultSet
            {
                quest = best, status = bestStatus,
                isReceived = QuestController.checkStatus(bestStatus, QuestStatus.Received),
                isErrand = QuestController.checkStatus(bestStatus, QuestStatus.Errand),
            };
        }
    }

    /// <summary>Game Quest &lt;-&gt; QuestDef.</summary>
    static class Convert
    {
        static readonly string[] Langs = { "jp", "es", "fr", "pt", "ru", "zh" };

        public static QuestDef ToDef(Quest q)
        {
            var d = new QuestDef
            {
                id = q.id, title = q.title, objective = NullIfEmpty(q.objective),
                types = TypeNames(q.questType),
                enabled = q.isEnable, autoAccept = q.isAutoAccept, autoReopen = q.isAutoReopen,
                repeatable = q.isRepeatable, priority = q.priority,
                giver = q.giverCID, ender = q.enderCID,
                description = q.description, descriptionForEnder = q.descriptionForEnder,
                messages = Empty(q.message) ? null : q.message,
                clearFlag = q.clearFlagIndex, clearWorkFlag = q.clearWorkFlagIndex,
            };
            var tr = new Dictionary<string, Translation>();
            foreach (var l in Langs)
            {
                var t = new Translation
                {
                    description = NullIfEmpty((string)Field(q, "description_" + l)),
                    descriptionForEnder = NullIfEmpty((string)Field(q, "descriptionForEnder_" + l)),
                    messages = Empty((string[])Field(q, "message_" + l)) ? null : (string[])Field(q, "message_" + l),
                };
                if (t.description != null || t.descriptionForEnder != null || t.messages != null) tr[l] = t;
            }
            if (tr.Count > 0) d.translations = tr;

            if (q.receives != null && q.receives.Length > 0)
                d.receives = q.receives.Select(r => new ItemCount { item = r.itemID, count = r.count }).ToList();
            if (q.targets != null && q.targets.Length > 0)
                d.targets = q.targets.Select(ToTarget).Where(t => t != null).ToList();
            if (q.rewards != null && q.rewards.Length > 0)
                d.rewards = q.rewards.Select(ToReward).Where(r => r != null).ToList();

            var req = new Requirements
            {
                flagsAll = Flags(q.flagPattern != null ? q.flagPattern.AND_FlagsPattern : null),
                flagsAny = Flags(q.flagPattern != null ? q.flagPattern.OR_FlagsPattern : null),
                workFlagsAll = Flags(q.flagPattern != null ? q.flagPattern.AND_WorkFlagsPattern : null),
                workFlagsAny = Flags(q.flagPattern != null ? q.flagPattern.OR_WorkFlagsPattern : null),
                itemsAll = Items(q.itemPattern != null ? q.itemPattern.AND_ItemPattern : null),
                itemsAny = Items(q.itemPattern != null ? q.itemPattern.OR_ItemPattern : null),
            };
            if (req.flagsAll != null || req.flagsAny != null || req.workFlagsAll != null || req.workFlagsAny != null || req.itemsAll != null || req.itemsAny != null)
                d.requires = req;
            d.setFlagsOnClear = Flags(q.setFlagsByClear);
            return d;
        }

        /// <summary>
        /// Things the game itself can't cope with (it would throw in its own loops, every second or on every
        /// kill), checked before anything is changed: the quest is then reported, not half applied.
        /// </summary>
        static void Validate(QuestDef d)
        {
            var targets = (d.targets ?? new List<Target>()).Where(t => t != null).ToList();
            bool kills = targets.Any(t => string.Equals(t.kind, "kill", StringComparison.OrdinalIgnoreCase));
            bool items = targets.Any(t => !string.Equals(t.kind, "kill", StringComparison.OrdinalIgnoreCase));
            if (kills && items) throw new ArgumentException("a quest can have item goals or kill goals, not both (make two quests)");
            if (targets.Count > 8) throw new ArgumentException("at most 8 goals (it has " + targets.Count + ")");
            foreach (var t in targets)
                if (!string.IsNullOrEmpty(t.kind) && !t.kind.Equals("kill", StringComparison.OrdinalIgnoreCase) && !t.kind.Equals("item", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("unknown goal kind \"" + t.kind + "\" (item or kill)");
            Action<int, int, string> range = (v, max, what) => { if (v < 0 || v >= max) throw new ArgumentException(what + " " + v + " is out of range (0-" + (max - 1) + ")"); };
            range(d.clearFlag, 10000, "clearFlag"); range(d.clearWorkFlag, 100000, "clearWorkFlag");
            foreach (var f in d.setFlagsOnClear ?? new List<FlagValue>()) if (f != null) range(f.flag, 10000, "flag");
            if (d.requires != null)
            {
                foreach (var f in (d.requires.flagsAll ?? new List<FlagValue>()).Concat(d.requires.flagsAny ?? new List<FlagValue>())) if (f != null) range(f.flag, 10000, "required flag");
                foreach (var f in (d.requires.workFlagsAll ?? new List<FlagValue>()).Concat(d.requires.workFlagsAny ?? new List<FlagValue>())) if (f != null) range(f.flag, 100000, "required work flag");
            }
        }

        public static void Apply(QuestControllerBase ctrl, QuestDef d)
        {
            Validate(d);
            // (null entries in the lists are skipped, not crashed on halfway)
            if (d.targets != null) d.targets.RemoveAll(x => x == null);
            if (d.rewards != null) d.rewards.RemoveAll(x => x == null);
            if (d.receives != null) d.receives.RemoveAll(x => x == null);
            if (d.setFlagsOnClear != null) d.setFlagsOnClear.RemoveAll(x => x == null);
            Quest q;
            QuestType type = ParseTypes(d.types);
            // Kill goals only count in Kill quests (QuestController.onKill).
            if (d.targets != null && d.targets.Exists(t => string.Equals(t.kind, "kill", StringComparison.OrdinalIgnoreCase))) type |= QuestType.Kill;
            // The game only counts item goals for Collect quests (GUI_ActionDock), so a quest with
            // an item goal but no Collect type could never be handed in. Add it.
            if (d.targets != null && d.targets.Exists(t => !string.Equals(t.kind, "kill", StringComparison.OrdinalIgnoreCase))) type |= QuestType.Collect;
            if (ctrl.quests.TryGetValue(d.id, out q))
            {
                Unlink(ctrl, q);
                q.questType = type;
                q.clearFlagIndex = d.clearFlag;
                q.isEnable = d.enabled;
            }
            else q = ctrl.addQuest(d.id, d.clearFlag, d.enabled, type);

            q.title = d.title ?? "";
            q.objective = d.objective ?? "";
            q.isAutoAccept = d.autoAccept;
            q.isAutoReopen = d.autoReopen;
            q.isRepeatable = d.repeatable;
            q.priority = d.priority;
            q.description = d.description;
            q.descriptionForEnder = d.descriptionForEnder;
            q.message = d.messages;
            q.clearWorkFlagIndex = d.clearWorkFlag;
            // Other languages: only what the pack says (otherwise a replaced quest would still tell the old
            // story to a player whose game is in another language).
            foreach (var l in Langs)
            {
                SetField(q, "description_" + l, "");
                SetField(q, "descriptionForEnder_" + l, "");
                SetField(q, "message_" + l, new string[0]);
            }
            if (d.translations != null)
                foreach (var kv in d.translations)
                {
                    if (Array.IndexOf(Langs, kv.Key) < 0 || kv.Value == null) continue;
                    SetField(q, "description_" + kv.Key, kv.Value.description);
                    SetField(q, "descriptionForEnder_" + kv.Key, kv.Value.descriptionForEnder);
                    SetField(q, "message_" + kv.Key, kv.Value.messages);
                }

            q.receives = d.receives == null ? null : d.receives.Select(r => new QuestReceiveItemData(r.item, r.count)).ToArray();
            q.targets = d.targets == null ? null : d.targets.Select(FromTarget).ToArray();
            q.rewards = d.rewards == null ? null : d.rewards.Select(FromReward).ToArray();

            var req = d.requires ?? new Requirements();
            q.flagPattern = new FlagController_FlagPatternSet
            {
                AND_FlagsPattern = GameFlags(req.flagsAll), OR_FlagsPattern = GameFlags(req.flagsAny),
                AND_WorkFlagsPattern = GameFlags(req.workFlagsAll), OR_WorkFlagsPattern = GameFlags(req.workFlagsAny),
            };
            q.itemPattern = new PlayerItemChecker { AND_ItemPattern = GameItems(req.itemsAll), OR_ItemPattern = GameItems(req.itemsAny) };
            q.setFlagsByClear = d.setFlagsOnClear == null ? null : GameFlags(d.setFlagsOnClear);

            if (d.giver > 0) ctrl.setLinkGiverCID(d.giver, q);
            if (d.ender > 0) ctrl.setLinkEnderCID(d.ender, q);
        }

        internal static void Unlink(QuestControllerBase ctrl, Quest q)
        {
            foreach (var table in new[] { ctrl.cidToQuestsList, ctrl.cidToQuestsGiverList, ctrl.cidToQuestsEnderList })
                foreach (var l in table.Values) l.Remove(q);
        }

        static Target ToTarget(QuestTargetData t)
        {
            var i = t as QuestTargetData_item;
            if (i != null) return new Target { kind = "item", item = i.itemID, count = i.count, includeBank = i.isIncludeBankCheck };
            var k = t as QuestTargetData_kill;
            if (k != null) return new Target { kind = "kill", monster = NullIfEmpty(k.name), monsterId = k.id, count = k.count };
            return null;
        }

        static QuestTargetData FromTarget(Target t)
        {
            if (t.kind == "kill")
                return string.IsNullOrEmpty(t.monster) ? new QuestTargetData_kill(t.monsterId, t.count) : new QuestTargetData_kill(t.monster, t.count);
            return new QuestTargetData_item(t.item, t.count, t.includeBank);
        }

        static Reward ToReward(QuestRewardData r)
        {
            var it = r as QuestReward_item;
            if (it != null) return new Reward { kind = "item", item = it.itemID, amount = it.count };
            if (r.rewardType == 2) return new Reward { kind = "gold", amount = r.count };
            if (r.rewardType == 3) return new Reward { kind = "exp", amount = r.count };
            return null;
        }

        static QuestRewardData FromReward(Reward r)
        {
            switch (r.kind)
            {
                case "gold": return new QuestReward_gold(r.amount);
                case "exp": return new QuestReward_experience(r.amount);
                default: return new QuestReward_item(r.item, Math.Max(1, r.amount));
            }
        }

        static List<string> TypeNames(QuestType t)
        {
            var l = new List<string>();
            foreach (QuestType v in Enum.GetValues(typeof(QuestType)))
                if ((t & v) != 0) l.Add(v.ToString());
            return l;
        }

        static QuestType ParseTypes(List<string> names)
        {
            QuestType t = 0;
            if (names != null)
                foreach (var n in names) t |= (QuestType)Enum.Parse(typeof(QuestType), n, true);
            return t == 0 ? QuestType.Normal : t;
        }

        static List<FlagValue> Flags(FlagController_FlagDataSet[] a)
        {
            if (a == null || a.Length == 0) return null;
            return a.Select(f => new FlagValue { flag = f.index, on = f.flag }).ToList();
        }

        static FlagController_FlagDataSet[] GameFlags(List<FlagValue> l)
        {
            return l == null ? new FlagController_FlagDataSet[0] : l.Select(f => new FlagController_FlagDataSet(f.flag, f.on)).ToArray();
        }

        static List<ItemReq> Items(PlayerItemChecker_ItemIDSet[] a)
        {
            if (a == null || a.Length == 0) return null;
            return a.Select(i => new ItemReq { item = i.id, count = i.count, atLeast = i.isGreaterEqual, includeBank = i.isIncludeBankCheck }).ToList();
        }

        static PlayerItemChecker_ItemIDSet[] GameItems(List<ItemReq> l)
        {
            if (l == null) return null;
            return l.Select(i => new PlayerItemChecker_ItemIDSet { id = i.item, count = i.count, isGreaterEqual = i.atLeast, isIncludeBankCheck = i.includeBank }).ToArray();
        }

        static object Field(Quest q, string name) { var f = typeof(Quest).GetField(name); return f != null ? f.GetValue(q) : null; }
        static void SetField(Quest q, string name, object v) { var f = typeof(Quest).GetField(name); if (f != null) f.SetValue(q, v); }
        static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }
        static bool Empty(string[] a) { return a == null || a.Length == 0; }
    }
}
