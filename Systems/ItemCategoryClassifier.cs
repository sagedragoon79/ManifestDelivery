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
    ///   • Firewood → Produced (chopped from logs at the Sawpit/Woodcutter)
    ///   • Salt → Raw (mined or evaporated, no workshop step)
    ///   • Honey/Beeswax → Raw (Apiary harvests them as-is)
    ///   • Hide → Raw (a butcher byproduct, not a craft)
    ///   • Cloth → Produced (woven from Wool at the Tailor)
    ///   • Charcoal → Produced (Charcoal Kiln)
    ///
    /// Names are resolved against ItemID at startup via Enum.TryParse so a
    /// game patch that adds/renames items doesn't crash this code — unknown
    /// names are silently dropped. Tweak the list and rebuild.
    /// </summary>
    internal static class ItemCategoryClassifier
    {
        // Materials gathered, grown, mined, butchered, or harvested without
        // a craft step. Anything not here is treated as PRODUCED.
        private static readonly string[] RawItemNames = new[]
        {
            // Mineral / quarry
            "Stone", "Coal", "Sand", "Clay", "IronOre", "GoldOre", "Iron", "Gold",
            "Salt", "Saltpeter", "Sulfur", "Crystal", "Gemstone",

            // Forestry / logs
            "Wood", "Logs", "Log", "RawWood",

            // Crops (raw produce — Mill/Bakery turn them into produced goods)
            "Wheat", "Carrot", "Onion", "Cabbage", "Beans", "Bean",
            "Flax", "Pea", "Peas", "Leek", "Turnip", "Squash", "Pumpkin",
            "Potato", "Potatoes", "Apple", "Apples", "Pear", "Pears",
            "Hops", "Barley", "Rye", "Hay", "Straw",

            // Foraged
            "Berries", "Berry", "Mushrooms", "Mushroom", "Herbs", "Herb",
            "Honey", "Beeswax",

            // Animal byproducts (raw — Smokehouse/Tannery process these)
            "RawMeat", "Meat", "RawFish", "Fish", "RawHide", "Hide", "Pelt",
            "Tallow", "Wool", "Milk", "Eggs", "Egg",

            // Fertilizer / compost (raw inputs to fields)
            "Compost", "Manure", "Fertilizer",
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
