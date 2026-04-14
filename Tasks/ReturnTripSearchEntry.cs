using System.Collections.Generic;
using UnityEngine;
using WagonShopsEnhanced;
using WagonShopsEnhanced.Components;

namespace WagonShopsEnhanced.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 3
    /// (above LogisticsProxy = 0, below KickOutAroundThreat = 10).
    ///
    /// EXECUTION ORDER within a single task-search cycle:
    ///   KickOut(10) → ReturnTrip(3) → LogisticsProxy(0) → ParkWagon(-10)
    ///
    /// WHAT IT DOES:
    ///   When a wagon just completed a delivery (JustDelivered flag is set),
    ///   this entry scans active stationary requesters within a configurable
    ///   radius of the wagon's current world position.  The nearest eligible
    ///   requester is temporarily assigned to the wagon, then this entry returns
    ///   null so the search continues.  LogisticsProxy fires next, finds the
    ///   newly-assigned requests, and returns a real logistics task — the wagon
    ///   never parks.
    ///
    ///   If no suitable requester is found, or if return-trip is disabled, this
    ///   entry is a no-op and the wagon parks normally.
    ///
    /// CLEAN-UP:
    ///   The temporary assignment is stored in WagonEnhancementData.
    ///   TransportWagonPatches clears it either when the logistics task actually
    ///   starts (success) or when ParkWagonThenIdleSubTask begins (failure —
    ///   nothing was close enough to make a trip worthwhile).
    /// </summary>
    public class ReturnTripSearchEntry : TaskSearchEntry
    {
        private readonly TransportWagon _wagon;
        private readonly WagonEnhancementData _data;

        // Priority modifier higher than LogisticsProxy (0) ensures this entry
        // fires first in the same search cycle.
        private const int PriorityModifier = 3;

        // Camp backhaul: items the wagon will haul FROM hub TO camp residences.
        // Processed food (not raw — camp produces raw items) + firewood + beer.
        // Raw items are intentionally excluded since they typically originate
        // from camps themselves; camps should get finished goods they can't make.
        private static readonly HashSet<ItemID> CampBackhaulItems = new HashSet<ItemID>
        {
            ItemID.Firewood,
            // Processed food
            ItemID.Bread,
            ItemID.Pastry,
            ItemID.SmokedMeat,
            ItemID.SmokedFish,
            ItemID.Cheese,
            ItemID.Preserves,
            ItemID.PreservedVeg,
            // Morale booster — camp pub is a must
            ItemID.WheatBeer,
        };

        public ReturnTripSearchEntry(TransportWagon wagon, WagonEnhancementData data)
            : base(
                wagon,                                   // _receiver
                new WorkTaskID(WorkTaskType.ParkWagon),  // _taskID  (unused — we always return null)
                false,                                   // _canEverBeAsync
                0,                                       // baseMaxPriority
                PriorityModifier)                        // _priorityModifier
        {
            _wagon = wagon;
            _data  = data;
        }

#nullable disable warnings
        protected override Task ProcessNewTask(
            int? relativePriorityToBeat,
            Task currentHighestPriorityTask)
        {
            // ── Early exits ───────────────────────────────────────────────────

            if (!WagonShopsEnhancedMod.ReturnTripEnabled.Value)
                return null;

            if (!_data.JustDelivered)
                return null;

            // Driver must still be inside the wagon.
            if (_wagon.driver.IsNull())
                return null;

            // ── Clear the flag immediately so we only run once per delivery ──
            _data.JustDelivered = false;

            // ── Find the best nearby requester ────────────────────────────────
            LogisticsRequester? best = FindBestNearbyRequester();
            if (best == null)
                return null;   // nothing nearby → fall through to ParkWagon

            // ── Temporarily assign the requester to this wagon ────────────────
            //    LogisticsRequester.AssignWorker propagates the assignment to
            //    every active request on that requester, making them visible to
            //    the LogisticsProxySearchEntry that fires next.
            try
            {
                best.AssignWorker(
                    _wagon,
                    LogisticsAssignment.AssignmentCategory.Default,
                    LogisticsAssignment.AssignmentPriority.Default);

                _data.TemporaryRequester = best;

                WagonShopsEnhancedMod.Log.Msg(
                    $"[WSE] ReturnTrip: {_wagon.name} assigned to " +
                    $"{best.gameObject.name} " +
                    $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u away)");
            }
            catch (System.Exception ex)
            {
                WagonShopsEnhancedMod.Log.Warning(
                    $"[WSE] ReturnTrip assignment failed for {_wagon.name}: {ex.Message}");
            }

            // Always return null — we never create a task ourselves.
            // LogisticsProxy will see the new assignment and create the real task.
            return null;
        }
#nullable restore warnings

        // ── Private helpers ───────────────────────────────────────────────────

        private LogisticsRequester? FindBestNearbyRequester()
        {
            LogisticsAggregator? aggregator = GetAggregator();
            if (aggregator == null)
            {
                WagonShopsEnhancedMod.Log.Warning("[WSE] ReturnTrip: could not resolve LogisticsAggregator.");
                return null;
            }

            float radius     = _data.ReturnTripRadius;
            float radiusSqr  = radius * radius;

            // Camp mode: anchor search to the shop's position, not the wagon's.
            // This means after delivering to the hub, the wagon only finds backhaul
            // work near its home camp — not random hub-area work.
            Vector3 searchCenter;
            bool isCampMode = _data.ShopEnhancement != null
                              && _data.ShopEnhancement.Mode == Components.ShopMode.Camp;

            if (isCampMode && _data.ShopEnhancement != null)
                searchCenter = _data.ShopEnhancement.transform.position;
            else
                searchCenter = _wagon.transform.position;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;

            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                // Skip requesters that have no active work left.
                if (!requester.hasActiveRequests) continue;

                // Skip if the wagon is already assigned to this requester
                // (would create a duplicate assignment).
                if (IsAlreadyAssigned(requester)) continue;

                // Distance filter — measured from search center (shop in Camp, wagon otherwise).
                float distSqr = (requester.transform.position - searchCenter).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Check that at least one active request is eligible.
                // In Camp mode, we prioritize firewood + food backhauls to camp residences
                // (delivery requests) with a relaxed threshold.
                if (!HasEligibleRequest(requester, isCampMode)) continue;

                // Pick closest to the wagon for efficient routing
                float wagonDistSqr = (requester.transform.position - _wagon.transform.position).sqrMagnitude;
                if (wagonDistSqr < bestDistSqr)
                {
                    bestDistSqr  = wagonDistSqr;
                    bestRequester = requester;
                }
            }

            return bestRequester;
        }

        /// <summary>
        /// Returns true when the wagon already has a Default-category assignment
        /// to this requester (checked via the wagon's LogisticsAssignment component).
        /// </summary>
        private bool IsAlreadyAssigned(LogisticsRequester requester)
        {
            var assigned = _wagon.logisticsAssignment
                               .GetAssignedRequestsByCategory(
                                   LogisticsAssignment.AssignmentCategory.Default);
            if (assigned == null) return false;

            // Iterate the wagon's assigned requests and check whether any
            // belong to this requester.
            foreach (var kv in assigned)
            {
                if (kv.Key.requester == requester)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when at least one of the requester's active requests
        /// is eligible for this wagon.
        ///
        /// In Camp mode: prioritizes delivery requests for firewood + processed
        /// food + beer — the camp backhaul items. Relaxed bulk threshold so
        /// even small camp deficits trigger a backhaul trip.
        ///
        /// Outside Camp mode: standard bulk check on any delivery/move-out request.
        /// </summary>
        private bool HasEligibleRequest(LogisticsRequester requester, bool isCampMode)
        {
            // Camp mode: look for firewood/food delivery requests on camp buildings
            if (isCampMode)
            {
                foreach (var kv in requester.activeDeliveryRequests)
                {
                    if (IsCampBackhaulRequest(kv.Value))
                        return true;  // No bulk threshold — camp buildings take any amount
                }
                // Fall through and also check move-out (shouldn't fire normally
                // since CampHaul handles pickups, but safety for edge cases)
            }

            // Standard: delivery requests with bulk check
            foreach (var kv in requester.activeDeliveryRequests)
            {
                if (PassesBulkCheck(kv.Value)) return true;
            }

            // Move-out requests (things the wagon takes FROM this building).
            foreach (var kv in requester.activeMoveOutRequests)
            {
                if (PassesBulkCheck(kv.Value)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if this request is for an item in the camp backhaul
        /// whitelist (firewood, processed food, beer).
        /// </summary>
        private bool IsCampBackhaulRequest(ItemRequest request)
        {
            if (request is SingleItemRequest singleReq)
                return CampBackhaulItems.Contains(singleReq.itemID);

            // Multi-item requests (like FoodItemsRequest) — accept if the
            // requester is likely a camp residence needing food/heating.
            // We allow these since the request type itself implies food or fuel.
            if (request is MultiItemRequest)
            {
                string tag = request.requestTag.ToString();
                return tag.Contains("Food") || tag.Contains("HeatingFuel") || tag.Contains("Residence");
            }

            return false;
        }

        /// <summary>
        /// Mirrors PassesBulkTransportItemCountRestrictions from
        /// LogisticsGlobalQueryJob but executed on the main thread.
        /// </summary>
        private bool PassesBulkCheck(ItemRequest request)
        {
            // If the request has no minimum, it's always eligible.
            if (request.minItemCountForBulkTransport == 0) return true;

            // Wagon has RestrictByBulkMinItemCount — compare total unreserved
            // count against the minimum.
            uint available = request.GetTotalUnreservedCount();
            return available >= request.minItemCountForBulkTransport;
        }

        /// <summary>
        /// Resolves the global LogisticsAggregator from the singleton GameManager.
        /// The field is spelled "logisiticsAggregator" (typo in source) hence
        /// the reflection fallback.
        /// </summary>
        private static LogisticsAggregator? GetAggregator()
        {
            GameManager? gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return null;

            // Try direct field access first (faster).
            try { return gm.logisiticsAggregator; }
            catch { /* field name mismatch — fall through to reflection */ }

            // Reflection fallback (handles any future rename).
            var field = typeof(GameManager).GetField(
                "logisiticsAggregator",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            return field?.GetValue(gm) as LogisticsAggregator;
        }
    }
}
