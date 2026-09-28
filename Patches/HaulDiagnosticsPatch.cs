using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// DIAGNOSTIC (feasibility Approach A — instrumentation only, no behavior
    /// change). When <see cref="ManifestDeliveryMod.HaulDiagnostics"/> is on,
    /// dumps the full shape of every <c>LogisticsTask</c> a TransportWagon
    /// receives, the moment the task is fully built:
    ///
    ///   • how many pickup (TakeOut) vs dropoff (Deliver) stops/actions,
    ///   • each item, count, and the source/destination building name,
    ///   • each served request's throttle params — maxTripsPerQuery,
    ///     maxItemCountPerTrip, minItemCountForBulkTransport,
    ///   • the wagon's carry capacity (and total weight this load uses),
    ///   • a one-line "limiter hint".
    ///
    /// Purpose: answer empirically whether a wagon already chains multiple
    /// source pickups into one load, and if it stops at one, WHICH limiter is
    /// biting — a single-building claim (the MD nudge), the trip cap, per-trip
    /// cap, or carry capacity. The vanilla route solver
    /// (LogisticsGlobalTaskSearch.FindBestRoute*) accumulates multiple source
    /// pickups up to carry capacity for a single request, so this tells us if
    /// there is a real gap before any feature is written.
    ///
    /// Hook: <c>LogisticsTask.OnSearchSuccess</c> — fires exactly once when the
    /// task is fully assembled (it already iterates every subtask). Gated on the
    /// pref and on the receiver being a TransportWagon, so it is near-zero cost
    /// when off and ignores all villager hauls.
    /// </summary>
    [HarmonyPatch(typeof(LogisticsTask), "OnSearchSuccess")]
    internal static class HaulDiagnosticsPatch
    {
        // Task.subTasks is `protected List<SubTask>` — read it via its getter so
        // we see the exact list OnSearchSuccess just iterated. subTasksRO is the
        // public fallback if the getter can't be resolved on some build.
        private static readonly MethodInfo? _subTasksGetter =
            AccessTools.PropertyGetter(typeof(Task), "subTasks");

        // The same task can reach OnSearchSuccess twice (observed 2026-09-28:
        // every haul logged twice at the same millisecond), so remember the
        // last one dumped.
        private static LogisticsTask? _lastDumped;

        private static void Postfix(LogisticsTask __instance)
        {
            try
            {
                if (ManifestDeliveryMod.HaulDiagnostics == null || !ManifestDeliveryMod.HaulDiagnostics.Value)
                    return;
                if (!(__instance.assignedReceiver is TransportWagon wagon) || wagon == null)
                    return;
                if (ReferenceEquals(__instance, _lastDumped))
                    return;
                _lastDumped = __instance;

                IEnumerable? subs = null;
                if (_subTasksGetter != null)
                    subs = _subTasksGetter.Invoke(__instance, null) as IEnumerable;
                if (subs == null)
                    subs = __instance.subTasksRO as IEnumerable;
                if (subs == null)
                    return;

                int subCount = 0, pickupStops = 0, dropoffStops = 0, pickupActions = 0, dropoffActions = 0;
                uint totalPickedUp = 0;
                float totalWeight = 0f;
                string reqParams = "n/a";
                var lines = new List<string>();

                var gm = UnitySingleton<GameManager>.Instance;
                var wbm = gm != null ? gm.workBucketManager : null;

                foreach (var o in subs)
                {
                    subCount++;
                    if (!(o is LogisticsDestinationSubTask dst)) continue;
                    var actions = dst.actionsToPerform;
                    if (actions == null) continue;

                    bool stHasPickup = false, stHasDrop = false;
                    foreach (var a in actions)
                    {
                        string item = a.itemID.ToString();
                        string where = DescribeContainer(a.storageForAction) + PriorityTag(a.storageForAction, a.itemID);

                        if (a.action == ItemAction.TakeOut)
                        {
                            pickupActions++;
                            stHasPickup = true;
                            totalPickedUp += a.itemCount;
                            totalWeight += a.itemCount * ItemWeight(wbm, a.itemID);
                            lines.Add($"[MD][Diag]   PICKUP  {item} x{a.itemCount}  from '{where}'{ReqTag(a.itemRequestActionIsFor)}");
                        }
                        else
                        {
                            dropoffActions++;
                            stHasDrop = true;
                            lines.Add($"[MD][Diag]   DROPOFF {item} x{a.itemCount}  to   '{where}'{ReqTag(a.itemRequestActionIsFor)}");
                        }

                        if (reqParams == "n/a" && a.itemRequestActionIsFor != null)
                            reqParams = ReqParams(a.itemRequestActionIsFor);
                    }
                    if (stHasPickup) pickupStops++;
                    if (stHasDrop) dropoffStops++;
                }

                // Only dump haul-shaped tasks (some logistics tasks may be empty).
                if (pickupActions == 0 && dropoffActions == 0) return;

                string mode = "?";
                var data = wagon.GetComponent<WagonEnhancementData>();
                if (data != null && data.ShopEnhancement != null) mode = data.ShopEnhancement.ModeDisplayName;

                float cap = wagon.GetCarryCapacity();
                float free = wagon.temporaryInventory != null
                    ? wagon.temporaryInventory.GetWeightCurrentlyAvailable() : -1f;

                ManifestDeliveryMod.Log.Msg($"[MD][Diag] ───── Haul built: {wagon.name} ({mode}) ─────");
                ManifestDeliveryMod.Log.Msg($"[MD][Diag]   carry capacity: {cap:F0}  (free now: {free:F0})");
                ManifestDeliveryMod.Log.Msg(
                    $"[MD][Diag]   {subCount} subtask(s) | pickups: {pickupActions} action(s)/{pickupStops} stop(s)" +
                    $"  dropoffs: {dropoffActions} action(s)/{dropoffStops} stop(s)");
                foreach (var l in lines) ManifestDeliveryMod.Log.Msg(l);
                ManifestDeliveryMod.Log.Msg(
                    $"[MD][Diag]   loaded {totalPickedUp} item(s) ≈ {totalWeight:F0} weight vs cap {cap:F0}" +
                    $"  | served-request: {reqParams}");
                ManifestDeliveryMod.Log.Msg($"[MD][Diag]   limiter hint: {LimiterHint(pickupStops, totalWeight, cap)}");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][Diag] Haul diagnostic failed: {ex.Message}");
            }
        }

        private static float ItemWeight(WorkBucketManager? wbm, ItemID id)
        {
            try
            {
                if (wbm != null && wbm.itemByItemIDRO != null)
                {
                    var def = wbm.itemByItemIDRO[id];
                    if (def != null) return def.weight;
                }
            }
            catch { /* unknown item — weight unavailable */ }
            return 0f;
        }

        private static string ReqParams(ItemRequest r) =>
            $"maxTrips={r.maxTripsPerQuery} maxPerTrip={r.maxItemCountPerTrip} bulkMin={r.minItemCountForBulkTransport} ({r.GetType().Name})";

        private static string ReqTag(ItemRequest r)
        {
            if (r == null) return "";
            return $"   [req {r.action} for '{DescribeContainer(r.storageForAction)}' {r.requestTag}" +
                   $" | maxTrips={r.maxTripsPerQuery} maxPerTrip={r.maxItemCountPerTrip} bulkMin={r.minItemCountForBulkTransport}]";
        }

        /// <summary>
        /// " p8" when the building is a storage with a Storage Priorities
        /// setting that differs from vanilla for this item. Reads the component
        /// only — never attaches one.
        /// </summary>
        private static string PriorityTag(IContainsItems c, ItemID itemID)
        {
            try
            {
                var owner = OwnerOf(c);
                if (owner == null) return "";
                var data = owner.GetComponent<StoragePriorityData>();
                if (data == null || !data.HasAnyPriority) return "";
                int p = data.GetPriority((int)itemID);
                return p == StoragePriorityData.DefaultPriority ? "" : $" p{p}";
            }
            catch { return ""; }
        }

        private static string LimiterHint(int pickupStops, float loadedWeight, float cap)
        {
            if (pickupStops <= 1)
                return "ONE pickup stop — the served request targets a single building/storage. " +
                       "Multi-BUILDING batching is the new-feature case (vanilla won't merge distinct requests).";
            if (cap > 0f && loadedWeight >= cap * 0.95f)
                return "load ≈ carry capacity — capacity-bound (more capacity = more per load).";
            return "multiple pickups, under capacity — bound by available supply or per-trip/trip caps " +
                   "(see served-request params above).";
        }

        /// <summary>
        /// IContainsItems may be a building MonoBehaviour or a plain ItemStorage.
        /// Mirror DeliveryLogPatches' container-naming so logs read consistently.
        /// </summary>
        /// <summary>
        /// Building name plus rounded map position, so two buildings of the same
        /// type (every stockyard is just "Stockyard") can be told apart.
        /// </summary>
        private static string DescribeContainer(IContainsItems c)
        {
            if (c == null) return "?";
            var owner = OwnerOf(c);
            if (owner == null) return c is ItemStorage ? "(storage)" : c.GetType().Name;
            Vector3 p = owner.transform.position;
            return $"{owner.gameObject.name}@{p.x:F0},{p.z:F0}";
        }

        private static Component? OwnerOf(IContainsItems c)
        {
            if (c == null) return null;
            if (c is MonoBehaviour mb && mb != null) return mb;
            if (c is ItemStorage st && st.reservableItemStorageOwner is Component owner && owner != null)
                return owner;
            return null;
        }
    }
}
