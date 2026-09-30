using System;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;
using ManifestDelivery.Systems;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Keeps MD's per-building settings — a Wagon Shop's mode and a storage's
    /// priorities — when you relocate the building.
    ///
    /// The game relocates by taking the old building down and constructing a
    /// NEW one at the destination (BuildManager.Relocate: a deconstruction site
    /// at the old spot and a RelocationDestination at the new one; the old
    /// instance is destroyed). MD saves both settings by position, so the new
    /// building found nothing saved at its spot and came up Standard /
    /// priority 5.
    ///
    /// The prefix runs when you confirm the move, while the old building still
    /// exists, and writes its settings under the destination's key. The new
    /// building restores them when it's built — later this session, or after a
    /// reload mid-move. Upgrades need nothing: they rebuild on the same spot.
    /// Registered manually so a renamed game method disables only this hook.
    /// </summary>
    internal static class RelocationPatches
    {
        public static void Register(HarmonyLib.Harmony harmony)
        {
            var target = AccessTools.Method(typeof(BuildManager), nameof(BuildManager.Relocate),
                new[] { typeof(ConstructionData), typeof(ConstructionData) });
            if (target == null)
            {
                ManifestDeliveryMod.Log.Warning(
                    "[MD] BuildManager.Relocate not found — relocated shops and storages won't keep MD settings.");
                return;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(RelocationPatches), nameof(Prefix))));
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] Relocation hook failed: {ex.Message}");
            }
        }

        private static void Prefix(ConstructionData deconstructionData, ConstructionData constructionData)
        {
            try
            {
                // Same lookup the game does for the building being moved.
                GameObject building = deconstructionData.sceneObject;
                if (building == null) building = constructionData.sceneObject;
                if (building == null) return;
                Vector3 destination = constructionData.position;

                var shop = building.GetComponent<WagonShopEnhancement>();
                if (shop != null) shop.CarryModeTo(destination);

                if (building.GetComponent<StorageBuilding>() != null)
                    StoragePriorityStore.CarryTo(building, destination);
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] Carrying settings to a relocated building failed: {ex.Message}");
            }
        }
    }
}
