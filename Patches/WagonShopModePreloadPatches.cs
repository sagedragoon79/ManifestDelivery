using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Repairs WagonShop state on save-load:
    ///   1. Raises maxWorkers to the saved-mode cap (Hub=4) BEFORE the
    ///      OnGameFinishedLoadingFinalize wagon-registration loop runs, so
    ///      vanilla has room to re-register all saved wagons.
    ///   2. Postfix: re-registers TransportWagons that point to this shop via
    ///      their own `wagonShop` reference but got skipped by vanilla's
    ///      registration loop (their paired wainwright wasn't in workersRO at
    ///      load time). It does NOT adopt unowned wagons — see Postfix.
    ///
    /// Why this hook (not Awake): at Awake time, transform.position is still
    /// the prefab origin (500,0,500) — the save system sets position later.
    /// By OnGameFinishedLoadingFinalize, positions are correct AND we're
    /// inserted right at the wagon-registration moment.
    /// </summary>
    internal static class WagonShopAwakePrefix
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                var finalize = typeof(WagonShop).GetMethod(
                    "OnGameFinishedLoadingFinalize", AllInstance);
                if (finalize == null)
                {
                    ManifestDeliveryMod.Log.Warning(
                        "[MD] ModePreload: OnGameFinishedLoadingFinalize not found.");
                    return;
                }

                var prefix = typeof(WagonShopAwakePrefix).GetMethod(
                    nameof(Prefix), BindingFlags.Static | BindingFlags.Public);
                var postfix = typeof(WagonShopAwakePrefix).GetMethod(
                    nameof(Postfix), BindingFlags.Static | BindingFlags.Public);

                harmony.Patch(finalize,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));

                ManifestDeliveryMod.Log.Msg(
                    "[MD] ModePreload: Patched WagonShop.OnGameFinishedLoadingFinalize");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ModePreload register failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Before the wagon-registration loop: raise maxWorkers cap so
        /// vanilla's condition check (workersRO.Contains(wainwright)) has
        /// a chance to succeed for all saved wainwrights.
        /// </summary>
        public static void Prefix(WagonShop __instance)
        {
            try
            {
                Vector3 pos = __instance.transform.position;
                ShopMode? savedMode = WagonShopEnhancement.GetSavedModeForPosition(pos);
                if (savedMode == null)
                {
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] Finalize Prefix: {__instance.gameObject.name} " +
                        $"pos=({pos.x:F1},{pos.z:F1}) savedMode=none (skipping)");
                    return;
                }

                int targetMax = WagonShopEnhancement.GetMaxWagonsForMode(savedMode.Value);
                var maxField = FindBackingField(__instance.GetType(), "maxWorkers");
                if (maxField != null)
                {
                    int current = (int)maxField.GetValue(__instance);
                    if (current < targetMax)
                    {
                        maxField.SetValue(__instance, targetMax);
                        ManifestDeliveryMod.Log.Msg(
                            $"[MD] Finalize Prefix raised maxWorkers {current} → {targetMax} " +
                            $"for {__instance.gameObject.name} ({savedMode})");
                    }
                }

                if (__instance.userDefinedMaxWorkers < targetMax)
                    __instance.userDefinedMaxWorkers = targetMax;
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] Finalize Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// After vanilla registration: re-register wagons that point to this
        /// shop but aren't in registeredWagons.
        ///
        /// This used to also ADOPT the nearest "unowned" wagons up to the cap.
        /// Removed 2026-09-28: each shop finalizes separately and
        /// TransportWagon.Load doesn't restore wagonShop, so when the first shop
        /// finalized, every other shop's wagons still looked unowned — it took
        /// them (log: "unowned=30 … adopted=5"), and adopted wagons could end
        /// up with no driver. Vanilla already hands unowned wagons to workers
        /// who lack one (WagonShop.WagonValidForWorkerAssignment).
        /// </summary>
        public static void Postfix(WagonShop __instance)
        {
            try
            {
                var field = typeof(WagonShop).GetField("registeredWagons", AllInstance);
                var list = field?.GetValue(__instance) as List<TransportWagon>;
                if (list == null) return;

                // The game's own wagon list — no FindObjectsOfType scene scan.
                var wagons = UnitySingleton<GameManager>.Instance?.resourceManager?.transportWagonsRO;
                if (wagons == null) return;

                int before = list.Count;
                int added = 0;
                foreach (var wagon in wagons)
                {
                    if (wagon == null || wagon.wagonShop != __instance) continue;
                    if (list.Contains(wagon)) continue;
                    list.Add(wagon);
                    added++;
                }

                if (added > 0)
                {
                    // Fire the count-changed callback so UI updates
                    var cbField = typeof(WagonShop).GetField(
                        "onRegisteredWagonCountChanged", AllInstance);
                    var cb = cbField?.GetValue(__instance) as System.Action;
                    cb?.Invoke();

                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] Finalize Postfix: re-registered {added} wagon(s) " +
                        $"for {__instance.gameObject.name} (count: {before} → {list.Count})");
                }
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] Finalize Postfix error: {ex.Message}");
            }
        }

        private static FieldInfo FindBackingField(System.Type startType, string propertyName)
        {
            string backingName = $"<{propertyName}>k__BackingField";
            System.Type t = startType;
            while (t != null)
            {
                var field = t.GetField(backingName, AllInstance);
                if (field != null) return field;
                t = t.BaseType;
            }
            return null;
        }
    }
}
