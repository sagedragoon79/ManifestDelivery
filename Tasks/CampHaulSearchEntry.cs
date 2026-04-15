using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 2
    /// (above LogisticsProxy = 0, below ReturnTrip = 3).
    ///
    /// EXECUTION ORDER within a single task-search cycle:
    ///   KickOut(10) → ReturnTrip(3) → CampHaul(2) → LogisticsProxy(0) → ParkWagon(-10)
    ///
    /// WHAT IT DOES:
    ///   When the wagon belongs to a Camp-mode shop and is idle, this entry
    ///   proactively scans production buildings within the camp work radius
    ///   for goods with active TakeOut (move-out) requests.
    ///
    ///   Any production building's move-out request is eligible — the wagon
    ///   picks up whatever the camp produces and hauls it to wherever the
    ///   game's logistics system routes it (typically a hub storage building).
    ///
    ///   The nearest eligible requester is temporarily assigned to the wagon,
    ///   then this entry returns null so LogisticsProxy creates the real task.
    ///
    /// EXCLUSIONS:
    ///   Storage buildings are skipped — camp wagons pick up from PRODUCTION
    ///   buildings, not shuffle between storages.
    ///
    /// COOLDOWN:
    ///   Scans at most once every 10 seconds per wagon to avoid performance
    ///   impact on task search cycles.
    /// </summary>
    public class CampHaulSearchEntry : TaskSearchEntry
    {
        private readonly TransportWagon _wagon;
        private readonly WagonEnhancementData _data;

        private const int PriorityModifier = 2;
        // Short cooldown so wagons claim camp-zone move-out requests FAST —
        // before villagers (fishermen, foragers) self-assign and haul their
        // own output across the map. Each wagon scans at most once every 1.5s.
        private const float ScanCooldown = 1.5f;

        // Storage building tags — camp wagons should NOT pick up FROM these
        // (they pick up from production buildings and deliver TO storage)
        private static readonly HashSet<string> StorageBuildingTags = new HashSet<string>
        {
            "Stockyard", "StorageDepot", "Storehouse", "RootCellar",
            "MarketBuilding", "SupplyWagon", "Treasury"
        };

        public CampHaulSearchEntry(TransportWagon wagon, WagonEnhancementData data)
            : base(
                wagon,                                   // _receiver
                new WorkTaskID(WorkTaskType.ParkWagon),  // _taskID (unused — we always return null)
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

            // Only active in Camp mode with hauling enabled
            if (_data.ShopEnhancement == null || !_data.ShopEnhancement.IsCampHaulActive)
                return null;

            // Don't scan if the return-trip just fired (let it handle post-delivery)
            if (_data.JustDelivered)
                return null;

            // Driver must be present
            if (_wagon.driver.IsNull())
                return null;

            // Cooldown: don't scan every task cycle
            if (Time.time < _data.NextCampHaulScanTime)
                return null;

            _data.NextCampHaulScanTime = Time.time + ScanCooldown;

            // ── Find the best nearby source (building with goods to move out) ──
            LogisticsRequester? bestSource = FindBestCampSource();
            if (bestSource == null)
                return null;

            // ── Temporarily assign the requester to this wagon ──────────────
            try
            {
                bestSource.AssignWorker(
                    _wagon,
                    LogisticsAssignment.AssignmentCategory.Default,
                    LogisticsAssignment.AssignmentPriority.Default);

                _data.CampHaulRequester = bestSource;

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] CampHaul: {_wagon.name} assigned to " +
                    $"{bestSource.gameObject.name} " +
                    $"({Vector3.Distance(_wagon.transform.position, bestSource.transform.position):F1}u away)");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] CampHaul assignment failed for {_wagon.name}: {ex.Message}");
            }

            // Always return null — LogisticsProxy creates the real task
            return null;
        }
#nullable restore warnings

        // ── Private helpers ───────────────────────────────────────────────────

        private LogisticsRequester? FindBestCampSource()
        {
            WagonShopEnhancement? shop = _data.ShopEnhancement;
            if (shop == null) return null;

            GameManager? gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return null;

            float radiusSqr = shop.WorkRadius * shop.WorkRadius;
            Vector3 shopPos = shop.transform.position;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;

            foreach (LogisticsRequester requester in gm.logisiticsAggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;
                if (requester.activeMoveOutRequests.Count == 0) continue;

                // Must be within camp work radius of the shop
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip if wagon is already assigned here
                if (IsAlreadyAssigned(requester)) continue;

                // Skip storage buildings — camp wagons pick up from PRODUCTION
                // buildings, not shuffle between storages
                string tag = requester.gameObject.tag;
                if (!string.IsNullOrEmpty(tag) && StorageBuildingTags.Contains(tag))
                    continue;

                // Check bulk transport minimum on any move-out request
                if (!HasBulkEligibleRequest(requester)) continue;

                if (distSqr < bestDistSqr)
                {
                    bestDistSqr  = distSqr;
                    bestRequester = requester;
                }
            }

            return bestRequester;
        }

        /// <summary>
        /// Checks whether at least one move-out request passes the wagon's
        /// bulk minimum restriction.
        /// </summary>
        private bool HasBulkEligibleRequest(LogisticsRequester requester)
        {
            foreach (var kv in requester.activeMoveOutRequests)
            {
                if (PassesBulkCheck(kv.Value)) return true;
            }
            return false;
        }

        /// <summary>
        /// Mirrors PassesBulkTransportItemCountRestrictions.
        /// </summary>
        private bool PassesBulkCheck(ItemRequest request)
        {
            if (request.minItemCountForBulkTransport == 0) return true;
            uint available = request.GetTotalUnreservedCount();
            return available >= request.minItemCountForBulkTransport;
        }

        /// <summary>
        /// Returns true when the wagon already has an assignment to this requester.
        /// </summary>
        private bool IsAlreadyAssigned(LogisticsRequester requester)
        {
            var assigned = _wagon.logisticsAssignment
                               .GetAssignedRequestsByCategory(
                                   LogisticsAssignment.AssignmentCategory.Default);
            if (assigned == null) return false;

            foreach (var kv in assigned)
            {
                if (kv.Key.requester == requester)
                    return true;
            }
            return false;
        }

    }
}
