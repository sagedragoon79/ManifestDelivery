using HarmonyLib;
using System.Reflection;
using WagonShopsEnhanced;

namespace WagonShopsEnhanced.Patches
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
            int targetCapacity = WagonShopsEnhancedMod.StorageCartCapacity.Value;
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
                    WagonShopsEnhancedMod.Log.Msg(
                        $"[WSE] Storage Cart '{__instance.name}' capacity: {current} → {targetCapacity}");
                }
            }
            else
            {
                WagonShopsEnhancedMod.Log.Warning(
                    "[WSE] Could not find _storageItemCountCapacity on StorageBuilding.");
            }
        }
    }
}
