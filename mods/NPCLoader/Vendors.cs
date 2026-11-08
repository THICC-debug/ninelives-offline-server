using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NineLivesModLoader;
using UnityEngine;

namespace NPCLoader
{
    /// <summary>One thing a vendor sells (an NPC's "sells" list).</summary>
    public class SellDef
    {
        /// <summary>Item id.</summary>
        public int id;
        /// <summary>Gold for one. 0 (or left out) = the game's usual price (base price × shop markup).</summary>
        public int price;
        /// <summary>How many the vendor has (each one is a shop slot). 1 if left out.</summary>
        public int stock = 1;
    }

    /// <summary>
    /// Vendors with their own list: the shop window shows exactly that list, at those prices.
    /// The game works out a shop price as base price × markup × the shop's own factor in three places
    /// (the shop window, the tooltip and the purchase). For a vendor with a list, each item's slot gets its
    /// final price per item and those factors count as 1 while that vendor's items are priced, so all
    /// three agree. The bought item gets its normal base price back (so selling it later pays as usual).
    /// </summary>
    static class Vendors
    {
        public const int MaxSlots = 54;   // GUI_Shop.slotCount
        static readonly Dictionary<int, List<SellDef>> lists = new Dictionary<int, List<SellDef>>();
        static readonly Dictionary<GUI_ActionButton, int> basePrice = new Dictionary<GUI_ActionButton, int>();   // priced slot -> its normal price
        static bool told;

        public static void Clear()
        {
            var shop = GUI_Shop.getGUIInstance();
            if (shop != null && lists.ContainsKey(shop.cid)) Restock(shop);
            lists.Clear();
        }

        /// <summary>Set (or clear) one NPC's list. An empty list means: the shop works the usual way.</summary>
        public static void Set(int cid, List<SellDef> sells)
        {
            var l = sells == null ? new List<SellDef>() : sells.Where(s => s != null && s.id > 0).ToList();
            var shop = GUI_Shop.getGUIInstance();
            if (shop != null && shop.cid == cid && (l.Count > 0 || lists.ContainsKey(cid))) Restock(shop);   // its window shows the old list
            if (l.Count == 0) lists.Remove(cid); else lists[cid] = l;
        }

        /// <summary>The next time this shop opens, it's stocked again (with the list as it is then).</summary>
        static void Restock(GUI_Shop shop)
        {
            try { Traverse.Create(shop).Field("isGoodsEmpty").SetValue(true); Traverse.Create(shop).Field("nextMakeGoodsTimeMin").SetValue(0f); } catch { }
            if (shop.Opened) shop.doClose();
        }

        static bool Priced(GUI_Shop shop) { return shop != null && lists.ContainsKey(shop.cid); }

        /// <summary>The stock: every item on the list, as many times as it's in stock.</summary>
        static bool Stock(ItemBase list, int cid, List<InitItemDataSet> result)
        {
            if (!(list is ShopItemList)) return false;
            List<SellDef> l;
            if (!lists.TryGetValue(cid, out l)) return false;
            var lib = ItemLibraryBase.GetItemLibraryCtrl();
            result.Clear();
            foreach (var s in l)
            {
                if (lib != null && !lib.itemIDList.ContainsKey(s.id))
                {
                    if (!told) { told = true; ModLoader.Log("NPCLoader: vendor " + cid + ": there's no item " + s.id + " in the game (left out)"); }
                    continue;
                }
                for (int i = 0; i < Math.Max(1, s.stock) && result.Count < MaxSlots; i++)
                    result.Add(new InitItemDataSet { id = s.id, randomCode = Mathfx.fullLongRandom(), count = 1 });
            }
            return true;
        }

        /// <summary>A slot was made in a priced vendor's window: give it its price per item.</summary>
        static void PriceSlot(GUI_ActionDock dock, int index, GUIActionButtonControllerAndParams made)
        {
            var shop = GUI_Shop.getGUIInstance();
            if (made == null || made.actionButtonCtrl == null || shop == null || dock != shop.actionDockCtrl) return;
            List<SellDef> l;
            if (!lists.TryGetValue(shop.cid, out l)) return;
            var b = made.actionButtonCtrl;
            var s = l.FirstOrDefault(x => x.id == made.id);
            float amp = 20f;
            var lib = ItemLibraryBase.GetItemLibraryCtrl();
            if (lib != null && lib.itemIDList.ContainsKey(made.id) && lib.itemIDList[made.id] != null) amp = lib.itemIDList[made.id].buyPriceAmp;
            int each = s != null && s.price > 0 ? s.price : Mathf.Max(1, Mathf.RoundToInt(b.price * amp * shop.priceAmp));
            basePrice[b] = b.price;
            b.price = each;
            if (basePrice.Count > 400) foreach (var k in basePrice.Keys.Where(k => k == null).ToList()) basePrice.Remove(k);   // (slots of closed shops)
        }

        // While a priced slot is shown, looked at or bought: markup and shop factor are 1.
        public struct Saved { public bool on; public float amp, shopAmp; }
        static Saved Neutral(GUI_Shop shop)
        {
            var s = new Saved { on = true, amp = GUI_ActionButton.buyPriceAmp, shopAmp = shop.priceAmp };
            GUI_ActionButton.buyPriceAmp = 1f; shop.priceAmp = 1f;
            return s;
        }
        static void Restore(GUI_Shop shop, Saved s)
        {
            if (!s.on) return;
            GUI_ActionButton.buyPriceAmp = s.amp;
            if (shop != null) shop.priceAmp = s.shopAmp;
        }

        /// <summary>
        /// The live link's /shopdebug: ?open=&lt;npc id&gt; opens that NPC's shop (nothing is bought); then
        /// what the shop window holds and the price it charges for each slot.
        /// </summary>
        public static object Debug(int open)
        {
            var shop = GUI_Shop.getGUIInstance();
            if (shop == null) return new Dictionary<string, object> { { "error", "no shop window yet" } };
            if (open > 0)
            {
                CommonStats who = null;
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(CommonStats)))
                {
                    var c = (CommonStats)o;
                    if (c != null && c.gameObject.scene.IsValid() && c.getCID() == open) { who = c; break; }
                }
                if (who == null) return new Dictionary<string, object> { { "error", "no NPC " + open + " here" } };
                shop.doOpen(who);
                return new Dictionary<string, object> { { "opening", open } };
            }
            var slots = new List<object>();
            var dock = shop.actionDockCtrl;
            bool priced = Priced(shop);
            if (dock != null && dock.dockedActionButtons != null)
                foreach (var d in dock.dockedActionButtons)
                {
                    if (d == null || d.actionButtonCtrl == null) continue;
                    var b = d.actionButtonCtrl;
                    var st = priced ? Neutral(shop) : default(Saved);
                    long charge;
                    try { charge = GUI_ActionButton.mathItemPrice(dock, b.price, b.stackCount, GUI_ActionButton.buyPriceAmp, shop.priceAmp); }
                    finally { Restore(shop, st); }
                    int bp;
                    slots.Add(new Dictionary<string, object> { { "id", d.id }, { "count", b.stackCount }, { "charge", charge },
                        { "normalPrice", basePrice.TryGetValue(b, out bp) ? bp : b.price } });
                }
            var patched = new List<string>();
            foreach (var m in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(m);
                if (info != null && info.Owners.Contains("ninelives.npcloader")) patched.Add(m.DeclaringType.Name + "." + m.Name);
            }
            return new Dictionary<string, object> { { "cid", shop.cid }, { "opened", shop.Opened }, { "ownList", priced }, { "slots", slots }, { "patched", patched } };
        }

        // ------------------------------------------------------------ patches
        [HarmonyPatch(typeof(ItemBase), nameof(ItemBase.getRandomItemIDs), new[] { typeof(ItemLibrary), typeof(ItemBase), typeof(int), typeof(List<InitItemDataSet>) })]
        static class StockPatch
        {
            static bool Prefix(ItemBase dropList_, int id_, List<InitItemDataSet> result_)
            {
                try
                {
                    if (Stock(dropList_, id_, result_)) return false;
                    // A shop with nothing set up (a new vendor): an empty shop, not an error that leaves the window stuck.
                    if (dropList_ is ShopItemList && dropList_.dropLists != null && !dropList_.dropLists.ContainsKey(id_)) { result_.Clear(); return false; }
                    return true;
                }
                catch (Exception e) { ModLoader.Log("NPCLoader: vendor stock failed: " + e.Message); return true; }
            }
        }

        [HarmonyPatch(typeof(GUI_ActionButtonTypes), nameof(GUI_ActionButtonTypes.MakeActionButton), new[] { typeof(GUI_ActionDock), typeof(int), typeof(Transform) })]
        static class SlotPatch
        {
            static void Postfix(GUI_ActionDock dock_, int dockNum, GUIActionButtonControllerAndParams __result)
            {
                try { PriceSlot(dock_, dockNum, __result); } catch (Exception e) { ModLoader.Log("NPCLoader: vendor price failed: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(GUI_Shop), "drawDockBG")]
        static class WindowPatch
        {
            static void Prefix(GUI_Shop __instance, GUI_ActionDock docks_, out Saved __state)
            {
                __state = default(Saved);
                if (docks_ == __instance.actionDockCtrl && Priced(__instance)) __state = Neutral(__instance);
            }
            static void Postfix(GUI_Shop __instance, Saved __state) { Restore(__instance, __state); }
        }

        [HarmonyPatch(typeof(GUI_ActionButton), "shopItemBuy")]
        static class BuyPatch
        {
            static void Prefix(GUI_ActionButton __instance, out Saved __state)
            {
                __state = default(Saved);
                var shop = GUI_Shop.getGUIInstance();
                if (shop != null && basePrice.ContainsKey(__instance)) __state = Neutral(shop);
            }
            static void Postfix(GUI_ActionButton __instance, bool __result, Saved __state)
            {
                Restore(GUI_Shop.getGUIInstance(), __state);
                int p;
                if (__result && basePrice.TryGetValue(__instance, out p)) { __instance.price = p; basePrice.Remove(__instance); }   // in the bag now: worth what it's normally worth
            }
        }

        [HarmonyPatch(typeof(GUI_Tooltips), "drawPrice")]
        static class TooltipPatch
        {
            static void Prefix(GUI_Tooltips __instance, out Saved __state)
            {
                __state = default(Saved);
                var t = __instance.tooltipsTarget;
                var b = t != null ? t.GetComponent<GUI_ActionButton>() : null;
                var shop = GUI_Shop.getGUIInstance();
                if (b != null && shop != null && basePrice.ContainsKey(b)) __state = Neutral(shop);
            }
            static void Postfix(Saved __state) { Restore(GUI_Shop.getGUIInstance(), __state); }
        }
    }
}
