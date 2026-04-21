using HarmonyLib;
using UnityEngine;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Re-enables the hidden SupplyWagon (Storage Cart) toolbar button.
    ///
    /// Crate removed the ability to build Storage Carts in an early patch
    /// by setting the toolbar button's GameObject to SetActive(false) in the
    /// prefab. The prefab itself still exists with a valid SupplyWagon
    /// buildingPrefab reference attached — we just flip the GameObject
    /// active and the full build flow works natively: ghost placement,
    /// materials check, villager construction, finished cart.
    /// </summary>
    [HarmonyPatch(typeof(UIToolbarBuildingButtonManager), "Init")]
    internal static class ReEnableSupplyWagonButton
    {
        private static void Postfix(UIToolbarBuildingButtonManager __instance)
        {
            try
            {
                var buttons = __instance.GetComponentsInChildren<UIToolbarBuildingButton>(
                    includeInactive: true);

                foreach (var btn in buttons)
                {
                    if (btn == null) continue;
                    if (btn.gameObject.name != "SupplyWagon") continue;
                    if (btn.buildingPrefab == null) continue;

                    if (!btn.gameObject.activeSelf)
                    {
                        btn.gameObject.SetActive(true);
                        ManifestDeliveryMod.Log.Msg(
                            "[MD] Re-enabled hidden SupplyWagon build button.");
                    }
                    return;
                }

                ManifestDeliveryMod.Log.Warning(
                    "[MD] SupplyWagon toolbar button not found — build feature unavailable.");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ReEnableSupplyWagonButton failed: {ex.Message}");
            }
        }
    }
}
