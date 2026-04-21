using HarmonyLib;
using System.Reflection;
using ManifestDelivery;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Patches for SupplyWagon (Storage Cart) to make it useful beyond year 1.
    ///
    /// Patch summary
    /// ──────────────────────────────────────────────────────────────────────────
    ///  Start_Postfix — overrides _storageItemCountCapacity from vanilla 750
    ///                  to the configured StorageCartCapacity (default 1500).
    /// </summary>
    [HarmonyPatch]
    internal static class StorageCartPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── 1. Increase Storage Cart capacity on Start ────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SupplyWagon), "Start")]
        private static void Start_Postfix(SupplyWagon __instance)
        {
            int targetCapacity = ManifestDeliveryMod.StorageCartCapacity.Value;
            if (targetCapacity <= 0) return;

            // _storageItemCountCapacity is a SerializeField on StorageBuilding
            var capField = typeof(StorageBuilding).GetField(
                "_storageItemCountCapacity", AllInstance);

            if (capField != null)
            {
                int current = (int)capField.GetValue(__instance);
                if (current != targetCapacity)
                {
                    capField.SetValue(__instance, targetCapacity);
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] Storage Cart '{__instance.name}' capacity: {current} → {targetCapacity}");
                }
            }
            else
            {
                ManifestDeliveryMod.Log.Warning(
                    "[MD] Could not find _storageItemCountCapacity on StorageBuilding.");
            }
        }

        // ── 2. Multiply Storage Cart relocation movement speed ────────────────
        //
        //  Building.movementSpeed is the relocation speed (cart being moved to
        //  a new location). Vanilla's value is painfully slow. Multiply the
        //  property result for SupplyWagons only, so road-bonus math is
        //  preserved but the effective pace is faster.
        //
        //  Applied via get_movementSpeed Postfix rather than field mutation so
        //  we don't stomp on roadBonus recalculation.

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Building), "get_movementSpeed")]
        private static void movementSpeed_Postfix(
            Building     __instance,
            ref float    __result)
        {
            // Only affect Storage Carts (SupplyWagon), not other mobile buildings
            if (!(__instance is SupplyWagon)) return;

            float mult = ManifestDeliveryMod.StorageCartSpeedMult.Value;
            if (mult <= 0f || mult == 1f) return;

            __result *= mult;
        }
    }
}
