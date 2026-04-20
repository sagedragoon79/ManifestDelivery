using HarmonyLib;
using System.Reflection;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
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
    ///                                         MaxWagons cap.
    ///  SetMeshFromPrefab_Postfix            — shows Camp + Hub radius circles
    ///                                         during placement preview.
    /// </summary>
    [HarmonyPatch]
    internal static class WagonShopPatches
    {
        // ── Placement preview: show Camp + Hub radius circles ─────────────

        /// <summary>
        /// True while a WagonShop is being placed. Used by WagonShopEnhancement
        /// to keep existing shop circles visible during placement so the player
        /// can see existing coverage.
        /// </summary>
        /// <summary>
        /// True while a WagonShop is being placed. Existing shop circles
        /// stay visible during placement so the player can see coverage overlap.
        /// </summary>
        public static bool IsPlacingWagonShop { get; set; } = false;

        // Standalone preview circles — NOT parented to the placeable.
        // Parented children caused sticky placement because their
        // SelectionCircle.OnDestroy events confused the game's exit logic.
        private static GameObject _previewHub = null;
        private static GameObject _previewCamp = null;
        private static PlaceableBuilding _trackedPlaceable = null;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlaceableBuilding), "SetMeshFromPrefab")]
        private static void SetMeshFromPrefab_Postfix(PlaceableBuilding __instance, GameObject prefab)
        {
            try
            {
                // Clean up any prior preview circles
                CleanupPreviewCircles();

                // Only applies to WagonShop placement
                if (prefab.GetComponent<WagonShop>() == null)
                {
                    IsPlacingWagonShop = false;
                    return;
                }

                IsPlacingWagonShop = true;
                _trackedPlaceable = __instance;

                float campRadius = ManifestDeliveryMod.CampWorkRadius.Value;
                float hubRadius  = ManifestDeliveryMod.HubWorkRadius.Value;

                // Create standalone circles (NOT parented to placeable)
                _previewHub = new GameObject("MD_HubRadiusPreview");
                _previewHub.transform.position = __instance.transform.position;
                var hubCircle = _previewHub.AddComponent<SelectionCircle>();
                hubCircle.Init(__instance.transform.position, hubRadius);
                hubCircle.SetEnabled(true);

                _previewCamp = new GameObject("MD_CampRadiusPreview");
                _previewCamp.transform.position = __instance.transform.position;
                var campCircle = _previewCamp.AddComponent<SelectionCircle>();
                campCircle.Init(__instance.transform.position, campRadius);
                campCircle.SetEnabled(true);

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Placement preview: showing Camp ({campRadius:F0}u) + Hub ({hubRadius:F0}u) radius circles");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] SetMeshFromPrefab_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync preview circle positions with the moving cursor ghost.
        /// Also detects when placement ends (placeable destroyed) and cleans up.
        /// Called from WagonShopEnhancement.Update on any existing shop.
        /// </summary>
        public static void UpdatePreviewCircles()
        {
            if (!IsPlacingWagonShop) return;

            // If the placeable was destroyed (confirmed or cancelled), clean up
            if (_trackedPlaceable == null || !_trackedPlaceable)
            {
                CleanupPreviewCircles();
                IsPlacingWagonShop = false;
                return;
            }

            // Sync positions
            Vector3 pos = _trackedPlaceable.transform.position;
            if (_previewHub != null) _previewHub.transform.position = pos;
            if (_previewCamp != null) _previewCamp.transform.position = pos;
        }

        private static void CleanupPreviewCircles()
        {
            if (_previewHub != null) { GameObject.Destroy(_previewHub); _previewHub = null; }
            if (_previewCamp != null) { GameObject.Destroy(_previewCamp); _previewCamp = null; }
            _trackedPlaceable = null;
        }

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
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] {__instance.name} wagon cap: {enhancement.MaxWagons} " +
                    $"(mode={enhancement.ModeDisplayName}, workers={workerCount}, " +
                    $"assigned={assignedCount}, space={numWagonSpaceAvailable})");
            }
        }
    }
}
