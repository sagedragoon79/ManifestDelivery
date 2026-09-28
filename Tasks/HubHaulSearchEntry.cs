using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 1
    /// (below LogisticsProxy = 2, CampHaul = 2 and ReturnTrip = 3).
    ///
    /// EXECUTION ORDER within a single task-search cycle (see ClaimHelpers):
    ///   KickOut(10) → ReturnTrip(3) → LogisticsProxy(2) → CampHaul(2) →
    ///   HubHaul(1) → ParkWagon(-10)
    /// so LogisticsProxy uses a HubHaul claim on its next search.
    ///
    /// WHAT IT DOES (the proactive Hub distributor):
    ///   When the wagon belongs to a Hub-mode shop and is idle, this entry
    ///   proactively scans EVERY logistics requester within the Hub work radius
    ///   for any building with an active delivery OR move-out request the wagon
    ///   can fulfill — markets, shelters/residences, producers, storages, no
    ///   building-type filtering. The nearest eligible requester's work is
    ///   claimed for the wagon, then this entry returns null so the game's
    ///   LogisticsProxySearchEntry creates the real haul task. Claims are
    ///   released once a route is built (WagonEnhancementData).
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
        private readonly HashSet<LogisticsRequester> _assignedBuffer = new HashSet<LogisticsRequester>();

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

            // Already hauling: don't claim more work for a busy wagon.
            if (ClaimHelpers.IsHauling(currentHighestPriorityTask))
                return null;

            // Driver must be present.
            if (_wagon.driver.IsNull())
                return null;

            // Don't stack claims while an earlier one waits for its route.
            if (!ClaimHelpers.ReadyToClaim(_wagon, _data))
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

            // ── Claim work for this wagon ─────────────────────────────────────
            try
            {
                bool multiSource = ManifestDeliveryMod.HubMultiSourcePickup != null
                                   && ManifestDeliveryMod.HubMultiSourcePickup.Value;

                if (multiSource)
                {
                    // Multi-source mode: claim ONLY the specific DELIVER request,
                    // via the per-request LogisticsRequest.AssignWorker overload —
                    // NOT LogisticsRequester.AssignWorker, which would also claim
                    // the building's TakeOut (move-out) requests and reintroduce
                    // single-source hauls. A Deliver-shaped claim routes through
                    // FindBestRouteDeliver, which fans the wagon across multiple
                    // source storages up to carry capacity before one drop-off.
                    // (HasEligibleRequest already gated `best` to Deliver-only.)
                    ItemRequest? deliver = GetEligibleDeliverRequest(best);
                    if (deliver == null) return null;  // gate guarantees one; be safe

                    // hubHerd: counts toward the herd guard until released.
                    _data.ClaimRequest(_wagon, deliver, hubHerd: true);

                    if (ManifestDeliveryMod.IsVerbose)
                    {
                        string items = DescribeRequests(best);
                        float distFromShop = Vector3.Distance(diagShopPos, best.transform.position);
                        ManifestDeliveryMod.LogVerbose(
                            $"[MD] HubHaul CLAIM (multi-source Deliver): {_wagon.name} → " +
                            $"{best.gameObject.name} [{items}] " +
                            $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u from wagon, " +
                            $"{distFromShop:F0}u from hub)");
                    }
                }
                else
                {
                    _data.ClaimRequester(_wagon, best);

                    if (ManifestDeliveryMod.IsVerbose)
                    {
                        string items = DescribeRequests(best);
                        float distFromShop = Vector3.Distance(diagShopPos, best.transform.position);
                        ManifestDeliveryMod.LogVerbose(
                            $"[MD] HubHaul CLAIM: {_wagon.name} → " +
                            $"{best.gameObject.name} " +
                            $"[{items}] " +
                            $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u from wagon, " +
                            $"{distFromShop:F0}u from hub)");
                    }
                }
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

            bool multiSource = ManifestDeliveryMod.HubMultiSourcePickup != null
                               && ManifestDeliveryMod.HubMultiSourcePickup.Value;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;
            var assignedRequesters = ClaimHelpers.CollectAssignedRequesters(_wagon, _assignedBuffer);

            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;

                // Within the Hub service radius (matches the visual circle).
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip if this wagon is already assigned here.
                if (assignedRequesters.Contains(requester)) continue;

                // Any eligible delivery OR move-out request qualifies — no
                // building-type filtering. Markets, shelters, producers,
                // storages are all fair game.
                if (!HasEligibleRequest(requester)) continue;

                // Herd guard (multi-source mode): all Hub wagons share the scan
                // cooldown, so without this they all claim the same nearest
                // Deliver request in one burst. Skip a request already covered by
                // enough Hub wagons for its remaining unreserved deficit, so the
                // fleet spreads across distinct requests.
                if (multiSource)
                {
                    ItemRequest? dreq = GetEligibleDeliverRequest(requester);
                    if (dreq == null || DeliverAlreadyCovered(dreq)) continue;
                }

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

            // Multi-source mode never selects a requester for its TakeOut work —
            // a claimed TakeOut request routes single-source (FindBestRouteTakeOut),
            // which is the near-empty-haul symptom. Deliver-only here steers the
            // wagon onto the multi-source FindBestRouteDeliver path.
            if (ManifestDeliveryMod.HubMultiSourcePickup != null
                && ManifestDeliveryMod.HubMultiSourcePickup.Value)
                return false;

            foreach (var kv in requester.activeMoveOutRequests)
                if (PassesBulkCheck(kv.Value)) return true;

            return false;
        }

        /// <summary>
        /// Returns the first bulk-eligible DELIVER request on this requester, or
        /// null. Used by multi-source mode to claim exactly that request (not the
        /// whole requester) so the wagon routes through FindBestRouteDeliver.
        /// </summary>
        private ItemRequest? GetEligibleDeliverRequest(LogisticsRequester requester)
        {
            foreach (var kv in requester.activeDeliveryRequests)
                if (PassesBulkCheck(kv.Value)) return kv.Value;
            return null;
        }

        // ── Herd guard: cap MD Hub wagons per Deliver request ─────────────────
        // All Hub wagons share the scan cooldown, so absent a guard they all
        // claim the same nearest Deliver request in one scan burst (observed:
        // 12 wagons onto one forge). We track how many Hub wagons MD has steered
        // onto each Deliver request and skip a request already covered by enough
        // wagons for its remaining unreserved deficit. Same-burst herding is
        // caught by the live count (incremented synchronously as each wagon
        // claims within the frame); post-reservation re-herding is caught by
        // GetTotalUnreservedCount dropping once the winning wagon's task reserves.
        private const int NominalItemsPerWagonLoad = 100;

        private static readonly Dictionary<ItemRequest, int> _hubClaimCounts =
            new Dictionary<ItemRequest, int>();

        internal static void RegisterHubClaim(ItemRequest deliver)
        {
            if (deliver == null) return;
            _hubClaimCounts.TryGetValue(deliver, out int n);
            _hubClaimCounts[deliver] = n + 1;
        }

        internal static void ReleaseHubClaim(ItemRequest deliver)
        {
            if (deliver == null) return;
            if (_hubClaimCounts.TryGetValue(deliver, out int n))
            {
                if (n <= 1) _hubClaimCounts.Remove(deliver);
                else        _hubClaimCounts[deliver] = n - 1;
            }
        }

        /// <summary>
        /// Drops all tracked Hub claims. Called on scene unload so ItemRequest
        /// keys from a previous save don't linger as stale dictionary entries.
        /// </summary>
        internal static void ClearHubClaims() => _hubClaimCounts.Clear();

        /// <summary>
        /// True when enough Hub wagons are already steered onto this Deliver
        /// request to cover its remaining unreserved deficit (one wagon-load per
        /// ~NominalItemsPerWagonLoad items needed).
        /// </summary>
        private static bool DeliverAlreadyCovered(ItemRequest deliver)
        {
            if (deliver == null) return false;
            if (!_hubClaimCounts.TryGetValue(deliver, out int claimed) || claimed <= 0)
                return false;

            uint unreserved = deliver.GetTotalUnreservedCount();
            if (unreserved == 0) return true;  // deficit already reserved away

            int loadsNeeded = Mathf.Max(1,
                Mathf.CeilToInt(unreserved / (float)NominalItemsPerWagonLoad));
            return claimed >= loadsNeeded;
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
    }
}
