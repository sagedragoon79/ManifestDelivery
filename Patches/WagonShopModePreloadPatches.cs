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
    ///   2. Postfix sweep: force-registers any orphaned TransportWagons that
    ///      point to this shop via their own `wagonShop` reference but got
    ///      skipped by vanilla's registration loop. This catches wagons whose
    ///      paired wainwright wasn't in workersRO at load time.
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
        /// After vanilla registration: sweep the world for TransportWagons
        /// that point to this shop but aren't in registeredWagons. These are
        /// orphans whose paired wainwright didn't make it into workersRO
        /// (save-load cap issue). Add them directly via reflection.
        /// </summary>
        public static void Postfix(WagonShop __instance)
        {
            try
            {
                var field = typeof(WagonShop).GetField("registeredWagons", AllInstance);
                var list = field?.GetValue(__instance) as List<TransportWagon>;
                if (list == null) return;

                int before = list.Count;
                int added = 0;
                int adopted = 0;
                int totalWagons = 0;
                int unownedWagons = 0;
                int otherShopWagons = 0;

                var allWagons = Object.FindObjectsOfType<TransportWagon>();
                var orphans = new List<TransportWagon>();

                foreach (var wagon in allWagons)
                {
                    if (wagon == null) continue;
                    totalWagons++;

                    if (wagon.wagonShop == null)
                    {
                        unownedWagons++;
                        orphans.Add(wagon);
                        continue;
                    }
                    if (wagon.wagonShop != __instance) { otherShopWagons++; continue; }
                    if (list.Contains(wagon)) continue;

                    list.Add(wagon);
                    added++;
                }

                // ─── Distance-based orphan adoption ───────────────────────────
                // If this shop has room under its cap, claim the nearest
                // unowned wagons. Camp/Hub cap comes from maxWorkers (which
                // our Prefix raised to the saved-mode cap).
                var maxField = FindBackingField(__instance.GetType(), "maxWorkers");
                int cap = maxField != null ? (int)maxField.GetValue(__instance) : 2;
                int gap = cap - list.Count;

                if (gap > 0 && orphans.Count > 0)
                {
                    Vector3 shopPos = __instance.transform.position;
                    orphans.Sort((a, b) =>
                        (a.transform.position - shopPos).sqrMagnitude
                            .CompareTo(
                        (b.transform.position - shopPos).sqrMagnitude));

                    // Access the protected assignedWagonsByWorker dict so we
                    // can also register the worker→wagon link. Without this,
                    // wainwrights keep building new wagons because the game's
                    // "does this worker have a wagon?" lookup misses our
                    // orphans.
                    var assignedField = typeof(WagonShop).GetField(
                        "assignedWagonsByWorker", AllInstance);
                    var assignedDict = assignedField?.GetValue(__instance)
                        as System.Collections.IDictionary;

                    int toAdopt = System.Math.Min(gap, orphans.Count);
                    for (int i = 0; i < toAdopt; i++)
                    {
                        var orphan = orphans[i];
                        try
                        {
                            orphan.AssignedToWagonShop(__instance);
                            list.Add(orphan);

                            // Match to first unassigned wainwright
                            if (assignedDict != null)
                            {
                                foreach (var worker in __instance.workersRO)
                                {
                                    if (worker == null) continue;
                                    if (assignedDict.Contains(worker)) continue;
                                    assignedDict.Add(worker, orphan);
                                    break;
                                }
                            }

                            adopted++;
                        }
                        catch (System.Exception ex)
                        {
                            ManifestDeliveryMod.Log.Warning(
                                $"[MD] Orphan adoption failed for {orphan?.name}: {ex.Message}");
                        }
                    }
                }

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Finalize Postfix survey for {__instance.gameObject.name}: " +
                    $"worldWagons={totalWagons}  unowned={unownedWagons}  " +
                    $"otherShops={otherShopWagons}  repaired={added}  adopted={adopted}  " +
                    $"finalList={list.Count}  cap={cap}");

                if (added + adopted > 0)
                {
                    // Fire the count-changed callback so UI updates
                    var cbField = typeof(WagonShop).GetField(
                        "onRegisteredWagonCountChanged", AllInstance);
                    var cb = cbField?.GetValue(__instance) as System.Action;
                    cb?.Invoke();

                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] Finalize Postfix: repaired {added} orphaned wagon(s) " +
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
