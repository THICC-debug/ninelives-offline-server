using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using NineLivesModLoader;
using UnityEngine;

namespace ContentLoader
{
    [ModInfo("contentloader", "ContentLoader", "1.4.1", Author = "Nine Lives offline project",
        Description = "Change item prices and stats, loot tables and shop stock with JSON content packs in Mods\\Content.")]
    public class ContentLoader : Mod
    {
        internal static ContentLoader Instance;
        internal static string ExportDir, PacksDir;
        static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        static string ToJson(object o) { return JsonConvert.SerializeObject(o, Formatting.Indented, Json); }

        // What the packs ask for, merged (later packs win).
        internal static readonly Dictionary<int, ItemOverride> Items = new Dictionary<int, ItemOverride>();
        static DropTables loot = new DropTables(), shops = new DropTables();
        // Original prices, so a reload can put them back.
        static readonly Dictionary<int, KeyValuePair<int, float>> origPrices = new Dictionary<int, KeyValuePair<int, float>>();
        static bool lootApplied, exported;
        float scanAt = -1, exportAt = -1;

        public override void Load()
        {
            Instance = this;
            ExportDir = Path.Combine(Path.Combine(ModLoader.ModsDir, "ContentLoader"), "export");
            PacksDir = Path.Combine(ModLoader.ModsDir, "Content");
            Directory.CreateDirectory(ExportDir);
            Directory.CreateDirectory(PacksDir);
            ReadPacks();
            new Harmony("ninelives.contentloader").PatchAll(typeof(ContentLoader).Assembly);
            Commands.Register("reloadcontent", "reloadcontent", "reload content packs from Mods\\Content", a => Reload());
            Log("ready - content packs go in Mods\\Content");
        }

        // ------------------------------------------------------------ reading packs
        static List<string> ReadPacks()
        {
            var problems = new List<string>();
            Items.Clear(); loot = new DropTables(); shops = new DropTables();
            foreach (var file in Directory.GetFiles(PacksDir, "*.json").OrderBy(f => f))
            {
                ContentPack p;
                try { p = JsonConvert.DeserializeObject<ContentPack>(File.ReadAllText(file)); }
                catch (Exception e) { problems.Add(Path.GetFileName(file) + ": " + e.Message); continue; }
                if (p == null) continue;
                if (p.items != null)
                    foreach (var kv in p.items)
                    {
                        int id;
                        if (kv.Value == null) continue;   // ("123": null)
                        if (!int.TryParse(kv.Key, out id)) { problems.Add(Path.GetFileName(file) + ": item id '" + kv.Key + "' isn't a number"); continue; }
                        ItemOverride cur;
                        if (!Items.TryGetValue(id, out cur)) Items[id] = cur = new ItemOverride();
                        if (kv.Value.price.HasValue) cur.price = kv.Value.price;
                        if (kv.Value.buyPriceAmp.HasValue) cur.buyPriceAmp = kv.Value.buyPriceAmp;
                        if (kv.Value.stats != null)
                        {
                            if (cur.stats == null) cur.stats = new Dictionary<string, object>();
                            foreach (var s in kv.Value.stats) cur.stats[s.Key] = s.Value;
                        }
                    }
                Merge(loot, p.loot);
                Merge(shops, p.shops);
            }
            foreach (var pr in problems) ModLoader.Log("ContentLoader: " + pr);
            return problems;
        }

        static void Merge(DropTables into, DropTables from)
        {
            if (from == null) return;
            if (from.groups != null) { if (into.groups == null) into.groups = new Dictionary<string, List<ItemStack>>(); foreach (var kv in from.groups) into.groups[kv.Key] = kv.Value; }
            if (from.drops != null) { if (into.drops == null) into.drops = new Dictionary<string, DropDef>(); foreach (var kv in from.drops) into.drops[kv.Key] = kv.Value; }
        }

        // ------------------------------------------------------------ applying
        /// <summary>Change a loot or shop table in place.</summary>
        internal static int ApplyTables(ItemBase table, DropTables t, List<string> problems)
        {
            if (table == null || t == null) return 0;
            int changes = 0;
            if (t.groups != null)
                foreach (var kv in t.groups)
                {
                    int gid;
                    if (!int.TryParse(kv.Key, out gid)) { problems.Add("group id '" + kv.Key + "' isn't a number"); continue; }
                    if (kv.Value == null) continue;
                    var g = new ItemDropRateGroupEx(0);
                    foreach (var s in kv.Value) if (s != null) g.addID(s.id, Math.Max(1, s.count));
                    table.groups[gid] = g;
                    // Drop lists copy a shared group's items when they're built: refresh those copies.
                    foreach (var list in table.dropLists.Values)
                        if (list != null && list.groups != null)
                            foreach (var rg in list.groups)
                                if (rg != null && rg.commonGroupID == gid) { rg.ids.Clear(); rg.idSets.Clear(); foreach (var s in kv.Value) if (s != null) rg.addID(s.id, Math.Max(1, s.count)); }
                    changes++;
                }
            if (t.drops != null)
                foreach (var kv in t.drops)
                {
                    int did;
                    if (!int.TryParse(kv.Key, out did)) { problems.Add("drop id '" + kv.Key + "' isn't a number"); continue; }
                    var d = kv.Value;
                    if (d == null) continue;
                    ItemDropRateGroupList list;
                    if (!table.dropLists.TryGetValue(did, out list) || list == null)
                    {
                        list = new ItemDropRateGroupList();
                        table.dropLists[did] = list;
                    }
                    if (d.dropRate.HasValue) list.dropRate = d.dropRate.Value;
                    if (d.min.HasValue) list.minDropCount = d.min.Value;
                    if (d.max.HasValue) list.maxDropCount = d.max.Value;
                    if (d.fixedAllDrop.HasValue) list.fixedAllDrop = d.fixedAllDrop.Value;
                    if (d.always != null)
                    {
                        list.fixedGroups.Clear();
                        var fg = new ItemDropRateGroupEx(-1);
                        foreach (var s in d.always) if (s != null) fg.addID(s.id, Math.Max(1, s.count));
                        list.fixedGroups.Add(fg);
                    }
                    if (d.random != null)
                    {
                        list.groups.Clear();
                        list.fullRate = 0;
                        foreach (var r in d.random)
                        {
                            if (r == null) continue;
                            var rg = new ItemDropRateGroupEx(Math.Max(1, r.rate));
                            var src = r.items;
                            if ((src == null || src.Count == 0) && r.group != 0)
                            {
                                ItemDropRateGroupEx shared;
                                if (table.groups.TryGetValue(r.group, out shared)) foreach (var s in shared.idSets) rg.addID(s.id, s.stackMax);
                                else problems.Add("drop " + did + ": no group " + r.group);
                                rg.commonGroupID = r.group;
                            }
                            else if (src != null) foreach (var s in src) if (s != null) rg.addID(s.id, Math.Max(1, s.count));
                            list.groups.Add(rg);
                            list.fullRate += rg.rate;
                        }
                    }
                    changes++;
                }
            return changes;
        }

        internal static bool exporting;   // reading the game's own stats for the export: no pack stats on top

        internal static void ApplyPrices(ItemLibraryBase lib)
        {
            if (lib == null || lib.itemIDList == null) return;
            foreach (var kv in origPrices)   // put back anything no longer overridden
            {
                ActionAndButton e;
                if (lib.itemIDList.TryGetValue(kv.Key, out e) && e != null) { e.price = kv.Value.Key; e.buyPriceAmp = kv.Value.Value; }
            }
            foreach (var kv in Items)
            {
                ActionAndButton e;
                if (!lib.itemIDList.TryGetValue(kv.Key, out e) || e == null) continue;
                if (!origPrices.ContainsKey(kv.Key)) origPrices[kv.Key] = new KeyValuePair<int, float>(e.price, e.buyPriceAmp);
                if (kv.Value.price.HasValue) e.price = kv.Value.price.Value;
                if (kv.Value.buyPriceAmp.HasValue) e.buyPriceAmp = kv.Value.buyPriceAmp.Value;
            }
            // Variants ("Fine Knife"...) copied their base item's price when the game loaded: a new price for
            // the base reaches them too, unless a pack prices the variant itself.
            foreach (var kv in lib.itemIDList)
            {
                var e = kv.Value; ItemOverride b;
                if (e == null || e.baseItemID <= 0 || Items.ContainsKey(kv.Key) || !Items.TryGetValue(e.baseItemID, out b)) continue;
                if (!b.price.HasValue && !b.buyPriceAmp.HasValue) continue;
                if (!origPrices.ContainsKey(kv.Key)) origPrices[kv.Key] = new KeyValuePair<int, float>(e.price, e.buyPriceAmp);
                if (b.price.HasValue) e.price = b.price.Value;
                if (b.buyPriceAmp.HasValue) e.buyPriceAmp = b.buyPriceAmp.Value;
            }
        }

        static readonly Dictionary<string, FieldInfo> statFields = typeof(ItemStats).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());

        internal static void ApplyStats(int id, ItemStats st)
        {
            ItemOverride o;
            if (st == null || !Items.TryGetValue(id, out o) || o.stats == null) return;
            foreach (var kv in o.stats)
            {
                FieldInfo f;
                if (!statFields.TryGetValue(kv.Key, out f)) continue;
                try
                {
                    object v = kv.Value;
                    if (f.FieldType.IsEnum) v = Enum.Parse(f.FieldType, System.Convert.ToString(v, CultureInfo.InvariantCulture), true);
                    else v = System.Convert.ChangeType(v, f.FieldType, CultureInfo.InvariantCulture);
                    f.SetValue(st, v);
                }
                catch (Exception e) { ModLoader.Log("ContentLoader: item " + id + " stat " + kv.Key + ": " + e.Message); }
            }
        }

        // ------------------------------------------------------------ hooks called by the patches
        // Runs inside the item library's Awake: change tables and prices here, but don't look up
        // item names yet (the library isn't finished, every lookup would fail). Exports wait.
        internal void OnLibraryReady(ItemLibraryBase lib)
        {
            var problems = new List<string>();
            if (!lootApplied && ItemLibraryBase.itemDropList != null)
            {
                lootApplied = true;
                int n = ApplyTables(ItemLibraryBase.itemDropList, loot, problems);
                if (n > 0) Log("loot: " + n + " change(s) applied");
            }
            ApplyPrices(lib);
            foreach (var p in problems) Log(p);
        }

        internal void OnShopListCreated(ShopItemList list)
        {
            var problems = new List<string>();
            int n = ApplyTables(list, shops, problems);
            if (n > 0) Log("shops: " + n + " change(s) applied");
            foreach (var p in problems) Log(p);
        }

        void Try(string what, Action a) { try { a(); } catch (Exception e) { Log(what + " failed: " + e.Message); } }

        // ------------------------------------------------------------ reload
        public string Reload()
        {
            var problems = ReadPacks();
            // Rebuild the tables from the game's own definitions, then apply the packs again.
            ItemLibraryBase.itemDropList = new ItemDropList();
            int l = ApplyTables(ItemLibraryBase.itemDropList, loot, problems);
            int s = 0;
            if (Universe.shopItemList != null)
            {
                Universe.shopItemList = new ShopItemList();
                s = ApplyTables(Universe.shopItemList, shops, problems);
            }
            // Item stats: forget cached stats so they're reloaded (and re-patched) when next shown.
            var cache = AccessTools.Field(typeof(Item), "itemStatsCache");
            if (cache != null) cache.SetValue(null, null);
            var lib = ItemLibraryBase.GetItemLibraryCtrl();
            if (lib != null) ApplyPrices(lib);
            var summary = "content: " + Items.Count + " item change(s), " + l + " loot and " + s + " shop table change(s)";
            if (problems.Count > 0) summary += "\n" + problems.Count + " problem(s): " + problems[0];
            Log("reload: " + summary);
            Messages.Show("Content packs reloaded");
            return summary;
        }

        // ------------------------------------------------------------ export
        // The game's own tables never change, so each file is written once (delete it to redo it).
        // The work runs a few milliseconds per frame so the game never stalls, and item names are
        // looked up once per base item: the game re-parses an item's XML on every uncached lookup,
        // and there are thousands of variants ("Fine Knife"), which used to freeze the game for minutes.
        System.Collections.IEnumerator exportJob;
        readonly Dictionary<int, string> nameCache = new Dictionary<int, string>();

        bool NeedsExport(string file) { return !File.Exists(Path.Combine(ExportDir, file)); }

        System.Collections.IEnumerator ExportAll()
        {
            var lib = ItemLibraryBase.GetItemLibraryCtrl();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (NeedsExport("loot.json")) { var e = Export(new ItemDropList(), "loot.json"); while (e.MoveNext()) yield return null; }
            if (NeedsExport("shops.json")) { var e = Export(new ShopItemList(), "shops.json"); while (e.MoveNext()) yield return null; }
            if (lib != null && lib.itemIDList != null)
            {
                if (NeedsExport("items.json")) { var e = ExportItems(lib); while (e.MoveNext()) yield return null; }
                if (NeedsExport("variants.json")) { var e = ExportVariants(lib); while (e.MoveNext()) yield return null; }
                if (NeedsExport("icons.json")) { var e = ExportIcons(lib); while (e.MoveNext()) yield return null; }
            }
            Log("exports done in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s (spread over frames)");
        }

        /// <summary>Item name, with base names cached: a variant is its affix + its base item's name.</summary>
        string NameOf(int id)
        {
            string n;
            if (nameCache.TryGetValue(id, out n)) return n;
            var lib = ItemLibraryBase.GetItemLibraryCtrl();
            ActionAndButton e = null;
            if (lib != null && lib.itemIDList != null) lib.itemIDList.TryGetValue(id, out e);
            if (e != null && e.baseItemID > 0 && e.baseItemID != id)
            {
                var baseName = NameOf(e.baseItemID);
                string affix = null;
                int i = lib.itemRandomCodeIDs != null ? lib.itemRandomCodeIDs.IndexOf(e.randomCodeID) : -1;
                if (e.randomCodeID != 0 && i >= 0 && lib.itemRandomCodes[i].affixType != ItemAffix.none)
                    affix = lib.itemRandomCodes[i].affixType.ToString();
                n = baseName == null ? null : affix != null ? affix + " " + baseName : baseName;
            }
            else n = Game.ItemName(id);
            nameCache[id] = n;
            return n;
        }

        System.Collections.IEnumerator Export(ItemBase table, string file)
        {
            var t = new DropTables { groups = new Dictionary<string, List<ItemStack>>(), drops = new Dictionary<string, DropDef>() };
            int n = 0;
            foreach (var kv in table.groups.OrderBy(k => k.Key))
            {
                t.groups[kv.Key.ToString()] = Stacks(kv.Value);
                if (++n % 20 == 0) yield return null;
            }
            foreach (var kv in table.dropLists.OrderBy(k => k.Key))
            {
                var l = kv.Value;
                if (l == null) continue;
                var d = new DropDef { dropRate = l.dropRate, min = l.minDropCount, max = l.maxDropCount, fixedAllDrop = l.fixedAllDrop ? true : (bool?)null };
                var always = l.fixedGroups.Where(g => g != null).SelectMany(Stacks).ToList();
                if (always.Count > 0) d.always = always;
                if (l.groups.Count > 0)
                    d.random = l.groups.Select(g => g.commonGroupID != 0 ? new RandomGroup { rate = g.rate, group = g.commonGroupID }
                                                                        : new RandomGroup { rate = g.rate, items = Stacks(g) }).ToList();
                t.drops[kv.Key.ToString()] = d;
                if (++n % 20 == 0) yield return null;
            }
            File.WriteAllText(Path.Combine(ExportDir, file), ToJson(t));
            Log("exported " + t.drops.Count + " drop lists and " + t.groups.Count + " groups to " + file);
        }

        /// <summary>
        /// Every item's icon as a PNG (export\icons\icon_set_n.png), and which icon each item uses (icons.json),
        /// for the editor's lists. Many items share an icon, so each picture is saved once.
        /// </summary>
        System.Collections.IEnumerator ExportIcons(ItemLibraryBase lib)
        {
            var dir = Path.Combine(ExportDir, "icons");
            Directory.CreateDirectory(dir);
            var map = new SortedDictionary<int, string>();
            var done = new Dictionary<string, bool>();
            var hasData = new HashSet<int>(Item.getItemXMLIDList());
            int n = 0, saved = 0;
            foreach (var kv in lib.itemIDList.ToList())
            {
                var e = kv.Value;
                if (e == null || !hasData.Contains(kv.Key)) continue;
                if (e.baseItemID > 0 && e.baseItemID != kv.Key) continue;   // a variant: it has its base item's icon
                if (e.setNumber < 0 || e.number < 0) continue;
                var name = "icon_" + e.setNumber + "_" + e.number;
                bool ok;
                if (!done.TryGetValue(name, out ok))
                {
                    ok = false;
                    try { ok = SaveIcon(GUI_ActionButtonTypes.getIconFilename(e.setNumber, e.number), Path.Combine(dir, name + ".png")); }
                    catch (Exception ex) { if (saved == 0 && n < 3) Log("icon " + name + ": " + ex.Message); }
                    done[name] = ok;
                    if (ok) saved++;
                    if (++n % 10 == 0) yield return null;
                }
                if (ok) map[kv.Key] = name;
            }
            File.WriteAllText(Path.Combine(ExportDir, "icons.json"), ToJson(map.ToDictionary(k => k.Key.ToString(), k => k.Value)));
            Log("exported " + saved + " item icons for " + map.Count + " items");
        }

        /// <summary>A game texture (not readable as it is) copied to a PNG through a render texture.</summary>
        static bool SaveIcon(string resource, string file)
        {
            var src = Resources.Load<Texture>(resource);
            if (src == null) return false;
            int w = Mathf.Clamp(src.width, 8, 128), h = Mathf.Clamp(src.height, 8, 128);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var was = RenderTexture.active;
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(file, tex.EncodeToPNG());
                return true;
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tex);
            }
        }

        List<ItemStack> Stacks(ItemDropRateGroupEx g)
        {
            return g.idSets.Select(s => new ItemStack { id = s.id, count = s.stackMax, name = NameOf(s.id) }).ToList();
        }

        System.Collections.IEnumerator ExportItems(ItemLibraryBase lib)
        {
            var fresh = new ItemStats();
            var all = new SortedDictionary<int, ItemOverride>();
            var hasData = new HashSet<int>(Item.getItemXMLIDList());   // skip ids with no item file (skills etc.)
            int n = 0;
            foreach (var kv in lib.itemIDList.ToList())
            {
                var e = kv.Value;
                if (e == null || !hasData.Contains(kv.Key)) continue;
                // The game's own values (packs may already have changed these)
                KeyValuePair<int, float> orig;
                var o = origPrices.TryGetValue(kv.Key, out orig) ? new ItemOverride { price = orig.Key, buyPriceAmp = orig.Value }
                                                                : new ItemOverride { price = e.price, buyPriceAmp = e.buyPriceAmp };
                ItemStats st = null;
                exporting = true;
                try { st = Item.LoadFromResource(kv.Key, true); } catch { }
                finally { exporting = false; }
                if (st != null)
                {
                    o.name = st.dispName;
                    if (!nameCache.ContainsKey(kv.Key)) nameCache[kv.Key] = st.dispName;
                    o.stats = new Dictionary<string, object>();
                    foreach (var f in statFields.Values)
                    {
                        var v = f.GetValue(st);
                        if (v == null || v is UnityEngine.Object || !(v is ValueType || v is string)) continue;
                        if (Equals(v, f.GetValue(fresh))) continue;   // only what differs from a blank item
                        o.stats[f.Name] = v is Enum ? v.ToString() : v;
                    }
                }
                else o.name = NameOf(kv.Key);
                all[kv.Key] = o;
                if (++n % 25 == 0) yield return null;
            }
            var outp = all.ToDictionary(k => k.Key.ToString(), k => k.Value);
            File.WriteAllText(Path.Combine(ExportDir, "items.json"), ToJson(outp));
            Log("exported " + outp.Count + " items to items.json");
        }

        // Variants ("Fine Knife") and their base item, so tools can group them and name them.
        System.Collections.IEnumerator ExportVariants(ItemLibraryBase lib)
        {
            var v = new SortedDictionary<int, Dictionary<string, object>>();
            int n = 0;
            foreach (var kv in lib.itemIDList.ToList())
            {
                var e = kv.Value;
                if (e == null || e.baseItemID <= 0 || e.baseItemID == kv.Key) continue;
                v[kv.Key] = new Dictionary<string, object> { { "base", e.baseItemID }, { "name", NameOf(kv.Key) } };
                if (++n % 200 == 0) yield return null;
            }
            File.WriteAllText(Path.Combine(ExportDir, "variants.json"), ToJson(v.ToDictionary(k => k.Key.ToString(), k => k.Value)));
            Log("exported " + v.Count + " item variants");
        }

        // Monsters and their drop ids, gathered as zones are visited.
        public override void OnZoneLoaded(string zone) { scanAt = Time.realtimeSinceStartup + 8f; }

        // Export once, a few seconds into the world.
        public override void OnPlayerSpawned() { if (!exported) exportAt = Time.realtimeSinceStartup + 5f; }

        public override void OnUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (exportAt > 0 && now >= exportAt)
            {
                exportAt = -1; exported = true;
                exportJob = ExportAll();
            }
            if (exportJob != null)
            {
                // At most ~8 ms of export work per frame.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (sw.ElapsedMilliseconds < 8)
                        if (!exportJob.MoveNext()) { exportJob = null; break; }
                }
                catch (Exception e) { Log("export failed: " + e.Message); exportJob = null; }
            }
            try { Monsters.Update(ToJson); } catch (Exception e) { Log("monster picture: " + e.Message); }
            if (scanAt < 0 || now < scanAt) return;
            scanAt = -1;
            Try("monster scan", ScanMonsters);
        }

        // Every monster kind here: id, stats, where it's seen, a picture (see Monsters.cs). Again every minute.
        void ScanMonsters()
        {
            scanAt = Time.realtimeSinceStartup + 60f;
            if (!Game.InWorld) return;
            Monsters.Scan(ToJson);
        }
    }

    // ------------------------------------------------------------ patches
    [HarmonyPatch(typeof(ItemLibraryBase), nameof(ItemLibraryBase.Awake))]
    static class AfterItemLibrary
    {
        static void Postfix(ItemLibraryBase __instance)
        {
            try { if (ContentLoader.Instance != null) ContentLoader.Instance.OnLibraryReady(__instance); }
            catch (Exception e) { ModLoader.Log("ContentLoader: applying item packs failed: " + e); }   // (never breaks the game's item library)
        }
    }

    [HarmonyPatch(typeof(Universe), nameof(Universe.GetShopItemList))]
    static class AfterShopList
    {
        static ShopItemList seen;
        static void Postfix(ShopItemList __result)
        {
            if (__result == null || ReferenceEquals(__result, seen)) return;
            seen = __result;
            try { if (ContentLoader.Instance != null) ContentLoader.Instance.OnShopListCreated(__result); }
            catch (Exception e) { ModLoader.Log("ContentLoader: applying loot/shop packs failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.LoadFromResource), new[] { typeof(int), typeof(Item_LoadFromResource_ResultSet), typeof(bool) })]
    static class AfterItemStats
    {
        static void Postfix(int id_, Item_LoadFromResource_ResultSet __result)
        {
            try { if (__result != null && ContentLoader.Items.Count > 0 && !ContentLoader.exporting) ContentLoader.ApplyStats(id_, __result.statsResult); }
            catch (Exception e) { ModLoader.Log("ContentLoader: item " + id_ + " stats: " + e.Message); }
        }
    }
}
