using HarmonyLib;
using UnityEngine;
using WagonShopsEnhanced;
using WagonShopsEnhanced.Components;

namespace WagonShopsEnhanced.Patches
{
    /// <summary>
    /// All Harmony patches that target WagonShop.
    ///
    /// Patch summary
    /// ──────────────────────────────────────────────────────────────────────────
    ///  Start_Postfix                        — attaches WagonShopEnhancement to
    ///                                         every WagonShop at startup.
    ///  HasWagonAssignedForAllWorkers_Postfix — replaces the vanilla 1-wagon-per-
    ///                                         worker ceiling with the mode-aware
    ///                                         MaxWagons cap so that:
    ///                                           Standard/Camp shops cap at 2
    ///                                           Hub shops cap at 4  (configurable)
    ///                                         The cap is still bounded by the
    ///                                         worker count — wagons need drivers.
    /// </summary>
    [HarmonyPatch]
    internal static class WagonShopPatches
    {
        // ── 1. Attach WagonShopEnhancement on Start ───────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WagonShop), "Start")]
        private static void Start_Postfix(WagonShop __instance)
        {
            if (__instance.GetComponent<WagonShopEnhancement>() == null)
                __instance.gameObject.AddComponent<WagonShopEnhancement>();
        }

        // ── 2. Cap wagon production at MaxWagons ──────────────────────────────
        //
        //  Vanilla formula (HasWagonAssignedForAllWorkers):
        //    numWagonSpaceAvailable = workersRO.Count - assignedWagonsByWorker.Count
        //
        //  Our replacement formula:
        //    effectiveCap           = min(MaxWagons, workersRO.Count)
        //    numWagonSpaceAvailable = effectiveCap - assignedWagonsByWorker.Count
        //
        //  We use a postfix so vanilla runs first (including its internal call to
        //  TryToFindWagonForAllWorkers which may auto-assign wagons).  The final
        //  vanilla value of numWagonSpaceAvailable lets us back-calculate how many
        //  workers already have a wagon without accessing the private dictionary.
        //
        //  Effect by mode (defaults):
        //    Standard  → max 2 wagons  even when 3+ workers are assigned
        //    Camp      → max 2 wagons
        //    Hub       → max 4 wagons  (needs 4 workers to fully utilise)
        //
        //  Note: CreateWagon (called by OnWorkOrderCompleted) assigns the new wagon
        //  to the first worker that has no wagon.  If all workers already have one,
        //  the wagon is registered but unassigned — that can't happen here because
        //  effectiveCap is always ≤ workersRO.Count.

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WagonShop), "HasWagonAssignedForAllWorkers")]
        private static void HasWagonAssignedForAllWorkers_Postfix(
            WagonShop __instance,
            ref bool  __result,
            ref int   numWagonSpaceAvailable)
        {
            WagonShopEnhancement? enhancement = __instance.GetComponent<WagonShopEnhancement>();
            if (enhancement == null) return;

            // Back-calculate assigned count from vanilla's final output.
            // vanilla: numWagonSpaceAvailable = workersRO.Count - assignedWagonsByWorker.Count
            // → assignedCount = workersRO.Count - numWagonSpaceAvailable
            int workerCount   = __instance.workersRO.Count;
            int assignedCount = workerCount - numWagonSpaceAvailable;

            // Effective ceiling: our mode cap, but never above the worker count
            // (a wagon without a driver would sit idle permanently).
            int effectiveCap = Mathf.Min(enhancement.MaxWagons, workerCount);
            int cappedSpace  = effectiveCap - assignedCount;

            // Nothing to do if the cap is the same as vanilla.
            if (cappedSpace == numWagonSpaceAvailable) return;

            numWagonSpaceAvailable = Mathf.Max(0, cappedSpace);
            __result               = numWagonSpaceAvailable <= 0;

            if (cappedSpace < numWagonSpaceAvailable)
            {
                WagonShopsEnhancedMod.Log.Msg(
                    $"[WSE] {__instance.name} wagon cap: {enhancement.MaxWagons} " +
                    $"(mode={enhancement.ModeDisplayName}, workers={workerCount}, " +
                    $"assigned={assignedCount}, space={numWagonSpaceAvailable})");
            }
        }
    }
}
