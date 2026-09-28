using System.Collections.Generic;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 3
    /// (above LogisticsProxy = 2, below KickOutAroundThreat = 10).
    ///
    /// EXECUTION ORDER within a single task-search cycle (see ClaimHelpers):
    ///   KickOut(10) → ReturnTrip(3) → LogisticsProxy(2) → CampHaul(2) →
    ///   HubHaul(1) → ParkWagon(-10)
    ///
    /// WHAT IT DOES:
    ///   When a wagon just completed a delivery (JustDelivered flag is set),
    ///   this entry scans active stationary requesters within a configurable
    ///   radius of the wagon's current world position.  The nearest eligible
    ///   requester's active requests are claimed for the wagon, then this
    ///   entry returns null so the search continues.  LogisticsProxy fires next, finds the
    ///   newly-assigned requests, and returns a real logistics task — the wagon
    ///   never parks.
    ///
    ///   If no suitable requester is found, or if return-trip is disabled, this
    ///   entry is a no-op and the wagon parks normally.
    ///
    /// CLEAN-UP:
    ///   Claims are tracked in WagonEnhancementData and released when a route
    ///   is built, when the wagon parks, on mode change, or when they go stale
    ///   (see WagonEnhancementData.ReleaseClaims).
    /// </summary>
    public class ReturnTripSearchEntry : TaskSearchEntry
    {
        private readonly TransportWagon _wagon;
        private readonly WagonEnhancementData _data;
        private readonly HashSet<LogisticsRequester> _assignedBuffer = new HashSet<LogisticsRequester>();

        // Priority modifier higher than LogisticsProxy (2) ensures this entry
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

            // Every drop-off of a multi-stop route sets JustDelivered. Wait
            // until the haul is over: the final drop-off sets it again, and
            // that's when a return trip matters. (Claiming mid-route piled up
            // claims the wagon never used.)
            if (ClaimHelpers.IsHauling(currentHighestPriorityTask))
                return null;

            // Driver must still be inside the wagon.
            if (_wagon.driver.IsNull())
                return null;

            // Don't stack claims; retry next tick while an earlier one is pending.
            if (!ClaimHelpers.ReadyToClaim(_wagon, _data))
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
                ManifestDeliveryMod.LogVerbose(
                    $"[MD] ReturnTrip EMPTY ({diagMode}): {_wagon.name} " +
                    $"— no candidates in {diagRadius:F0}u around " +
                    $"{(diagShopAnchored ? "shop" : "wagon")} at " +
                    $"({diagCenter.x:F0},{diagCenter.z:F0})");
                return null;   // nothing nearby → fall through to ParkWagon
            }

            // ── Claim the requester's active requests for this wagon ─────────
            //    Per request, not per requester (see WagonEnhancementData), so
            //    they become visible to the LogisticsProxySearchEntry that fires
            //    next and are released once it builds a route.
            try
            {
                _data.ClaimRequester(_wagon, best);

                if (ManifestDeliveryMod.IsVerbose)
                {
                    string items = DescribeMoveOut(best);
                    float distFromShop = diagShopAnchored
                        ? Vector3.Distance(diagCenter, best.transform.position)
                        : -1f;
                    string shopDistStr = distFromShop >= 0f
                        ? $", {distFromShop:F0}u from shop"
                        : "";
                    ManifestDeliveryMod.LogVerbose(
                        $"[MD] ReturnTrip CLAIM ({diagMode}): {_wagon.name} → " +
                        $"{best.gameObject.name} " +
                        $"[{items}] " +
                        $"({Vector3.Distance(_wagon.transform.position, best.transform.position):F1}u from wagon" +
                        $"{shopDistStr})");
                }
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
            var assignedRequesters = ClaimHelpers.CollectAssignedRequesters(_wagon, _assignedBuffer);

            foreach (LogisticsRequester requester in aggregator.activeStationaryRequestsRO)
            {
                // Skip requesters that have no active work left.
                if (!requester.hasActiveRequests) continue;

                // Distance filter — measured from search center (shop in Camp, wagon otherwise).
                float distSqr = (requester.transform.position - searchCenter).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip if the wagon is already assigned to this requester —
                // vanilla already routes the wagon there (storage quota work).
                if (assignedRequesters.Contains(requester)) continue;

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
                ManifestDeliveryMod.LogVerbose(
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
            return GetAggregatorPublic(gm);
        }

        /// <summary>
        /// Shared with CampHaulSearchEntry. Reflection-only because the
        /// accessor on GameManager has changed at least twice now (vanilla
        /// 'logisiticsAggregator' field with the typo, then non-public, then
        /// the DLC moved/renamed it again). We do a *type-based* scan:
        /// look at every instance field and property on GameManager and
        /// return the first one whose value is a LogisticsAggregator. The
        /// resolved member is cached so we only walk reflection metadata once.
        /// </summary>
        internal static LogisticsAggregator? GetAggregatorPublic(GameManager gm)
        {
            // Fast path — once resolved, just call through.
            if (_aggregatorGetter != null)
            {
                try { return _aggregatorGetter(gm); }
                catch { _aggregatorGetter = null; /* re-resolve below */ }
            }
            if (_aggregatorLookupAttempted && _aggregatorGetter == null)
                return null;

            _aggregatorLookupAttempted = true;
            var type = typeof(GameManager);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            // 1) Try known field names first (fast, matches old paths).
            foreach (var name in new[] { "logisiticsAggregator", "logisticsAggregator",
                                         "aggregator", "Aggregator",
                                         "logisticsAggregatorInstance" })
            {
                var f = type.GetField(name, flags);
                if (f != null && typeof(LogisticsAggregator).IsAssignableFrom(f.FieldType))
                {
                    var ff = f;
                    _aggregatorGetter = g => ff.GetValue(g) as LogisticsAggregator;
                    ManifestDeliveryMod.Log.Msg($"[MD] LogisticsAggregator resolved via field GameManager.{name}");
                    return _aggregatorGetter(gm);
                }
                var p = type.GetProperty(name, flags);
                if (p != null && typeof(LogisticsAggregator).IsAssignableFrom(p.PropertyType)
                    && p.GetGetMethod(true) != null)
                {
                    var pp = p;
                    _aggregatorGetter = g => pp.GetValue(g, null) as LogisticsAggregator;
                    ManifestDeliveryMod.Log.Msg($"[MD] LogisticsAggregator resolved via property GameManager.{name}");
                    return _aggregatorGetter(gm);
                }
            }

            // 2) Type-based scan — any field or property whose value is a
            //    LogisticsAggregator. Survives further renames as long as the
            //    type itself doesn't change.
            foreach (var f in type.GetFields(flags))
            {
                if (typeof(LogisticsAggregator).IsAssignableFrom(f.FieldType))
                {
                    var ff = f;
                    _aggregatorGetter = g => ff.GetValue(g) as LogisticsAggregator;
                    ManifestDeliveryMod.Log.Msg($"[MD] LogisticsAggregator resolved by type-scan: field GameManager.{f.Name}");
                    return _aggregatorGetter(gm);
                }
            }
            foreach (var p in type.GetProperties(flags))
            {
                if (typeof(LogisticsAggregator).IsAssignableFrom(p.PropertyType)
                    && p.GetGetMethod(true) != null
                    && p.GetIndexParameters().Length == 0)
                {
                    var pp = p;
                    _aggregatorGetter = g => pp.GetValue(g, null) as LogisticsAggregator;
                    ManifestDeliveryMod.Log.Msg($"[MD] LogisticsAggregator resolved by type-scan: property GameManager.{p.Name}");
                    return _aggregatorGetter(gm);
                }
            }

            // 3) Last resort — look for the aggregator as a scene object.
            //    Some game refactors detach singletons from GameManager.
            var sceneInstance = UnityEngine.Object.FindObjectOfType<LogisticsAggregator>();
            if (sceneInstance != null)
            {
                var captured = sceneInstance;
                _aggregatorGetter = _ => captured;
                ManifestDeliveryMod.Log.Msg($"[MD] LogisticsAggregator resolved via FindObjectOfType (scene singleton).");
                return captured;
            }

            ManifestDeliveryMod.Log.Warning(
                "[MD] LogisticsAggregator could not be resolved by name, type-scan, or scene lookup. " +
                "Backhaul/CampHaul disabled until game restart resolves it.");
            return null;
        }

        private static System.Func<GameManager, LogisticsAggregator?>? _aggregatorGetter;
        private static bool _aggregatorLookupAttempted;
    }
}
