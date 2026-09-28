using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 2
    /// (ties with LogisticsProxy = 2 and sorts after it; below ReturnTrip = 3).
    ///
    /// EXECUTION ORDER within a single task-search cycle (see ClaimHelpers):
    ///   KickOut(10) → ReturnTrip(3) → LogisticsProxy(2) → CampHaul(2) →
    ///   HubHaul(1) → ParkWagon(-10)
    /// so LogisticsProxy uses a CampHaul claim on its next search.
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
    ///   The nearest eligible requester's active requests are claimed for the
    ///   wagon, then this entry returns null so LogisticsProxy creates the real
    ///   task. Claims are released once a route is built (WagonEnhancementData).
    ///
    /// EXCLUSIONS:
    ///   Storage buildings are skipped — camp wagons pick up from PRODUCTION
    ///   buildings, not shuffle between storages.
    ///
    /// COOLDOWN:
    ///   Scans at most once every 1.5 seconds per wagon, and never while the
    ///   wagon is already hauling or waiting on an earlier claim.
    /// </summary>
    public class CampHaulSearchEntry : TaskSearchEntry
    {
        private readonly TransportWagon _wagon;
        private readonly WagonEnhancementData _data;
        private readonly HashSet<LogisticsRequester> _assignedBuffer = new HashSet<LogisticsRequester>();

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
                PriorityModifier,                        // _priorityModifier
                // Align vanilla's search-delay with our intended scan cadence.
                // Default _additionalDelayIfLastTaskSearchFailed is 2 s; since
                // ProcessNewTask always returns null, that 2 s would mask our
                // 1.5 s cooldown. Set both to 0 — our own NextCampHaulScanTime
                // gate enforces the intended cadence.
                _delayBetweenNewTaskSearch: 0f,
                _additionalDelayIfLastTaskSearchFailed: 0f)
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

            // Only active in Camp mode with hauling enabled.
            // Lazy-resolve the shop link in case AssignedToWagonShop fired
            // before our data component existed.
            var shop = _data.ResolveShopEnhancement(_wagon);
            if (shop == null || !shop.IsCampHaulActive)
                return null;

            // Don't scan if the return-trip just fired (let it handle post-delivery)
            if (_data.JustDelivered)
                return null;

            // Already hauling: don't claim more work for a busy wagon.
            if (ClaimHelpers.IsHauling(currentHighestPriorityTask))
                return null;

            // Driver must be present
            if (_wagon.driver.IsNull())
                return null;

            // Don't stack claims while an earlier one waits for its route.
            if (!ClaimHelpers.ReadyToClaim(_wagon, _data))
                return null;

            // Cooldown: don't scan every task cycle
            if (Time.time < _data.NextCampHaulScanTime)
                return null;

            _data.NextCampHaulScanTime = Time.time + ScanCooldown;

            // ── Find the best nearby source (building with goods to move out) ──
            Vector3 diagShopPos = shop.transform.position;
            float   diagRadius  = shop.WorkRadius;
            LogisticsRequester? bestSource = FindBestCampSource();
            if (bestSource == null)
            {
                // Log only on the transition from "finding work" → "now empty"
                // so an idle camp doesn't generate a line every scan cooldown.
                // Earlier logs were dominated by hundreds of EMPTY lines per
                // minute when wagons were waiting for new production.
                if (!_data.LastCampHaulScanWasEmpty)
                {
                    _data.LastCampHaulScanWasEmpty = true;
                    ManifestDeliveryMod.LogVerbose(
                        $"[MD] CampHaul EMPTY: {_wagon.name} " +
                        $"— no camp sources in {diagRadius:F0}u around shop at " +
                        $"({diagShopPos.x:F0},{diagShopPos.z:F0})");
                }
                return null;
            }
            _data.LastCampHaulScanWasEmpty = false;

            // ── Claim the source's active requests for this wagon ───────────
            try
            {
                _data.ClaimRequester(_wagon, bestSource);

                if (ManifestDeliveryMod.IsVerbose)
                {
                    string items = DescribeMoveOut(bestSource);
                    float distFromShop = Vector3.Distance(
                        diagShopPos, bestSource.transform.position);
                    ManifestDeliveryMod.LogVerbose(
                        $"[MD] CampHaul CLAIM: {_wagon.name} → " +
                        $"{bestSource.gameObject.name} " +
                        $"[{items}] " +
                        $"({Vector3.Distance(_wagon.transform.position, bestSource.transform.position):F1}u from wagon, " +
                        $"{distFromShop:F0}u from shop)");
                }
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
            // FF made this field non-public — use the shared reflection helper.
            LogisticsAggregator? aggregator = ReturnTripSearchEntry.GetAggregatorPublic(gm);
            if (aggregator == null) return null;

            float radiusSqr = shop.WorkRadius * shop.WorkRadius;
            Vector3 shopPos = shop.transform.position;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;
            var assignedRequesters = ClaimHelpers.CollectAssignedRequesters(_wagon, _assignedBuffer);

            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;
                if (requester.activeMoveOutRequests.Count == 0) continue;

                // Must be within camp work radius of the shop
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip if wagon is already assigned here
                if (assignedRequesters.Contains(requester)) continue;

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
        /// Formats the requester's outgoing items as "Fish×25, Meat×10" for logs.
        /// </summary>
        private static string DescribeMoveOut(LogisticsRequester requester)
        {
            var parts = new List<string>();
            foreach (var kv in requester.activeMoveOutRequests)
            {
                string item = kv.Key.customStringTiedToID ?? "?";
                uint qty   = kv.Value.GetTotalUnreservedCount();
                parts.Add($"{item}×{qty}");
            }
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "nothing";
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

    }
}
