using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ManifestDelivery.Tasks
{
    /// <summary>One camp producer's waiting output of one item.</summary>
    internal struct ProducerStock
    {
        public LogisticsRequester Requester;
        public SingleItemRequest Request;
        public ReservableItemStorage Storage;
        public Vector3 Position;
        public ItemID Item;
        public uint Unreserved;
    }

    /// <summary>
    /// Finds output waiting at camp producers, whether or not the producer has
    /// asked for a pickup yet.
    ///
    /// A producer only posts (activates) its move-out request once its output
    /// storage is FULL (Building.ShouldTransferProducedItemCount: count >=
    /// capacity), and wagons clear full producers almost at once — so two
    /// producers of the same item are rarely both "requesting" at the same
    /// moment, and Camp Multi-Pickup almost never found a partner. Partly
    /// filled producers keep their request in LogisticsRequester's protected
    /// moveOutItemRequests (all requests, active or not) and aren't listed in
    /// the aggregator's active requesters, so this walks the game's building
    /// list instead.
    ///
    /// Taking output from a producer that isn't full yet is safe: the pickup
    /// holds an ItemReservation on the producer's storage, and during the trip
    /// the request is only a label (ReportItemsPickedUpForRequest is empty).
    /// </summary>
    internal static class CampProducers
    {
        private static readonly AccessTools.FieldRef<LogisticsRequester, Dictionary<LogisticsRequestID, ItemRequest>>? _moveOutRequests =
            ResolveMoveOutRequests();
        private static bool _loggedFallback;

        private static AccessTools.FieldRef<LogisticsRequester, Dictionary<LogisticsRequestID, ItemRequest>>? ResolveMoveOutRequests()
        {
            try
            {
                return AccessTools.FieldRefAccess<LogisticsRequester, Dictionary<LogisticsRequestID, ItemRequest>>(
                    "moveOutItemRequests");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with every non-storage building inside
        /// the circle that has unreserved output waiting. Pass
        /// <paramref name="onlyItem"/> to collect a single item.
        /// </summary>
        internal static void Collect(Vector3 center, float radius, List<ProducerStock> buffer, ItemID? onlyItem = null)
        {
            buffer.Clear();
            var gm = UnitySingleton<GameManager>.Instance;
            var buildings = gm != null && gm.resourceManager != null ? gm.resourceManager.allBuildingsRO : null;
            var wbm = gm != null ? gm.workBucketManager : null;
            if (buildings == null || wbm == null) return;

            float radiusSqr = radius * radius;
            foreach (Building building in buildings)
            {
                if (building == null || building is StorageBuilding) continue;
                Vector3 pos = building.transform.position;
                if ((pos - center).sqrMagnitude > radiusSqr) continue;
                var requester = building.logisticsRequester;
                if (requester == null) continue;

                var requests = MoveOutRequests(requester);
                if (requests == null) continue;
                foreach (var kv in requests)
                {
                    if (!(kv.Value is SingleItemRequest request) || request.action != ItemAction.TakeOut) continue;
                    if (onlyItem.HasValue && request.itemID != onlyItem.Value) continue;
                    if (!(request.storageForAction is ReservableItemStorage storage) || storage == null) continue;
                    if (!wbm.itemByItemIDRO.TryGetValue(request.itemID, out Item item) || item == null) continue;
                    uint unreserved = storage.GetNumberOfUnreservedItems(item);
                    if (unreserved == 0) continue;
                    buffer.Add(new ProducerStock
                    {
                        Requester = requester, Request = request, Storage = storage,
                        Position = pos, Item = request.itemID, Unreserved = unreserved,
                    });
                }
            }
        }

        /// <summary>All of a building's move-out requests, active or not (active only if the field is missing).</summary>
        private static IEnumerable<KeyValuePair<LogisticsRequestID, ItemRequest>>? MoveOutRequests(LogisticsRequester requester)
        {
            if (_moveOutRequests != null)
            {
                try { return _moveOutRequests(requester); }
                catch { /* fall through */ }
            }
            if (!_loggedFallback)
            {
                _loggedFallback = true;
                ManifestDeliveryMod.Log.Warning(
                    "[MD] LogisticsRequester.moveOutItemRequests not readable — Camp Multi-Pickup only sees full producers.");
            }
            return requester.activeMoveOutRequests;
        }
    }
}
