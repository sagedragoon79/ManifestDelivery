using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 1
    /// (above LogisticsProxy = 0, below CampHaul = 2 and ReturnTrip = 3).
    ///
    /// EXECUTION ORDER within a single task-search cycle:
    ///   KickOut(10) → ReturnTrip(3) → CampHaul(2) → HubHaul(1) → LogisticsProxy(0) → ParkWagon(-10)
    ///
    /// WHAT IT DOES (the proactive Hub distributor):
    ///   When the wagon belongs to a Hub-mode shop and is idle, this entry
    ///   proactively scans EVERY logistics requester within the Hub work radius
    ///   for any building with an active delivery OR move-out request the wagon
    ///   can fulfill — markets, shelters/residences, producers, storages, no
    ///   building-type filtering. The nearest eligible requester is temporarily
    ///   assigned to the wagon, then this entry returns null so the game's
    ///   LogisticsProxySearchEntry creates the real haul task.
    ///
    ///   This is the difference between Hub mode actually distributing goods and
    ///   merely doing opportunistic backhaul. ReturnTrip only fires AFTER a
    ///   delivery completes (JustDelivered); HubHaul fires on a cooldown while
    ///   the wagon is idle, so a Hub wagon sets out on its own instead of waiting
    ///   for vanilla to hand it a first job.
    ///
    /// CONTRAST WITH CampHaul:
    ///   CampHaul pulls move-out goods FROM producers TO storage (extraction).
    ///   HubHaul serves ANY request in radius (distribution + collection). The
    ///   two never run on the same wagon — they're gated on different modes.
    ///
    /// COOLDOWN:
    ///   Scans at most once every 1.5 s per wagon (NextHubHaulScanTime gate).
    /// </summary>
    public class HubHaulSearchEntry : TaskSearchEntry
    {
        private readonly TransportWagon _wagon;
        private readonly WagonEnhancementData _data;

        private const int PriorityModifier = 1;
        private const float ScanCooldown = 1.5f;

        public HubHaulSearchEntry(TransportWagon wagon, WagonEnhancementData data)
            : base(
                wagon,                                   // _receiver
                new WorkTaskID(WorkTaskType.ParkWagon),  // _taskID (unused — we always return null)
                false,                                   // _canEverBeAsync
                0,                                       // baseMaxPriority
                PriorityModifier,                        // _priorityModifier
                // ProcessNewTask always returns null, so vanilla's fail-delay
                // (default 2 s) would mask our own 1.5 s cooldown. Zero both —
                // NextHubHaulScanTime enforces the intended cadence.
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

            // Only active in Hub mode with hub hauling enabled.
            var shop = _data.ResolveShopEnhancement(_wagon);
            if (shop == null || !shop.IsHubHaulActive)
                return null;

            // Let ReturnTrip handle the immediate post-delivery backhaul first.
            if (_data.JustDelivered)
                return null;

            // Driver must be present.
            if (_wagon.driver.IsNull())
                return null;

            // Cooldown: don't scan every task cycle.
            if (Time.time < _data.NextHubHaulScanTime)
                return null;

            _data.NextHubHaulScanTime = Time.time + ScanCooldown;

            // ── Find the best requester anywhere in the Hub radius ────────────
            Vector3 diagShopPos = shop.transform.position;
            float   diagRadius  = shop.WorkRadius;
            LogisticsRequester? best = FindBestHubRequester();
            if (best == null)
            {
                // Log only on the transition finding → empty so an idle hub
                // doesn't spam a line every cooldown.
                if (!_data.LastHubHaulScanWasEmpty)
                {
                    _data.LastHubHaulScanWasEmpty = true;
                    ManifestDeliveryMod.LogVerbose(
                        $"[MD] HubHaul EMPTY: {_wagon.name} " +
                        $"— no requests in {diagRadius:F0}u around hub at " +
                        $"({diagShopPos.x:F0},{diagShopPos.z:F0})");
                }
                return null;
            }
            _data.LastHubHaulScanWasEmpty = false;

            // ── Temporarily assign the requester to this wagon ────────────────
            try
            {
                best.AssignWorker(
                    _wagon,
                    LogisticsAssignment.AssignmentCategory.Default,
                    LogisticsAssignment.AssignmentPriority.Default);

                _data.HubHaulRequester = best;

                string items = DescribeRequests(best);
                float distFromShop = Vector3.Distance(diagShopPos, best.transform.position);
                ManifestDeliveryMod.LogVerbose(
                    $"[MD] HubHaul CLAIM: {_wagon.name} → " +
                    $"{best.gameObject.name} " +
                    $"[{items}] " +
                    $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u from wagon, " +
                    $"{distFromShop:F0}u from hub)");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] HubHaul assignment failed for {_wagon.name}: {ex.Message}");
            }

            // Always return null — LogisticsProxy creates the real task.
            return null;
        }
#nullable restore warnings

        // ── Private helpers ───────────────────────────────────────────────────

        private LogisticsRequester? FindBestHubRequester()
        {
            WagonShopEnhancement? shop = _data.ShopEnhancement;
            if (shop == null) return null;

            GameManager? gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return null;
            LogisticsAggregator? aggregator = ReturnTripSearchEntry.GetAggregatorPublic(gm);
            if (aggregator == null)
            {
                ManifestDeliveryMod.Log.Warning("[MD] HubHaul: could not resolve LogisticsAggregator.");
                return null;
            }

            float radiusSqr = shop.WorkRadius * shop.WorkRadius;
            Vector3 shopPos = shop.transform.position;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;

            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;

                // Within the Hub service radius (matches the visual circle).
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip if this wagon is already assigned here.
                if (IsAlreadyAssigned(requester)) continue;

                // Any eligible delivery OR move-out request qualifies — no
                // building-type filtering. Markets, shelters, producers,
                // storages are all fair game.
                if (!HasEligibleRequest(requester)) continue;

                // Score by distance to the WAGON — the real driving distance.
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
        /// Returns true when the requester has at least one delivery or move-out
        /// request that passes the wagon's bulk-transport minimum.
        /// </summary>
        private bool HasEligibleRequest(LogisticsRequester requester)
        {
            foreach (var kv in requester.activeDeliveryRequests)
                if (PassesBulkCheck(kv.Value)) return true;

            foreach (var kv in requester.activeMoveOutRequests)
                if (PassesBulkCheck(kv.Value)) return true;

            return false;
        }

        /// <summary>Mirrors PassesBulkTransportItemCountRestrictions.</summary>
        private bool PassesBulkCheck(ItemRequest request)
        {
            if (request.minItemCountForBulkTransport == 0) return true;
            uint available = request.GetTotalUnreservedCount();
            return available >= request.minItemCountForBulkTransport;
        }

        /// <summary>Formats both delivery [in] and move-out [out] items for logs.</summary>
        private static string DescribeRequests(LogisticsRequester requester)
        {
            var parts = new List<string>();
            foreach (var kv in requester.activeDeliveryRequests)
            {
                string item = kv.Key.customStringTiedToID ?? "?";
                parts.Add($"{item} [in]");
            }
            foreach (var kv in requester.activeMoveOutRequests)
            {
                string item = kv.Key.customStringTiedToID ?? "?";
                uint qty   = kv.Value.GetTotalUnreservedCount();
                parts.Add($"{item}×{qty} [out]");
            }
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "nothing";
        }

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
