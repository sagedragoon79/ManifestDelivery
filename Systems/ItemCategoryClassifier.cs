using System;
using System.Collections.Generic;

namespace ManifestDelivery.Systems
{
    /// <summary>
    /// Classifies hauled items as RAW (gathered/grown/mined/butchered) or
    /// PRODUCED (crafted in a workshop). Used by the per-shop stats report
    /// to give players a sense of how much basic resource hauling they're
    /// doing vs how much finished-goods movement.
    ///
    /// Edge calls:
    ///   • Firewood → Produced (chopped from logs at the Woodcutter)
    ///   • Iron → Produced (an ingot from the Smelter; IronOre is the raw item)
    ///   • Honey/Wax → Raw (the Apiary harvests them as-is)
    ///   • Hide/Tallow → Raw (butchering byproducts, not crafts)
    ///   • Carcasses → Raw (hunting output before the butcher)
    ///
    /// Names are resolved against ItemID at startup via Enum.TryParse so a
    /// game patch that adds/renames items doesn't crash this code — unknown
    /// names are silently dropped. Tweak the list and rebuild.
    ///
    /// The list uses the game's real ItemID names (checked against the enum
    /// 2026-09-28). An earlier list guessed names like "Wheat" and "Carrot",
    /// so Grain, RootVegetable, Greens, Fruit and others counted as produced.
    /// </summary>
    internal static class ItemCategoryClassifier
    {
        // Materials gathered, grown, mined, butchered, or harvested without
        // a craft step. Anything not here is treated as PRODUCED.
        private static readonly string[] RawItemNames = new[]
        {
            // Mineral / quarry / well
            "Stone", "Coal", "Sand", "Clay", "IronOre", "GoldOre", "Water",

            // Forestry
            "Logs", "Willow",

            // Crops (raw produce — Mill/Bakery etc. turn them into produced goods)
            "Grain", "RootVegetable", "Beans", "Greens", "Fruit", "Flax", "Hay", "Clover",

            // Foraged / apiary
            "Berries", "Mushroom", "Roots", "Nuts", "Herbs", "Honey", "Wax",

            // Animal (raw — Smokehouse/Tannery/Butcher process these)
            "Meat", "Fish", "Hide", "Tallow", "Eggs", "Milk",
            "Carcass", "BoarCarcass", "SmallCarcass", "WolfCarcass",
            "HealthyCarcass", "UnhealthyCarcass", "SicklyCarcass",

            // Fertilizer inputs
            "Poop", "Compost",

            // Mod items, resolved when the mod that adds them is installed
            // (LiveStockMarket's Wool); dropped silently otherwise.
            "Wool",
        };

        // Resolved at first use via reflection on FF's ItemID enum.
        private static HashSet<int>? _rawItemIds;

        public static bool IsRawMaterial(int itemId)
        {
            EnsureLoaded();
            return _rawItemIds!.Contains(itemId);
        }

        public static bool IsRawMaterial(ItemID itemId) => IsRawMaterial((int)itemId);

        private static void EnsureLoaded()
        {
            if (_rawItemIds != null) return;
            _rawItemIds = new HashSet<int>();
            int matched = 0, missed = 0;
            foreach (var name in RawItemNames)
            {
                if (Enum.TryParse<ItemID>(name, ignoreCase: false, out var id))
                {
                    _rawItemIds.Add((int)id);
                    matched++;
                }
                else
                {
                    missed++;
                }
            }
            ManifestDeliveryMod.Log.Msg(
                $"[MD][Stats] Item classifier loaded: {matched} raw items resolved, " +
                $"{missed} names not in this game version's ItemID enum.");
        }
    }
}
