using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery;
using ManifestDelivery.Components;
using ManifestDelivery.Tasks;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// All Harmony patches that target TransportWagon.
    ///
    /// Patch summary
    /// ──────────────────────────────────────────────────────────────────────────
    ///  Start_Postfix              — adds WagonEnhancementData component.
    ///  SetupSearchEntries_Postfix — injects the ReturnTrip, CampHaul and HubHaul
    ///                              search entries into the wagon's task search list.
    ///  ItemBundleDroppedOff_Post  — sets JustDelivered = true when a delivery
    ///                              completes.
    ///  AssignedToWagonShop_Post   — caches the shop's WagonShopEnhancement on the
    ///                              wagon's data component and reapplies capacity.
    ///  UnAssignedFromWagonShop_Post — releases MD claims, clears the cached shop
    ///                              reference and reapplies capacity.
    ///  workerFlags_Get            — Hub "fire duty": strips
    ///                              IgnoreGloballyAssignedRequests. The only
    ///                              globally assigned requests in the game carry
    ///                              water to building fires.
    ///  ParkWagonThenIdleSubTask_Ctor — releases MD claims when the wagon parks.
    ///  LogisticsTask_OnSearchSuccess — releases MD claims once a route is built.
    /// </summary>
    [HarmonyPatch]
    internal static class TransportWagonPatches
    {
        // ── 1. Attach WagonEnhancementData on Start ───────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), "Start")]
        private static void Start_Postfix(TransportWagon __instance)
        {
            if (__instance.GetComponent<WagonEnhancementData>() == null)
                __instance.gameObject.AddComponent<WagonEnhancementData>();
        }

        // ── 2. Inject ReturnTripSearchEntry ───────────────────────────────────
        //
        //  Original SetupSearchEntries registers (in order):
        //    KickOut(10)  →  LogisticsProxy(0,max2)  →  ParkWagon(-10)
        //
        //  We postfix to add ReturnTrip(priority=3) so the task manager's
        //  priority-ordered evaluation fires it AFTER KickOut but BEFORE
        //  LogisticsProxy:
        //    KickOut(10)  →  ReturnTrip(3)  →  LogisticsProxy(0)  →  ParkWagon(-10)

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), "SetupSearchEntries")]
        private static void SetupSearchEntries_Postfix(TransportWagon __instance)
        {
            // Vanilla Start sets up search entries before our Start postfix
            // runs, so on load the component is usually not there yet. That's
            // expected (it logged a warning per wagon on every load).
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data == null)
                data = __instance.gameObject.AddComponent<WagonEnhancementData>();

            // Back-link to the shop's enhancement if we haven't already.
            // AssignedToWagonShop_Postfix normally handles this, but if it
            // fired before our data component existed (new wagon mid-save-load
            // or mid-session spawn), we catch it here.
            if (data.ShopEnhancement == null && __instance.wagonShop != null)
            {
                data.ShopEnhancement =
                    __instance.wagonShop.GetComponent<WagonShopEnhancement>();
                if (data.ShopEnhancement != null)
                    ManifestDeliveryMod.LogVerbose(
                        $"[MD] Back-linked {__instance.name} → " +
                        $"{__instance.wagonShop.gameObject.name} " +
                        $"({data.ShopEnhancement.Mode})");
            }

            GameManager? gm = UnitySingleton<GameManager>.Instance;
            if (gm == null)
            {
                ManifestDeliveryMod.Log.Warning("[MD] SetupSearchEntries: GameManager not ready.");
                return;
            }

            // Real order after the task manager's stable sort (see ClaimHelpers):
            //   KickOut(10) → ReturnTrip(3) → LogisticsProxy(2) → CampHaul(2) →
            //   HubHaul(1) → ParkWagon(-10)
            gm.defaultTaskManager.AddTaskSearchEntry(
                __instance,
                new ReturnTripSearchEntry(__instance, data));

            // Camp haul: priority 2 — ties with LogisticsProxy and sorts after it.
            gm.defaultTaskManager.AddTaskSearchEntry(
                __instance,
                new CampHaulSearchEntry(__instance, data));

            // Hub haul: priority 1. The proactive Hub distributor — serves any
            // delivery/move-out request within the Hub radius (markets,
            // shelters, producers, storages) instead of waiting on
            // opportunistic backhaul.
            gm.defaultTaskManager.AddTaskSearchEntry(
                __instance,
                new HubHaulSearchEntry(__instance, data));
        }

        // ── 3. Flag delivery completion ───────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.ItemBundleDroppedOff))]
        private static void ItemBundleDroppedOff_Postfix(TransportWagon __instance)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            data.JustDelivered = true;
        }

        // ── 4. Cache shop reference on assignment ─────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.AssignedToWagonShop))]
        private static void AssignedToWagonShop_Postfix(
            TransportWagon __instance,
            WagonShop      newWagonShopAssignedTo)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            data.ShopEnhancement = newWagonShopAssignedTo != null
                ? newWagonShopAssignedTo.GetComponent<WagonShopEnhancement>()
                : null;

            // Hub's +20% capacity comes from the shop's mode, so a newly built
            // or newly paired wagon needs its capacity recalculated here —
            // otherwise it only changed on a mode switch or a tech unlock.
            __instance.CalculateCarryCapacity();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.UnAssignedFromWagonShop))]
        private static void UnAssignedFromWagonShop_Postfix(TransportWagon __instance)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            data.ReleaseClaims(__instance, "left its shop");
            data.ShopEnhancement = null;
            __instance.CalculateCarryCapacity();
        }

        // ── 5. Hub mode: fire duty (remove IgnoreGloballyAssignedRequests) ────
        //
        //  TransportWagon.workerFlags is a property (get-only).  We patch its
        //  getter so that Hub-mode wagons drop the flag and join the global
        //  request pool. The ONLY globally assigned requests in the game are
        //  the water requests of burning buildings (ExtinguishFireResource,
        //  decompile L60827), so this makes Hub wagons help carry water to
        //  fires. It adds no general hauling — Hub distribution comes from
        //  HubHaulSearchEntry. (An earlier comment claimed it opened "all global
        //  requests"; verified 2026-09-28 that it doesn't.)
        //
        //  Note: Harmony patches property getters by their backing method name.

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), "get_workerFlags")]
        private static void workerFlags_Postfix(
            TransportWagon        __instance,
            ref LogisticsWorkerFlags __result)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data?.ShopEnhancement == null) return;

            if (!data.ShopEnhancement.IgnoresGlobalRequests)
            {
                // Hub mode: clear IgnoreGloballyAssignedRequests (bit 2) — fire duty.
                __result &= ~LogisticsWorkerFlags.IgnoreGloballyAssignedRequests;
            }
        }

        // ── 6. Release MD claims when parking ─────────────────────────────────
        //
        //  ParkWagonThenIdleSubTask is constructed when ParkWagonSearchEntry
        //  wins the task search (LogisticsProxy found nothing).  Any claim MD
        //  made didn't produce a route, so release it here. (Before 2026-09-28
        //  this only released the legacy Hub claim, so a multi-source Hub claim
        //  leaked its assignment AND its herd-guard count, which then kept every
        //  Hub wagon off that request until reload.)

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ParkWagonThenIdleSubTask), MethodType.Constructor,
            new System.Type[] { typeof(Task) })]
        private static void ParkWagonSubTask_Ctor_Postfix(ParkWagonThenIdleSubTask __instance)
        {
            // Retrieve the wagon from the sub-task's owning task receiver.
            if (__instance.owningTask?.assignedReceiver is not TransportWagon wagon) return;

            WagonEnhancementData? data = wagon.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            data.ReleaseClaims(wagon, "parked — no route");

            // Also ensure JustDelivered is cleared in case the ReturnTrip entry
            // somehow didn't fire (e.g. game loaded mid-task).
            data.JustDelivered = false;
        }

        // ── 7. Release MD claims once a route is built ─────────────────────
        //
        //  Vanilla only reads assignments while SEARCHING for a route
        //  (LogisticsGlobalTaskSearch.HasValidRequests / ProcessRequestsAssigned-
        //  ToWorker). Once OnSearchSuccess fires, the task holds its own item and
        //  storage-space reservations, so the claims have done their job and are
        //  released. Unassigning does not cancel the task (verified 2026-09-28:
        //  LogisticsAssignment.OnWorkerUnassignedFromRequest only edits a
        //  dictionary). The previous hook cleared MD's tracking WITHOUT
        //  unassigning, which is how claims piled up for a whole session.

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LogisticsTask), "OnSearchSuccess")]
        private static void LogisticsTask_OnSearchSuccess_Postfix(LogisticsTask __instance)
        {
            if (!(__instance.assignedReceiver is TransportWagon wagon) || wagon == null) return;
            WagonEnhancementData? data = wagon.GetComponent<WagonEnhancementData>();
            data?.ReleaseClaims(wagon, "route built");
        }

        // ── 8. Mode-based speed modifier ─────────────────────────────────────
        //
        //  Camp mode: +25% speed (long hauls on open roads)
        //  Hub mode:  -10% speed (heavy loads, short trips)
        //  Standard:  no change

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), "get_movementSpeed")]
        private static void movementSpeed_Postfix(
            TransportWagon __instance,
            ref float      __result)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data?.ShopEnhancement == null) return;

            float multiplier = data.ShopEnhancement.Mode switch
            {
                Components.ShopMode.Camp => 1.25f,   // +25% speed
                Components.ShopMode.Hub  => 0.90f,   // -10% speed
                _                        => 1.0f,
            };

            if (multiplier != 1.0f)
                __result *= multiplier;
        }

        // ── 9. Mode-based capacity modifier ──────────────────────────────────
        //
        //  Hub mode: +20% carry capacity (bulk hauler)
        //  Other modes: no change
        //
        //  Runs after CalculateCarryCapacity sets the base + tech multiplier.

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.CalculateCarryCapacity))]
        private static void CalculateCarryCapacity_Postfix(TransportWagon __instance)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data?.ShopEnhancement == null) return;

            if (data.ShopEnhancement.Mode == Components.ShopMode.Hub)
            {
                // Apply +20% on top of the already-calculated capacity.
                // TransportWagon.temporaryInventory is public; ItemStorage.carryCapacity
                // is a public property with setter. No reflection needed.
                var inv = __instance.temporaryInventory;
                if (inv != null)
                    inv.carryCapacity *= 1.20f;
            }
        }
    }
}
