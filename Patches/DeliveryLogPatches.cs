using System;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;
using ManifestDelivery.Systems;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Logs every wagon drop-off so we can see what actually got delivered
    /// (not just what got claimed). Pairs with the CLAIM lines emitted by
    /// CampHaul / ReturnTrip — a matching CLAIM + DELIVER pair means the
    /// wagon successfully completed the haul we assigned it.
    ///
    /// Hooks: TransportWagon.ItemBundleDroppedOff(originStorage, dropOffStorage, bundle)
    ///   Fires whenever the wagon unloads a bundle at a destination.
    ///   Before it reports to the game's TallyItemMoved, we log item + count
    ///   + mode context for diagnostics.
    /// </summary>
    [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.ItemBundleDroppedOff))]
    internal static class TransportWagonDropOffPatch
    {
        // Simple dedupe — the game calls ItemBundleDroppedOff twice per drop-off
        // (once from the wagon side, once from the destination side). Suppress
        // the second call when we see the same bundle reference within 200ms.
        private static readonly System.Collections.Generic.Dictionary<int, float>
            _lastLogTime = new System.Collections.Generic.Dictionary<int, float>();

        private static void Postfix(
            TransportWagon __instance,
            ItemStorage originStorage,
            IContainsItems dropOffStorage,
            ItemBundle bundle)
        {
            try
            {
                if (bundle == null) return;

                // Dedupe by bundle reference hash
                int bundleKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(bundle);
                float now = Time.time;
                if (_lastLogTime.TryGetValue(bundleKey, out float last)
                    && now - last < 0.2f)
                    return;
                _lastLogTime[bundleKey] = now;

                string mode = "Standard";
                var data = __instance.GetComponent<WagonEnhancementData>();
                var shop = data?.ResolveShopEnhancement(__instance);
                if (shop != null)
                    mode = shop.Mode.ToString();

                string dest = DescribeContainer(dropOffStorage);
                string origin = DescribeStorage(originStorage);

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] DELIVER ({mode}): {__instance.name} " +
                    $"{bundle.name}×{bundle.numberOfItems} " +
                    $"{origin} → {dest}");

                // ── Stats: record this delivery on the shop's running totals ──
                // Only counts deliveries to a shop with our enhancement (Camp,
                // Hub, or Standard mode set). Vagabond wagons with no shop
                // assignment are skipped — they wouldn't have a stable key
                // anyway, and per-wagon counts are vanilla's job.
                if (shop != null)
                {
                    int itemId = -1;
                    if (Enum.TryParse<ItemID>(bundle.name, ignoreCase: false, out var id))
                        itemId = (int)id;
                    StatsTracker.RecordDelivery(
                        shopPos:   shop.transform.position,
                        shopName:  shop.gameObject.name,
                        modeInt:   (int)shop.Mode,
                        itemId:    itemId,
                        count:     (int)bundle.numberOfItems,
                        gameYear:  GetCurrentGameYear());
                }
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] DeliveryLog postfix error: {ex.Message}");
            }
        }

        // Cached reflection lookup for FF's current-year accessor. We try a
        // few likely shapes (TimeManager singleton with int year property/field).
        // Resolved once, used many times via the _readYear closure.
        private static Func<int>? _readYear;
        private static bool _yearLookupAttempted;

        private static int GetCurrentGameYear()
        {
            if (_readYear != null)
            {
                try { return _readYear(); }
                catch { return 0; }
            }
            if (_yearLookupAttempted) return 0;
            _yearLookupAttempted = true;
            try
            {
                var tmType = AccessTools.TypeByName("TimeManager");
                if (tmType == null)
                {
                    ManifestDeliveryMod.Log.Warning("[MD][Stats] TimeManager type not found — YTD rollover disabled.");
                    return 0;
                }

                var instProp = tmType.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                object? inst = instProp?.GetValue(null, null);
                if (inst == null)
                {
                    ManifestDeliveryMod.Log.Warning("[MD][Stats] TimeManager.Instance null — YTD rollover disabled.");
                    return 0;
                }

                foreach (var n in new[] { "year", "Year", "currentYear", "CurrentYear", "gameYear", "GameYear" })
                {
                    var p = tmType.GetProperty(n,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (p != null && p.PropertyType == typeof(int) && p.GetGetMethod(true) != null)
                    {
                        var getter = p.GetGetMethod(true);
                        var captured = inst;
                        _readYear = () => (int)getter.Invoke(captured, null);
                        ManifestDeliveryMod.Log.Msg($"[MD][Stats] Year accessor: TimeManager.{n} (property)");
                        return _readYear();
                    }
                    var f = tmType.GetField(n,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (f != null && f.FieldType == typeof(int))
                    {
                        var captured = inst; var captureF = f;
                        _readYear = () => (int)captureF.GetValue(captured);
                        ManifestDeliveryMod.Log.Msg($"[MD][Stats] Year accessor: TimeManager.{n} (field)");
                        return _readYear();
                    }
                }
                ManifestDeliveryMod.Log.Warning("[MD][Stats] No int year property/field on TimeManager — YTD rollover disabled.");
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][Stats] Year lookup failed: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// ItemStorage is a plain class — resolve its GameObject via the
        /// ReservableItemStorage MonoBehaviour that owns it.
        /// </summary>
        private static string DescribeStorage(ItemStorage storage)
        {
            // originStorage is usually the wagon's own inventory — either null
            // or an ItemStorage with no reservableItemStorageOwner link.
            if (storage == null) return "(wagon)";
            var owner = storage.reservableItemStorageOwner;
            if (owner != null) return owner.gameObject.name;
            return "(wagon)";
        }

        /// <summary>
        /// IContainsItems may be a MonoBehaviour (building storage) or a plain
        /// ItemStorage. Cast both ways to get a readable name.
        /// </summary>
        private static string DescribeContainer(IContainsItems container)
        {
            if (container == null) return "?";
            if (container is MonoBehaviour mb && mb != null) return mb.gameObject.name;
            if (container is ItemStorage store) return DescribeStorage(store);
            return container.GetType().Name;
        }
    }
}
