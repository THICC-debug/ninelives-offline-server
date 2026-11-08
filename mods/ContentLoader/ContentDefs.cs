using System.Collections.Generic;

namespace ContentLoader
{
    // The JSON formats. The same shapes are used for the exported originals
    // (Mods\ContentLoader\export\*.json) and for content packs (Mods\Content\*.json).

    /// <summary>A content pack: Mods\Content\&lt;name&gt;.json. Every part is optional.</summary>
    public class ContentPack
    {
        public string name;
        public string author;
        public string description;
        /// <summary>Item changes, by item id.</summary>
        public Dictionary<string, ItemOverride> items;
        /// <summary>Monster and chest loot.</summary>
        public DropTables loot;
        /// <summary>Shop stock (drop id = the shop NPC's id).</summary>
        public DropTables shops;
    }

    public class ItemOverride
    {
        /// <summary>Base price (shops sell for price × buyPriceAmp, buy back for price).</summary>
        public int? price;
        public float? buyPriceAmp;
        /// <summary>Item stat fields to change, e.g. {"weaponDamage": 12, "stackMax": 50}. Names as in export\items.json.</summary>
        public Dictionary<string, object> stats;
        /// <summary>Ignored: the item's name, for readability.</summary>
        public string name;
    }

    public class DropTables
    {
        /// <summary>Shared item groups, by group id. Changing one updates every drop that uses it.</summary>
        public Dictionary<string, List<ItemStack>> groups;
        /// <summary>Drop lists, by drop id. A monster's dropId (export\monsters.json) or a shop NPC's id.</summary>
        public Dictionary<string, DropDef> drops;
    }

    public class DropDef
    {
        /// <summary>Chance a monster leaves a chest at all (0-1). Not used by shops.</summary>
        public float? dropRate;
        /// <summary>How many items in total.</summary>
        public int? min, max;
        public bool? fixedAllDrop;
        /// <summary>Items that always come (every one, up to max). Giving this replaces the original list.</summary>
        public List<ItemStack> always;
        /// <summary>Weighted groups for the rest. Giving this replaces the original list.</summary>
        public List<RandomGroup> random;
        /// <summary>Ignored: a note for people reading the file.</summary>
        public string note;
    }

    public class RandomGroup
    {
        /// <summary>Weight: a group with rate 20 is picked twice as often as one with 10.</summary>
        public int rate = 10;
        /// <summary>Use a shared group (from "groups" or the game's own) …</summary>
        public int group;
        /// <summary>… or list items here.</summary>
        public List<ItemStack> items;
    }

    public class ItemStack
    {
        public int id;
        /// <summary>In "always": how many. In random groups: up to how many (1..count).</summary>
        public int count = 1;
        /// <summary>Ignored: the item's name, for readability.</summary>
        public string name;
    }

    /// <summary>export\monsters.json</summary>
    public class MonsterInfo
    {
        /// <summary>The name shown in game when an enemy pack renames it (EnemyLoader); null when it isn't.</summary>
        public string shownAs;
        public string name;
        public string zone;
        public int dropId;
        /// <summary>Most seen at once in the zone.</summary>
        public int seen;
        /// <summary>The game's character id (kill quests can use it), -1 when it has none.</summary>
        public int cid = -1;
        public string title;
        /// <summary>Base stats of one of them: health, damage, defense, exp, gold …</summary>
        public Dictionary<string, float> stats;
        /// <summary>Where they've been seen: [x, z] on a 5 m grid.</summary>
        public List<int[]> spots;
        /// <summary>Picture in export\monsters\ (without .png).</summary>
        public string pic;
    }
}
