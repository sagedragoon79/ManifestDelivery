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
    ///  SetupSearchEntries_Postfix — injects ReturnTripSearchEntry into the wagon's
    ///                              task search list.
    ///  ItemBundleDroppedOff_Post  — sets JustDelivered = true when a delivery
    ///                              completes, recording the drop-off position.
    ///  AssignedToWagonShop_Post   — caches the shop's WagonShopEnhancement on the
    ///                              wagon's data component.
    ///  UnAssignedFromWagonShop_Post — clears the cached shop reference.
    ///  workerFlags_Get            — for Hub-mode wagons, strips
    ///                              IgnoreGloballyAssignedRequests so they participate
    ///                              in the global request pool permanently.
    ///  ParkWagonThenIdleSubTask_Ctor — cleans up any pending temporary assignment
    ///                              when the wagon decides to park (return-trip found
    ///                              nothing useful).
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
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data == null)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] SetupSearchEntries: WagonEnhancementData missing on " +
                    $"{__instance.name}, adding now.");
                data = __instance.gameObject.AddComponent<WagonEnhancementData>();
            }

            GameManager? gm = UnitySingleton<GameManager>.Instance;
            if (gm == null)
            {
                ManifestDeliveryMod.Log.Warning("[MD] SetupSearchEntries: GameManager not ready.");
                return;
            }

            gm.defaultTaskManager.AddTaskSearchEntry(
                __instance,
                new ReturnTripSearchEntry(__instance, data));

            // Camp haul: priority 2, fires after ReturnTrip(3) but before LogisticsProxy(0)
            gm.defaultTaskManager.AddTaskSearchEntry(
                __instance,
                new CampHaulSearchEntry(__instance, data));
        }

        // ── 3. Flag delivery completion ───────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.ItemBundleDroppedOff))]
        private static void ItemBundleDroppedOff_Postfix(TransportWagon __instance)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            data.JustDelivered        = true;
            data.LastDeliveryPosition = __instance.transform.position;
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
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.UnAssignedFromWagonShop))]
        private static void UnAssignedFromWagonShop_Postfix(TransportWagon __instance)
        {
            WagonEnhancementData? data = __instance.GetComponent<WagonEnhancementData>();
            if (data != null)
                data.ShopEnhancement = null;
        }

        // ── 5. Hub mode: remove IgnoreGloballyAssignedRequests ────────────────
        //
        //  TransportWagon.workerFlags is a property (get-only).  We patch its
        //  getter so that Hub-mode wagons permanently drop the flag and compete
        //  for all global requests, turning them into high-priority bulk porters.
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
                // Hub mode: clear IgnoreGloballyAssignedRequests (bit 2).
                __result &= ~LogisticsWorkerFlags.IgnoreGloballyAssignedRequests;
            }
        }

        // ── 6. Clean up temporary assignment when parking ─────────────────────
        //
        //  ParkWagonThenIdleSubTask is constructed when ParkWagonSearchEntry
        //  wins the task search (LogisticsProxy found nothing even after our
        //  ReturnTripSearchEntry ran).  We clean up the temporary requester
        //  assignment at this point so it doesn't linger on the wagon.
        //
        //  Because ParkWagonThenIdleSubTask is an inner class, we identify it
        //  by its constructor's declaring type name rather than a typeof().

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ParkWagonThenIdleSubTask), MethodType.Constructor,
            new System.Type[] { typeof(Task) })]
        private static void ParkWagonSubTask_Ctor_Postfix(ParkWagonThenIdleSubTask __instance)
        {
            // Retrieve the wagon from the sub-task's owning task receiver.
            if (__instance.owningTask?.assignedReceiver is not TransportWagon wagon) return;

            WagonEnhancementData? data = wagon.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            // If there's a lingering temporary assignment, release it.
            if (data.TemporaryRequester != null)
            {
                data.ClearTemporaryAssignment(wagon);
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] ReturnTrip: no logistics work found near drop-off for " +
                    $"{wagon.name}, parking and releasing temp assignment.");
            }

            // Clean up camp haul assignment too
            if (data.CampHaulRequester != null)
            {
                data.ClearCampHaulAssignment(wagon);
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] CampHaul: no logistics work found for " +
                    $"{wagon.name}, parking and releasing camp haul assignment.");
            }

            // Also ensure JustDelivered is cleared in case the ReturnTrip entry
            // somehow didn't fire (e.g. game loaded mid-task).
            data.JustDelivered = false;
        }

        // ── 7. Clear temporary assignment once logistics task actually starts ─
        //
        //  When a LogisticsTask starts executing (Enter is called on its first
        //  sub-task) we know the wagon accepted the backhaul work.  We can clear
        //  TemporaryRequester because the assignment is now permanent for the
        //  duration of that task.
        //
        //  LogisticsTask inherits from Task. The "work started" transition is
        //  signalled by taskStatus = WorkStarted, which is set inside task
        //  processor logic.  We hook the LogisticsDestinationSubTask constructor
        //  as a reliable proxy for "logistics task is now executing".

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LogisticsDestinationSubTask), MethodType.Constructor,
            new System.Type[] { typeof(Task), typeof(IRegistersForWork) })]
        private static void LogisticsDestSubTask_Ctor_Postfix(
            LogisticsDestinationSubTask __instance)
        {
            if (__instance.owningTask?.assignedReceiver is not TransportWagon wagon) return;

            WagonEnhancementData? data = wagon.GetComponent<WagonEnhancementData>();
            if (data == null) return;

            // The wagon accepted logistics work — it's no longer a "temporary"
            // assignment; clear the reference (but keep the assignment itself).
            if (data.TemporaryRequester != null)
            {
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] ReturnTrip: {wagon.name} started logistics task via " +
                    $"backhaul to {data.TemporaryRequester.gameObject.name}.");
                data.TemporaryRequester = null;
            }

            // Clear camp haul assignment once task actually starts
            if (data.CampHaulRequester != null)
            {
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] CampHaul: {wagon.name} started logistics task via " +
                    $"camp haul to {data.CampHaulRequester.gameObject.name}.");
                data.CampHaulRequester = null;
            }
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
                // Apply +20% on top of the already-calculated capacity
                var invField = typeof(TransportWagon).GetField("temporaryInventory",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (invField != null)
                {
                    var inventory = invField.GetValue(__instance);
                    if (inventory != null)
                    {
                        var capProp = inventory.GetType().GetProperty("carryCapacity",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);

                        if (capProp != null && capProp.CanWrite)
                        {
                            float current = (float)capProp.GetValue(inventory);
                            capProp.SetValue(inventory, current * 1.20f);  // +20%
                        }
                    }
                }
            }
        }
    }
}
