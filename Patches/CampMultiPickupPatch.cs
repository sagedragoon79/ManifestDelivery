using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;
using ManifestDelivery.Tasks;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Camp Multi-Pickup (experimental; design in _handoffs/2026-09-28_deep-dive.md §3).
    ///
    /// Vanilla plans a Camp wagon's haul as ONE producer → ONE storage: the
    /// route solver commits one single-source TakeOut route per query. This
    /// postfix runs the moment that route is built, before the wagon sets off,
    /// and adds pickup stops at other camp producers of the same item plus
    /// matching drop-off actions at the same storage, so one trip carries a
    /// fuller load.
    ///
    /// WHY THIS IS SAFE (verified in the decompile, 2026-09-28):
    /// - A TakeOut pickup holds only an ItemReservation at the source and a
    ///   StorageSpaceReservation at the destination. Vanilla makes no
    ///   request-level reservation for TakeOut requests (ProcessRouteInternal),
    ///   so MD makes exactly those two, with the same helpers.
    /// - A producer's move-out count is recomputed from its storage's UNRESERVED
    ///   items, so other haulers immediately see less to take.
    /// - Stops run in list order (GetNextSubTaskToStart), and
    ///   LogisticsDestinationSubTask.CancelReservations releases every action's
    ///   reservations if the trip aborts — MD's included.
    /// Routing and scoring are untouched: the destination is the one vanilla
    /// chose, so Storage Priorities' anti-ping-pong contract still holds.
    ///
    /// Extra stops go BEFORE the original pickup. Vanilla's grab-while-pending
    /// (LogisticsTask.GrabAddedItemsWhilePendingTakeOut) sums the load of every
    /// earlier stop to find the room left, so the extra load must come first or
    /// the wagon could be overfilled at the original stop.
    /// </summary>
    [HarmonyPatch(typeof(LogisticsTask), "OnSearchSuccess")]
    internal static class CampMultiPickupPatch
    {
        private static readonly object Marker = new object();
        private static readonly ConditionalWeakTable<LogisticsTask, object> _processed =
            new ConditionalWeakTable<LogisticsTask, object>();
        private static readonly MethodInfo? _subTasksGetter =
            AccessTools.PropertyGetter(typeof(Task), "subTasks");
        private static bool _loggedFailure;

        /// <summary>Smallest extra stop worth the detour, as a share of carry capacity.</summary>
        private const float MinStopShareOfCapacity = 0.05f;

        private struct Candidate
        {
            public SingleItemRequest Request;
            public ReservableItemStorage Storage;
            public Vector3 Position;
            public float DistSqrFromPickup;
        }

        private static readonly List<ProducerStock> _stock = new List<ProducerStock>();

        // Before HaulDiagnostics, so its dump shows the finished route.
        [HarmonyPriority(Priority.High)]
        private static void Postfix(LogisticsTask __instance)
        {
            try
            {
                var pref = ManifestDeliveryMod.CampMultiPickup;
                if (pref == null || !pref.Value) return;
                if (!(__instance.assignedReceiver is TransportWagon wagon) || wagon == null) return;
                var data = wagon.GetComponent<WagonEnhancementData>();
                var shop = data != null ? data.ResolveShopEnhancement(wagon) : null;
                if (shop == null || shop.Mode != ShopMode.Camp) return;

                // The same task can reach OnSearchSuccess twice; augment it once.
                if (_processed.TryGetValue(__instance, out _)) return;
                _processed.Add(__instance, Marker);

                AddPickups(__instance, wagon, shop);
            }
            catch (Exception ex)
            {
                if (_loggedFailure) return;
                _loggedFailure = true;
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] Camp multi-pickup failed (logged once): {ex.Message}");
            }
        }

        private static void AddPickups(LogisticsTask task, TransportWagon wagon, WagonShopEnhancement shop)
        {
            if (!(_subTasksGetter?.Invoke(task, null) is List<SubTask> subTasks)) return;

            // ── Shape: exactly one pickup stop, then exactly one drop-off stop ──
            LogisticsDestinationSubTask? pickup = null;
            LogisticsDestinationSubTask? dropOff = null;
            foreach (SubTask sub in subTasks)
            {
                if (!(sub is LogisticsDestinationSubTask stop)) return;
                bool takes = false, delivers = false;
                foreach (var action in stop.actionsToPerform)
                {
                    if (action.action == ItemAction.TakeOut) takes = true;
                    else if (action.action == ItemAction.Deliver) delivers = true;
                    else return;
                }
                if (takes == delivers) return;                  // mixed or empty stop
                if (takes)
                {
                    if (pickup != null || dropOff != null) return;
                    pickup = stop;
                }
                else
                {
                    if (dropOff != null) return;
                    dropOff = stop;
                }
            }
            if (pickup == null || dropOff == null) return;

            // ── The pickup must be a camp producer emptying its own output ──
            ItemRequest? firstRequest = null;
            var items = new List<ItemID>();
            foreach (var action in pickup.actionsToPerform)
            {
                var request = action.itemRequestActionIsFor;
                if (request == null || request.action != ItemAction.TakeOut) return;
                if (action.storageForAction != request.storageForAction) return;
                if (firstRequest == null) firstRequest = request;
                if (!items.Contains(action.itemID)) items.Add(action.itemID);
            }
            LogisticsRequester? pickupRequester = firstRequest?.requester;
            var pickupOwner = pickupRequester?.owner as Component;
            if (pickupOwner == null || pickupOwner is StorageBuilding) return;

            Vector3 shopPos = shop.transform.position;
            Vector3 pickupPos = pickupOwner.transform.position;
            float radius = shop.WorkRadius;
            if ((pickupPos - shopPos).sqrMagnitude > radius * radius) return;

            // ── The drop-off must be one storage building ──
            IContainsItems? destination = null;
            foreach (var action in dropOff.actionsToPerform)
            {
                if (destination == null) destination = action.storageForAction;
                else if (!ReferenceEquals(action.storageForAction, destination)) return;
            }
            var destinationStorage = destination as ReservableItemStorage;
            var storage = destinationStorage != null ? destinationStorage.GetComponent<StorageBuilding>() : null;
            if (destinationStorage == null || storage == null || storage.storageSpaceReservationManager == null) return;

            var gm = UnitySingleton<GameManager>.Instance;
            var wbm = gm != null ? gm.workBucketManager : null;
            if (wbm == null) return;

            // ── Budgets ──
            float capacity = wagon.GetCarryCapacity();
            float plannedWeight = 0f;
            foreach (var action in pickup.actionsToPerform)
                plannedWeight += action.itemCount * ClaimHelpers.ItemWeight(action.itemID);
            uint space = storage.GetAvailableStorageSpace(out _);   // already net of vanilla's reservation
            float minStopWeight = capacity * MinStopShareOfCapacity;

            int maxStops = ManifestDeliveryMod.CampMultiPickupMaxStops != null
                ? Mathf.Max(0, ManifestDeliveryMod.CampMultiPickupMaxStops.Value) : 3;
            float detour = ManifestDeliveryMod.CampMultiPickupDetour != null
                ? ManifestDeliveryMod.CampMultiPickupDetour.Value : 80f;
            float detourSqr = detour * detour;

            var added = new List<KeyValuePair<LogisticsDestinationSubTask, Vector3>>();
            var candidates = new List<Candidate>();
            uint extraItems = 0;

            foreach (ItemID itemID in items)
            {
                if (added.Count >= maxStops) break;
                float unitWeight = ClaimHelpers.ItemWeight(itemID);
                if (unitWeight <= 0f) continue;
                if (!wbm.itemByItemIDRO.TryGetValue(itemID, out Item item) || item == null) continue;

                // Room left under the destination's max quota for this item, net
                // of what this trip already plans to drop there.
                uint quotaRoom = uint.MaxValue;
                if (storage.quotaHandler != null
                    && storage.quotaHandler.GetMaxQuotaCountLimit(itemID, out uint quotaLimit))
                {
                    uint planned = 0;
                    foreach (var action in dropOff.actionsToPerform)
                        if (action.itemID == itemID) planned += action.itemCount;
                    quotaRoom = quotaLimit > planned ? quotaLimit - planned : 0;
                }

                // Other camp producers with the same item waiting near the pickup —
                // full or not (see CampProducers for why partly filled ones count).
                candidates.Clear();
                CampProducers.Collect(shopPos, radius, _stock, itemID);
                foreach (var stock in _stock)
                {
                    if (stock.Requester == pickupRequester) continue;
                    float distSqr = (stock.Position - pickupPos).sqrMagnitude;
                    if (distSqr > detourSqr) continue;
                    if ((stock.Request.storageExclusionFlags & destinationStorage.logisticsStorageFlags) != 0) continue;
                    if (!(stock.Requester.owner is IRegistersForWork)) continue;
                    candidates.Add(new Candidate
                    {
                        Request = stock.Request, Storage = stock.Storage,
                        Position = stock.Position, DistSqrFromPickup = distSqr,
                    });
                }
                candidates.Sort((a, b) => a.DistSqrFromPickup.CompareTo(b.DistSqrFromPickup));

                foreach (var candidate in candidates)
                {
                    if (added.Count >= maxStops) break;
                    uint canCarry = (uint)Mathf.Max(0f, Mathf.Floor((capacity - plannedWeight) / unitWeight));
                    uint room = Math.Min(Math.Min(canCarry, space), quotaRoom);
                    if (room == 0) break;                          // full, or destination full

                    ReservableItemStorage source = candidate.Storage;
                    uint available = source.GetNumberOfUnreservedItems(item);
                    uint count = Math.Min(available, room);
                    if (count == 0 || count * unitWeight < minStopWeight) continue;   // not worth the detour

                    // Same two reservations vanilla makes for a TakeOut route.
                    ItemReservation? itemReservation =
                        ReservationHelpers.ReserveExactAmountOfItem(wagon, source, item, count);
                    if (itemReservation == null) continue;
                    StorageSpaceReservation? spaceReservation =
                        storage.storageSpaceReservationManager.ReserveStorageSpace(wagon, destinationStorage, count);
                    if (spaceReservation == null)
                    {
                        itemReservation.CancelReservation();
                        continue;
                    }

                    var stop = new LogisticsDestinationSubTask(task, (IRegistersForWork)candidate.Request.requester.owner);
                    stop.AddLogisticsAction(new LogisticsDestinationAction(
                        candidate.Request, ItemAction.TakeOut, itemID, count, itemReservation, null, null, source));
                    dropOff.AddLogisticsAction(new LogisticsDestinationAction(
                        candidate.Request, ItemAction.Deliver, itemID, count, null, null, spaceReservation, destinationStorage));

                    added.Add(new KeyValuePair<LogisticsDestinationSubTask, Vector3>(stop, candidate.Position));
                    plannedWeight += count * unitWeight;
                    space -= count;
                    if (quotaRoom != uint.MaxValue) quotaRoom -= count;
                    extraItems += count;
                }
            }

            if (added.Count == 0) return;

            // Travel order: nearest-neighbour from the wagon, ending at the
            // original pickup (inserted just before it — see class summary).
            var ordered = new List<SubTask>(added.Count);
            Vector3 cursor = wagon.transform.position;
            while (added.Count > 0)
            {
                int nearest = 0;
                float nearestSqr = float.MaxValue;
                for (int i = 0; i < added.Count; i++)
                {
                    float d = (added[i].Value - cursor).sqrMagnitude;
                    if (d < nearestSqr) { nearestSqr = d; nearest = i; }
                }
                ordered.Add(added[nearest].Key);
                cursor = added[nearest].Value;
                added.RemoveAt(nearest);
            }
            subTasks.InsertRange(subTasks.IndexOf(pickup), ordered);

            if (ManifestDeliveryMod.IsVerbose
                || (ManifestDeliveryMod.HaulDiagnostics != null && ManifestDeliveryMod.HaulDiagnostics.Value))
            {
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Camp multi-pickup: {wagon.name} +{ordered.Count} stop(s), +{extraItems} item(s) " +
                    $"→ {storage.gameObject.name} (load {plannedWeight:F0}/{capacity:F0})");
            }
        }
    }
}
