using System;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;
using ManifestDelivery.Systems;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Logs every wagon drop-off so we can see what actually got delivered
    /// (not just what got claimed), and feeds the per-shop hauling stats.
    ///
    /// Hooks: TransportWagon.ItemBundleDroppedOff(originStorage, dropOffStorage, bundle)
    ///   Fires whenever the wagon unloads a bundle at a destination.
    /// </summary>
    [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.ItemBundleDroppedOff))]
    internal static class TransportWagonDropOffPatch
    {
        // Simple dedupe — the game calls ItemBundleDroppedOff twice per drop-off
        // (once from the wagon side, once from the destination side). Suppress
        // the second call when we see the same bundle reference within 200ms.
        private static readonly System.Collections.Generic.Dictionary<int, float>
            _lastLogTime = new System.Collections.Generic.Dictionary<int, float>();

        /// <summary>
        /// Entries only matter for 200 ms, so the map is simply reset once it
        /// grows past this. (It used to grow by one entry per delivery, forever.)
        /// </summary>
        private const int DedupeMapLimit = 256;

        private static bool _yearUnavailable;

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
                if (_lastLogTime.Count >= DedupeMapLimit) _lastLogTime.Clear();
                _lastLogTime[bundleKey] = now;

                var data = __instance.GetComponent<WagonEnhancementData>();
                var shop = data?.ResolveShopEnhancement(__instance);

                if (ManifestDeliveryMod.IsVerbose)
                {
                    string mode = shop != null ? shop.Mode.ToString() : "Standard";
                    ManifestDeliveryMod.LogVerbose(
                        $"[MD] DELIVER ({mode}): {__instance.name} " +
                        $"{bundle.name}×{bundle.numberOfItems} " +
                        $"{DescribeStorage(originStorage)} → {DescribeContainer(dropOffStorage)}");
                }

                // ── Stats: record this delivery on the shop's running totals ──
                // Only counts deliveries to a shop with our enhancement (Camp,
                // Hub, or Standard mode set). Vagabond wagons with no shop
                // assignment are skipped — they wouldn't have a stable key
                // anyway, and per-wagon counts are vanilla's job.
                if (shop != null)
                {
                    // bundle.itemID, NOT Enum.TryParse(bundle.name): item names
                    // are keys like "ItemLogs" rather than enum names (see
                    // Item.itemIDByName), so the parse always failed and no
                    // per-item or raw/produced stat was ever recorded.
                    StatsTracker.RecordDelivery(
                        shopPos:   shop.transform.position,
                        shopName:  shop.gameObject.name,
                        modeInt:   (int)shop.Mode,
                        itemId:    (int)bundle.itemID,
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

        /// <summary>
        /// FF's canonical year: GameManager.timeManager.currentDate.year — the
        /// same source vanilla's "shipped last year" stats use. Read fresh on
        /// every call. The previous version cached a reflection closure bound
        /// to the first scene's TimeManager, so after a reload it kept reporting
        /// the old town's frozen date.
        /// </summary>
        private static int GetCurrentGameYear()
        {
            if (_yearUnavailable) return 0;
            try
            {
                return ReadYear();
            }
            catch (Exception ex)
            {
                _yearUnavailable = true;   // e.g. a game update renamed the member
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][Stats] Can't read the game year ({ex.Message}) — year-to-date rollover disabled.");
                return 0;
            }
        }

        // Separate method so a missing member fails once, inside the try above.
        private static int ReadYear()
        {
            var tm = UnitySingleton<GameManager>.Instance?.timeManager;
            return tm != null ? tm.currentDate.year : 0;
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
