using System;
using System.Collections.Generic;

namespace QuestLoader
{
    // The JSON format for quests. Readable and editable, and the same format for the
    // exported originals (Mods\QuestLoader\export\quests.json) and quest packs (Mods\Quests\*.json).

    /// <summary>A quest pack file: Mods\Quests\&lt;name&gt;.json</summary>
    public class QuestPack
    {
        public string name;
        public string author;
        public string description;
        public List<QuestDef> quests = new List<QuestDef>();
    }

    public class QuestDef
    {
        /// <summary>Quest id. Same id as an original quest = replace it. New quests: use 900000001 and up.</summary>
        public int id;
        public string title;
        public string objective;
        /// <summary>QuestType flags: Normal, Daily, Repeatable, Seasonal, Errand, Kill, Collect, Defence, Message, ItemReceive, TimeAttack, Main.</summary>
        public List<string> types;
        public bool enabled = true;
        public bool autoAccept;
        public bool autoReopen;
        public bool repeatable;
        public int priority;

        /// <summary>NPC id (cid) who gives the quest, and who finishes it.</summary>
        public int giver;
        public int ender;

        /// <summary>What the giver says.</summary>
        public string description;
        /// <summary>What the ender says when you hand in.</summary>
        public string descriptionForEnder;
        /// <summary>Extra lines (used by Message-type quests).</summary>
        public string[] messages;
        /// <summary>Other languages: jp, es, fr, pt, ru, zh. Kept as-is from the original game.</summary>
        public Dictionary<string, Translation> translations;

        /// <summary>Items the player receives when accepting.</summary>
        public List<ItemCount> receives;
        /// <summary>What the player must do.</summary>
        public List<Target> targets;
        public List<Reward> rewards;

        /// <summary>Progress flag set when the quest is cleared (0 = none). Mods should use 9000-9999.</summary>
        public int clearFlag;
        public int clearWorkFlag;
        /// <summary>Conditions for the quest to be offered.</summary>
        public Requirements requires;
        /// <summary>Flags changed when the quest is cleared.</summary>
        public List<FlagValue> setFlagsOnClear;
    }

    public class Translation
    {
        public string description;
        public string descriptionForEnder;
        public string[] messages;
    }

    public class ItemCount
    {
        public int item;
        public int count = 1;
    }

    public class Target
    {
        /// <summary>"item" = bring items, "kill" = defeat monsters.</summary>
        public string kind;
        public int item;
        /// <summary>For kill targets: the monster's name …</summary>
        public string monster;
        /// <summary>… or its id (used when monster is empty).</summary>
        public int monsterId;
        /// <summary>How many. For item targets, -1 means "just have one, don't take it" (as the game uses it).</summary>
        public int count = 1;
        /// <summary>Item targets: also count items in the bank.</summary>
        public bool includeBank = true;
    }

    public class Reward
    {
        /// <summary>"exp", "gold" or "item".</summary>
        public string kind;
        /// <summary>exp/gold amount, or item count.</summary>
        public int amount = 1;
        public int item;
    }

    public class FlagValue
    {
        public int flag;
        public bool on = true;
    }

    public class ItemReq
    {
        public int item;
        public int count = 1;
        /// <summary>true = at least count, false = fewer than count.</summary>
        public bool atLeast = true;
        public bool includeBank;
    }

    public class Requirements
    {
        public List<FlagValue> flagsAll, flagsAny, workFlagsAll, workFlagsAny;
        public List<ItemReq> itemsAll, itemsAny;
    }

    /// <summary>One entry of export\items.json</summary>
    public class ItemInfo { public int id; public string name; }

    /// <summary>One entry of export\npcs.json</summary>
    public class NpcInfo
    {
        public int cid;
        public string name;
        public string zone;
        public float x, y, z;
    }
}
