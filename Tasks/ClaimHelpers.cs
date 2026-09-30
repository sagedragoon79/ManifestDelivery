using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
{
    /// <summary>
    /// Shared rules for MD's claiming search entries (ReturnTrip, CampHaul,
    /// HubHaul).
    ///
    /// REAL ORDER of a wagon's search entries (the task manager sorts by
    /// maxPriority, stable for ties; vanilla's LogisticsProxy has maxPriority 2):
    ///   KickOut(10) → ReturnTrip(3) → LogisticsProxy(2) → CampHaul(2) →
    ///   HubHaul(1) → ParkWagon(-10)
    /// A claim made by CampHaul or HubHaul is therefore picked up by
    /// LogisticsProxy on its next search, not in the same tick.
    /// </summary>
    internal static class ClaimHelpers
    {
        /// <summary>
        /// How long a claim may wait for vanilla to build a route before MD
        /// drops it. LogisticsProxy retries every ~2 s after a failed search, so
        /// this allows several attempts. A parked wagon never builds another
        /// park task, so without this a failed claim would sit forever.
        /// </summary>
        internal const float StaleClaimSeconds = 10f;

        /// <summary>
        /// True while the wagon is on a haul. The task manager keeps running
        /// lower-priority entries during a haul (hauls run at priority 0), so
        /// without this check MD kept claiming work for busy wagons every
        /// 1.5 s. Park/idle tasks are not LogisticsTasks, so parked wagons
        /// still get work.
        /// </summary>
        internal static bool IsHauling(Task currentHighestPriorityTask) =>
            currentHighestPriorityTask is LogisticsTask;

        /// <summary>
        /// True when the wagon may claim now. Holds off while an earlier claim
        /// is still waiting for its route, and releases claims that went stale.
        /// </summary>
        internal static bool ReadyToClaim(TransportWagon wagon, WagonEnhancementData data)
        {
            if (!data.HasClaims) return true;
            if (Time.time - data.ClaimTime < StaleClaimSeconds) return false;
            data.ReleaseClaims(wagon, "stale — no route built");
            return true;
        }

        // ── Minimum load ─────────────────────────────────────────────────

        /// <summary>
        /// Per-item weight for multi-item requests (food/fuel for homes), whose
        /// items aren't exposed. Roughly a food item's weight.
        /// </summary>
        internal const float MultiItemWeightEstimate = 10f;

        /// <summary>Weight of one unit of an item; 0 when unknown.</summary>
        internal static float ItemWeight(ItemID itemID)
        {
            var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
            if (wbm?.itemByItemIDRO != null && wbm.itemByItemIDRO.TryGetValue(itemID, out var item) && item != null)
                return item.weight;
            return 0f;
        }

        /// <summary>
        /// Weight of the work a request still offers: its unreserved count times
        /// the item's weight. For a Deliver request that's the remaining deficit;
        /// for a move-out, what's waiting to be picked up.
        /// </summary>
        internal static float RequestLoadWeight(ItemRequest request)
        {
            if (request == null) return 0f;
            uint count = request.GetTotalUnreservedCount();
            if (count == 0) return 0f;
            float weight = request is SingleItemRequest single ? ItemWeight(single.itemID) : 0f;
            if (weight <= 0f) weight = MultiItemWeightEstimate;
            return count * weight;
        }

        /// <summary>
        /// The smallest load (by weight) worth sending this wagon for, from the
        /// Minimum Wagon Load setting. 0 when the rule is off.
        /// </summary>
        internal static float MinLoadWeight(TransportWagon wagon)
        {
            var pref = ManifestDeliveryMod.MinLoadPercent;
            int percent = pref != null ? pref.Value : 0;
            if (percent <= 0 || wagon == null) return 0f;
            return wagon.GetCarryCapacity() * Mathf.Min(percent, 100) / 100f;
        }

        /// <summary>True when the request alone is worth a trip for this wagon.</summary>
        internal static bool MeetsMinLoad(TransportWagon wagon, ItemRequest request)
        {
            float min = MinLoadWeight(wagon);
            return min <= 0f || RequestLoadWeight(request) >= min;
        }

        /// <summary>
        /// Every building this wagon is currently assigned to (vanilla's
        /// storage-quota assignments plus pending MD claims), built once per
        /// scan. The old per-requester check rescanned the wagon's whole
        /// assignment set for every requester on the map.
        /// </summary>
        internal static HashSet<LogisticsRequester> CollectAssignedRequesters(
            TransportWagon wagon, HashSet<LogisticsRequester> buffer)
        {
            buffer.Clear();
            var assigned = wagon.logisticsAssignment.GetAssignedRequestsByCategory(
                LogisticsAssignment.AssignmentCategory.Default);
            if (assigned == null) return buffer;
            foreach (var kv in assigned)
            {
                var requester = kv.Key?.requester;
                if (requester != null) buffer.Add(requester);
            }
            return buffer;
        }
    }
}
