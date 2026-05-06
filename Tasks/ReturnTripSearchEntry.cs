using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
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
                PriorityModifier,                        // _priorityModifier
                // Trip is JustDelivered-gated, not cooldown-gated, so zero out
                // vanilla's fail-delay (default 2 s) — we don't want vanilla
                // delaying the next check when our own flag controls frequency.
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

            if (!ManifestDeliveryMod.ReturnTripEnabled.Value)
                return null;

            if (!_data.JustDelivered)
                return null;

            // Driver must still be inside the wagon.
            if (_wagon.driver.IsNull())
                return null;

            // Lazy-resolve shop link so Camp/Hub search-center anchoring works
            // even when AssignedToWagonShop fired before our data component.
            _data.ResolveShopEnhancement(_wagon);

            // ── Clear the flag immediately so we only run once per delivery ──
            _data.JustDelivered = false;

            // ── Capture diag context BEFORE the scan so we can log either branch ─
            string diagMode = _data.ShopEnhancement != null
                ? _data.ShopEnhancement.Mode.ToString() : "None";
            Vector3 diagCenter;
            float diagRadius;
            bool diagShopAnchored = _data.ShopEnhancement != null
                && (_data.ShopEnhancement.Mode == Components.ShopMode.Camp
                    || _data.ShopEnhancement.Mode == Components.ShopMode.Hub);
            if (diagShopAnchored && _data.ShopEnhancement != null)
            {
                diagCenter = _data.ShopEnhancement.transform.position;
                diagRadius = _data.ShopEnhancement.WorkRadius;
            }
            else
            {
                diagCenter = _wagon.transform.position;
                diagRadius = _data.ReturnTripRadius;
            }

            // ── Find the best nearby requester ────────────────────────────────
            LogisticsRequester? best = FindBestNearbyRequester();
            if (best == null)
            {
                // Log the empty-scan so we can see WHEN backhaul tried but found
                // nothing. One line per drop-off, not per frame — cost is fine.
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] ReturnTrip EMPTY ({diagMode}): {_wagon.name} " +
                    $"— no candidates in {diagRadius:F0}u around " +
                    $"{(diagShopAnchored ? "shop" : "wagon")} at " +
                    $"({diagCenter.x:F0},{diagCenter.z:F0})");
                return null;   // nothing nearby → fall through to ParkWagon
            }

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

                string items = DescribeMoveOut(best);
                float distFromShop = diagShopAnchored
                    ? Vector3.Distance(diagCenter, best.transform.position)
                    : -1f;
                string shopDistStr = distFromShop >= 0f
                    ? $", {distFromShop:F0}u from shop"
                    : "";
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] ReturnTrip CLAIM ({diagMode}): {_wagon.name} → " +
                    $"{best.gameObject.name} " +
                    $"[{items}] " +
                    $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u from wagon" +
                    $"{shopDistStr})");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ReturnTrip assignment failed for {_wagon.name}: {ex.Message}");
            }

            // Always return null — we never create a task ourselves.
            // LogisticsProxy will see the new assignment and create the real task.
            return null;
        }
#nullable restore warnings

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Formats both delivery and move-out items as "Firewood×40 [in], Fish×25 [out]"
        /// for logs. In Camp mode, ReturnTrip typically claims delivery requests
        /// (firewood/food to camp buildings), so both directions matter.
        /// </summary>
        private static string DescribeMoveOut(LogisticsRequester requester)
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

        private LogisticsRequester? FindBestNearbyRequester()
        {
            LogisticsAggregator? aggregator = GetAggregator();
            if (aggregator == null)
            {
                ManifestDeliveryMod.Log.Warning("[MD] ReturnTrip: could not resolve LogisticsAggregator.");
                return null;
            }

            // Camp AND Hub modes: anchor search to the shop's position using
            // the shop's WorkRadius. This keeps both mode types focused on their
            // service area — Camp wagons only backhaul to camp buildings,
            // Hub wagons only serve the town. Matches the visual circle.
            //
            // Standard mode: scan around the wagon's current position using
            // ReturnTripRadiusStandard (no shop zone concept).
            Vector3 searchCenter;
            float radius;
            bool isCampMode = _data.ShopEnhancement != null
                              && _data.ShopEnhancement.Mode == Components.ShopMode.Camp;
            bool isShopAnchored = _data.ShopEnhancement != null
                                  && (_data.ShopEnhancement.Mode == Components.ShopMode.Camp
                                      || _data.ShopEnhancement.Mode == Components.ShopMode.Hub);

            if (isShopAnchored && _data.ShopEnhancement != null)
            {
                searchCenter = _data.ShopEnhancement.transform.position;
                radius = _data.ShopEnhancement.WorkRadius;  // Camp=60u, Hub=100u
            }
            else
            {
                searchCenter = _wagon.transform.position;
                radius = _data.ReturnTripRadius;            // Standard: around wagon
            }

            float radiusSqr = radius * radius;

            // Two-tier tracking when PreferWorkshopInput is on:
            //   bestWorkshop = closest non-storage requester (workshops, residences, etc.)
            //   bestRequester = closest of all requesters (current vanilla behavior)
            // If a workshop was found AND PreferWorkshopInput is true, return it.
            // Otherwise return the overall closest. When the toggle is off,
            // bestWorkshop is never populated (no extra cost) and behavior
            // is identical to the original.
            bool preferWorkshop = ManifestDeliveryMod.PreferWorkshopInput != null
                                  && ManifestDeliveryMod.PreferWorkshopInput.Value;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;
            LogisticsRequester? bestWorkshop = null;
            float bestWorkshopDistSqr = float.MaxValue;

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

                // Score by distance to the WAGON (not the search center) — that's
                // the actual driving distance the wagon will cover.
                float wagonDistSqr = (requester.transform.position - _wagon.transform.position).sqrMagnitude;

                // Track overall closest (vanilla behavior, also our fallback).
                if (wagonDistSqr < bestDistSqr)
                {
                    bestDistSqr  = wagonDistSqr;
                    bestRequester = requester;
                }

                // Also track closest workshop when the toggle is on.
                if (preferWorkshop && !IsStorageBuilding(requester))
                {
                    if (wagonDistSqr < bestWorkshopDistSqr)
                    {
                        bestWorkshopDistSqr = wagonDistSqr;
                        bestWorkshop = requester;
                    }
                }
            }

            // Workshop wins if toggle is on and one was found in range.
            // Otherwise fall through to overall closest (storage, etc.).
            if (preferWorkshop && bestWorkshop != null)
            {
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Backhaul tier: workshop-priority chose {bestWorkshop.gameObject.name} " +
                    $"(closest-overall would have been {bestRequester?.gameObject.name ?? "null"})");
                return bestWorkshop;
            }
            return bestRequester;
        }

        // ── Storage classification ────────────────────────────────────────────
        // FF building types whose primary role is INVENTORY HOLDING. When
        // PreferWorkshopInput is on, requesters living on these buildings are
        // demoted to Tier-2 (only chosen if no Tier-1 workshop is in range).
        // Add new storage types here if Crate adds more building types.
        private static readonly System.Collections.Generic.HashSet<string> StorageTypeNames =
            new System.Collections.Generic.HashSet<string>
            {
                "Storehouse",
                "StorageDepot",
                "RootCellar",
                "Granary",
                "Marketplace",
                "Treasury",
                "Stockyard",      // pre-built camp stockyard
            };

        // Per-requester cache of IsStorageBuilding result. Lifetime-stable —
        // a building's storage classification can't change without the building
        // being destroyed and replaced (upgrades create new instances). Drops
        // ~800 transform-walks + GetComponents allocations per second to
        // amortized zero after warmup. See ClearStorageCache for invalidation.
        private static readonly System.Collections.Generic.Dictionary<LogisticsRequester, bool> _storageCache
            = new System.Collections.Generic.Dictionary<LogisticsRequester, bool>();

        /// <summary>
        /// Clears the storage classification cache. Called on scene unload so
        /// the next load doesn't carry over destroyed requesters as dead
        /// dictionary keys (Unity-null Object references hash but won't ==).
        /// </summary>
        public static void ClearStorageCache() => _storageCache.Clear();

        /// <summary>
        /// Returns true when this requester lives on a pure-storage building.
        /// Walks up the GameObject parent chain looking for any MonoBehaviour
        /// whose type name matches the storage list. Robust against requesters
        /// being on child GameObjects (e.g. zone markers).
        /// </summary>
        private static bool IsStorageBuilding(LogisticsRequester req)
        {
            if (req == null || req.gameObject == null) return false;
            if (_storageCache.TryGetValue(req, out bool cached)) return cached;
            bool result = ComputeIsStorageBuilding(req);
            _storageCache[req] = result;
            return result;
        }

        private static bool ComputeIsStorageBuilding(LogisticsRequester req)
        {
            Transform t = req.transform;
            while (t != null)
            {
                var components = t.GetComponents<MonoBehaviour>();
                foreach (var c in components)
                {
                    if (c == null) continue;
                    if (StorageTypeNames.Contains(c.GetType().Name))
                        return true;
                }
                t = t.parent;
            }
            return false;
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
