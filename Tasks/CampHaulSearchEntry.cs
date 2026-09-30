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
        private readonly List<LogisticsRequester> _campProducers = new List<LogisticsRequester>();

        // Output waiting at camp producers, full or not — what Camp Multi-Pickup
        // can collect as extra stops. Filled per scan only when pooling is on.
        private readonly List<ProducerStock> _campStock = new List<ProducerStock>();

        // The last scan's thresholds, so the claim applies the same test.
        private float _scanMinLoad;
        private bool _scanPooled;
        private float _scanDetourSqr;

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
                // Only its worthwhile OUTPUT. Claiming the whole building also
                // claimed its input deliveries, and vanilla would then serve a
                // 5-coal restock for a camp Foundry instead of its iron output.
                _data.ClaimRequester(_wagon, bestSource,
                    request => request.action == ItemAction.TakeOut
                               && IsWorthwhileMoveOut(bestSource, request, _scanMinLoad, _scanPooled, _scanDetourSqr));

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

            var assignedRequesters = ClaimHelpers.CollectAssignedRequesters(_wagon, _assignedBuffer);

            // Pass 1: every camp producer with output waiting.
            _campProducers.Clear();
            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;
                if (requester.activeMoveOutRequests.Count == 0) continue;

                // Must be within camp work radius of the shop
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip storage buildings — camp wagons pick up from PRODUCTION
                // buildings, not shuffle between storages
                string tag = requester.gameObject.tag;
                if (!string.IsNullOrEmpty(tag) && StorageBuildingTags.Contains(tag))
                    continue;

                _campProducers.Add(requester);
            }

            // Pass 2: the producer nearest the shop whose output is worth a trip.
            float minLoad = ClaimHelpers.MinLoadWeight(_wagon);
            bool pooled = ManifestDeliveryMod.CampMultiPickup != null && ManifestDeliveryMod.CampMultiPickup.Value;
            float detour = ManifestDeliveryMod.CampMultiPickupDetour != null
                ? ManifestDeliveryMod.CampMultiPickupDetour.Value : 80f;
            float detourSqr = detour * detour;
            _scanMinLoad = minLoad;
            _scanPooled = pooled;
            _scanDetourSqr = detourSqr;
            if (pooled && minLoad > 0f)
                CampProducers.Collect(shopPos, shop.WorkRadius, _campStock);
            else
                _campStock.Clear();

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;
            foreach (LogisticsRequester requester in _campProducers)
            {
                // Skip if wagon is already assigned here
                if (assignedRequesters.Contains(requester)) continue;

                if (!HasWorthwhileMoveOut(requester, minLoad, pooled, detourSqr)) continue;

                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr  = distSqr;
                    bestRequester = requester;
                }
            }

            return bestRequester;
        }

        /// <summary>
        /// True when one of the producer's move-outs passes vanilla's bulk
        /// minimum AND is worth a trip under Minimum Wagon Load. With Camp
        /// Multi-Pickup on, the same item waiting at other camp producers within
        /// the detour counts too — the wagon collects it on the way
        /// (CampMultiPickupPatch).
        /// </summary>
        private bool HasWorthwhileMoveOut(LogisticsRequester producer, float minLoad, bool pooled, float detourSqr)
        {
            foreach (var kv in producer.activeMoveOutRequests)
                if (IsWorthwhileMoveOut(producer, kv.Value, minLoad, pooled, detourSqr)) return true;
            return false;
        }

        /// <summary>The per-request test, shared by the scan and the claim.</summary>
        private bool IsWorthwhileMoveOut(LogisticsRequester producer, ItemRequest request,
            float minLoad, bool pooled, float detourSqr)
        {
            if (!PassesBulkCheck(request)) return false;
            if (minLoad <= 0f) return true;

            float load = ClaimHelpers.RequestLoadWeight(request);
            if (load < minLoad && pooled && request is SingleItemRequest single)
                load += NearbySameItemLoad(producer, single.itemID, detourSqr);
            return load >= minLoad;
        }

        /// <summary>
        /// Weight of <paramref name="itemID"/> waiting at other camp producers
        /// (full or not) within the detour of <paramref name="origin"/>, counting
        /// at most as many producers as a trip may add as extra stops.
        /// </summary>
        private float NearbySameItemLoad(LogisticsRequester origin, ItemID itemID, float detourSqr)
        {
            int maxStops = ManifestDeliveryMod.CampMultiPickupMaxStops != null
                ? ManifestDeliveryMod.CampMultiPickupMaxStops.Value : 3;
            float unitWeight = ClaimHelpers.ItemWeight(itemID);
            if (unitWeight <= 0f) return 0f;
            Vector3 originPos = origin.transform.position;
            float total = 0f;
            int stops = 0;
            foreach (var stock in _campStock)
            {
                if (stops >= maxStops) break;
                if (stock.Item != itemID || stock.Requester == origin) continue;
                if ((stock.Position - originPos).sqrMagnitude > detourSqr) continue;
                total += stock.Unreserved * unitWeight;
                stops++;
            }
            return total;
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
