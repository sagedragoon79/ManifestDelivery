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
                catch { _readYear = null; /* re-resolve on next call */ }
            }
            if (_yearLookupAttempted && _readYear == null) return 0;
            _yearLookupAttempted = true;
            try
            {
                var tmType = AccessTools.TypeByName("TimeManager");
                if (tmType == null)
                {
                    ManifestDeliveryMod.Log.Warning("[MD][Stats] TimeManager type not found — YTD rollover disabled.");
                    return 0;
                }

                // Try several singleton patterns. FF/DLC may use any of these.
                object? inst = ResolveTimeManagerInstance(tmType);
                if (inst == null)
                {
                    ManifestDeliveryMod.Log.Warning(
                        "[MD][Stats] TimeManager located but no singleton instance found via " +
                        "Instance/instance/UnitySingleton<>/FindObjectOfType — YTD rollover disabled.");
                    return 0;
                }

                // Try known year property/field names first.
                foreach (var n in new[] { "year", "Year", "currentYear", "CurrentYear", "gameYear", "GameYear", "currentGameYear" })
                {
                    if (TryBindYear(tmType, inst, n)) return _readYear!();
                }

                // Type-based scan — any int field/property whose name contains "year"
                // (case-insensitive). Survives renames as long as something
                // year-shaped exists.
                System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                foreach (var p in tmType.GetProperties(flags))
                {
                    if (p.PropertyType == typeof(int)
                        && p.GetGetMethod(true) != null
                        && p.Name.IndexOf("year", StringComparison.OrdinalIgnoreCase) >= 0
                        && p.GetIndexParameters().Length == 0)
                    {
                        if (TryBindYear(tmType, inst, p.Name)) return _readYear!();
                    }
                }
                foreach (var f in tmType.GetFields(flags))
                {
                    if (f.FieldType == typeof(int)
                        && f.Name.IndexOf("year", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (TryBindYear(tmType, inst, f.Name)) return _readYear!();
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

        /// <summary>Try several known singleton patterns to find the TimeManager
        /// instance. Returns null if nothing yields one.</summary>
        private static object? ResolveTimeManagerInstance(Type tmType)
        {
            System.Reflection.BindingFlags staticFlags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static;

            // 1) Standard `Instance` property/field on the type.
            foreach (var n in new[] { "Instance", "instance" })
            {
                var p = tmType.GetProperty(n, staticFlags);
                if (p != null) { var v = p.GetValue(null, null); if (v != null) return v; }
                var f = tmType.GetField(n, staticFlags);
                if (f != null) { var v = f.GetValue(null); if (v != null) return v; }
            }

            // 2) UnitySingleton<TimeManager>.Instance (FF's common base singleton).
            var usType = AccessTools.TypeByName("UnitySingleton`1");
            if (usType != null)
            {
                try
                {
                    var closed = usType.MakeGenericType(tmType);
                    var p = closed.GetProperty("Instance", staticFlags);
                    if (p != null) { var v = p.GetValue(null, null); if (v != null) return v; }
                    var f = closed.GetField("Instance", staticFlags);
                    if (f != null) { var v = f.GetValue(null); if (v != null) return v; }
                }
                catch { /* generic close may fail on non-Unity ancestors — fine */ }
            }

            // 3) Scene singleton — look up by type via FindObjectOfType.
            try
            {
                var inst = UnityEngine.Object.FindObjectOfType(tmType);
                if (inst != null) return inst;
            }
            catch { }

            return null;
        }

        /// <summary>Bind _readYear to a specific property or field by name.
        /// Returns true if bound successfully and the value can be read.</summary>
        private static bool TryBindYear(Type tmType, object inst, string memberName)
        {
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var p = tmType.GetProperty(memberName, flags);
            if (p != null && p.PropertyType == typeof(int) && p.GetGetMethod(true) != null)
            {
                var getter = p.GetGetMethod(true);
                var captured = inst;
                _readYear = () => (int)getter.Invoke(captured, null);
                try { _readYear(); ManifestDeliveryMod.Log.Msg($"[MD][Stats] Year accessor: TimeManager.{memberName} (property)"); return true; }
                catch { _readYear = null; return false; }
            }
            var f = tmType.GetField(memberName, flags);
            if (f != null && f.FieldType == typeof(int))
            {
                var captured = inst; var captureF = f;
                _readYear = () => (int)captureF.GetValue(captured);
                try { _readYear(); ManifestDeliveryMod.Log.Msg($"[MD][Stats] Year accessor: TimeManager.{memberName} (field)"); return true; }
                catch { _readYear = null; return false; }
            }
            return false;
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
