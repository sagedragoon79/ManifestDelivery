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
    /// TRADING POSTS (Hub Stocks Trading Post, experimental):
    ///   A post's stock work is a Deliver request into its trader storage,
    ///   assigned only to the post's own traders, so no wagon serves it unless
    ///   MD claims it. With the setting on, HubHaul claims the post's largest
    ///   stock shortfall that meets Minimum Wagon Load — that one request, so
    ///   vanilla's Deliver route gathers the good from storage up to capacity.
    ///   Posts are never claimed any other way (ReturnTrip skips them too).
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
            LogisticsRequester? best = FindBestHubRequester(out ItemRequest? stockRequest);
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

                if (stockRequest != null)
                {
                    // Trading Post stock: claim just that request. Like a
                    // multi-source claim, it routes through FindBestRouteDeliver,
                    // which gathers the good from storage up to capacity.
                    _data.ClaimRequest(_wagon, stockRequest, hubHerd: true);

                    if (ManifestDeliveryMod.IsVerbose)
                    {
                        string item = stockRequest is SingleItemRequest single ? single.itemID.ToString() : "?";
                        ManifestDeliveryMod.LogVerbose(
                            $"[MD] HubHaul CLAIM (Trading Post stock): {_wagon.name} → " +
                            $"{best.gameObject.name} [{item}×{stockRequest.GetTotalUnreservedCount()} short] " +
                            $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u from wagon, " +
                            $"{Vector3.Distance(diagShopPos, best.transform.position):F0}u from hub)");
                    }
                }
                else if (multiSource)
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
                    // Only the requests the scan approved (bulk + minimum load).
                    _data.ClaimRequester(_wagon, best,
                        request => PassesBulkCheck(request) && ClaimHelpers.MeetsMinLoad(_wagon, request));

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

        /// <summary>
        /// The nearest requester (to the wagon) with work for this wagon. When
        /// that's a Trading Post, <paramref name="stockRequest"/> is the stock
        /// request to claim; otherwise it's null.
        /// </summary>
        private LogisticsRequester? FindBestHubRequester(out ItemRequest? stockRequest)
        {
            stockRequest = null;
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
            bool stockPosts = ManifestDeliveryMod.HubStockTradingPost != null
                              && ManifestDeliveryMod.HubStockTradingPost.Value;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;
            var assignedRequesters = ClaimHelpers.CollectAssignedRequesters(_wagon, _assignedBuffer);

            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;

                // Within the Hub service radius (matches the visual circle).
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Trading Posts: only their stock work, and only with Hub Stocks
                // Trading Post on. Checked before the assignment test because
                // vanilla may assign every wagon to the post's storage-side
                // requests, which says nothing about its stock requests.
                if (requester.owner is TradingPost post)
                {
                    if (!stockPosts) continue;
                    ItemRequest? stock = GetTradingPostStockRequest(requester, post);
                    if (stock == null) continue;
                    float postDistSqr = (requester.transform.position - _wagon.transform.position).sqrMagnitude;
                    if (postDistSqr < bestDistSqr)
                    {
                        bestDistSqr   = postDistSqr;
                        bestRequester = requester;
                        stockRequest  = stock;
                    }
                    continue;
                }

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
                    stockRequest  = null;
                }
            }

            return bestRequester;
        }

        /// <summary>
        /// The Trading Post's largest stock shortfall this wagon may take, or
        /// null. Stock requests deliver into the post's trader storage
        /// (TradingPost.CheckWorkAvailabilityForTraderStockingItem): the target
        /// you set minus what's there, with no bulk minimum. The traders and a
        /// claimed wagon share the request, and its reservations keep them from
        /// bringing more than the shortfall.
        /// </summary>
        private ItemRequest? GetTradingPostStockRequest(LogisticsRequester requester, TradingPost post)
        {
            if (post.traderStorage == null) return null;
            float minLoad  = ClaimHelpers.MinLoadWeight(_wagon);
            float capacity = _wagon.GetCarryCapacity();
            ItemRequest? best = null;
            float bestLoad = 0f;
            foreach (var kv in requester.activeDeliveryRequests)
            {
                ItemRequest request = kv.Value;
                if (!(request is SingleItemRequest single)) continue;
                if (!ReferenceEquals(request.storageForAction, post.traderStorage)) continue;
                if (_wagon.logisticsAssignment.GetAssignedPriorityForRequest(
                        request, LogisticsAssignment.AssignmentCategory.Default, out _)) continue;
                if (!PassesBulkCheck(request)) continue;

                // What one trip can actually bring: the shortfall, capped by the
                // good's unreserved stock around town. The shortfall alone sent
                // wagons across town for 1–3 spice when the target was big but
                // the town barely had any.
                float load = ClaimHelpers.RequestLoadWeight(request);
                if (load <= 0f || load < minLoad) continue;
                load = Mathf.Min(load, AvailableWeight(single.itemID, post.traderStorage));
                if (load <= 0f || load < minLoad) continue;

                // Herd guard in wagon loads: the item-count guard assumed ~100
                // items a load, but a wagon carries 1,700+ light goods, so
                // several wagons piled onto one shortfall and all but the
                // first found scraps.
                if (capacity > 0f && HubClaimCount(request) >= Mathf.CeilToInt(load / capacity)) continue;

                if (load > bestLoad)
                {
                    bestLoad = load;
                    best = request;
                }
            }
            return best;
        }

        /// <summary>
        /// Weight of <paramref name="itemID"/> the game could route to a post
        /// right now: the unreserved stock in every container holding it — the
        /// same "has item" bucket the route search draws from — minus the
        /// post's own trader storage.
        /// </summary>
        private static float AvailableWeight(ItemID itemID, IContainsItems exclude)
        {
            var wbm = UnitySingleton<GameManager>.Instance?.workBucketManager;
            if (wbm?.itemByItemIDRO == null
                || !wbm.itemByItemIDRO.TryGetValue(itemID, out Item item) || item == null)
                return 0f;
            WorkBucket bucket = wbm.GetHasItemWorkBucketByItem(wbm, item);
            if (bucket == null) return 0f;

            uint total = 0;
            foreach (IRegistersForWork holder in bucket.workObjsRO)
            {
                if (holder.IsNull()) continue;
                ReservableItemStorage storage = holder.GetStorageForContainer(bucket);
                if (storage == null || ReferenceEquals(storage, exclude)) continue;
                total += storage.GetNumberOfUnreservedItems(item);
            }
            return total * item.weight;
        }

        private static int HubClaimCount(ItemRequest request) =>
            _hubClaimCounts.TryGetValue(request, out int n) ? n : 0;

        /// <summary>
        /// Returns true when the requester has at least one delivery or move-out
        /// request that passes the wagon's bulk-transport minimum.
        /// </summary>
        private bool HasEligibleRequest(LogisticsRequester requester)
        {
            // Bulk check (vanilla's minimum) plus MD's Minimum Wagon Load, so a
            // Hub wagon doesn't cross town for a handful of items.
            foreach (var kv in requester.activeDeliveryRequests)
                if (PassesBulkCheck(kv.Value) && ClaimHelpers.MeetsMinLoad(_wagon, kv.Value)) return true;

            // Multi-source mode never selects a requester for its TakeOut work —
            // a claimed TakeOut request routes single-source (FindBestRouteTakeOut),
            // which is the near-empty-haul symptom. Deliver-only here steers the
            // wagon onto the multi-source FindBestRouteDeliver path.
            if (ManifestDeliveryMod.HubMultiSourcePickup != null
                && ManifestDeliveryMod.HubMultiSourcePickup.Value)
                return false;

            foreach (var kv in requester.activeMoveOutRequests)
                if (PassesBulkCheck(kv.Value) && ClaimHelpers.MeetsMinLoad(_wagon, kv.Value)) return true;

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
                if (PassesBulkCheck(kv.Value) && ClaimHelpers.MeetsMinLoad(_wagon, kv.Value)) return kv.Value;
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
