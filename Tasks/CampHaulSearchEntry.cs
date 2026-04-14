using System.Collections.Generic;
using UnityEngine;
using WagonShopsEnhanced;
using WagonShopsEnhanced.Components;

namespace WagonShopsEnhanced.Tasks
{
    /// <summary>
    /// Injected into every TransportWagon's search entry list at priority 2
    /// (above LogisticsProxy = 0, below ReturnTrip = 3).
    ///
    /// EXECUTION ORDER within a single task-search cycle:
    ///   KickOut(10) → ReturnTrip(3) → CampHaul(2) → LogisticsProxy(0) → ParkWagon(-10)
    ///
    /// WHAT IT DOES:
    ///   When the wagon belongs to a Camp-mode shop and is idle (not just-delivered),
    ///   this entry proactively scans production buildings within the camp work radius
    ///   for goods with active TakeOut (move-out) requests.
    ///
    ///   It filters by item type according to config:
    ///     - Raw meat/fish → finds nearest smokehouse within camp radius
    ///     - Smoked meat/fish, other food → finds nearest hub root cellar or market
    ///     - Iron ingots → finds nearest hub storehouse
    ///
    ///   The nearest eligible requester is temporarily assigned to the wagon,
    ///   then this entry returns null so LogisticsProxy creates the real task.
    ///
    /// COOLDOWN:
    ///   Scans at most once every 10 seconds per wagon to avoid performance
    ///   impact on task search cycles.
    /// </summary>
    public class CampHaulSearchEntry : TaskSearchEntry
    {
        private readonly TransportWagon _wagon;
        private readonly WagonEnhancementData _data;

        private const int PriorityModifier = 2;
        private const float ScanCooldown = 10f;

        // Item IDs for routing decisions
        private static readonly HashSet<ItemID> RawMeatFish = new HashSet<ItemID>
        {
            ItemID.Meat, ItemID.Fish
        };

        private static readonly HashSet<ItemID> SmokedItems = new HashSet<ItemID>
        {
            ItemID.SmokedMeat, ItemID.SmokedFish
        };

        private static readonly HashSet<ItemID> IronItems = new HashSet<ItemID>
        {
            ItemID.Iron
        };

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
                PriorityModifier)                        // _priorityModifier
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

            // Only active in Camp mode with hauling enabled
            if (_data.ShopEnhancement == null || !_data.ShopEnhancement.IsCampHaulActive)
                return null;

            // Don't scan if the return-trip just fired (let it handle post-delivery)
            if (_data.JustDelivered)
                return null;

            // Driver must be present
            if (_wagon.driver.IsNull())
                return null;

            // Cooldown: don't scan every task cycle
            if (Time.time < _data.NextCampHaulScanTime)
                return null;

            _data.NextCampHaulScanTime = Time.time + ScanCooldown;

            // ── Find the best nearby source (building with goods to move out) ──
            LogisticsRequester? bestSource = FindBestCampSource();
            if (bestSource == null)
                return null;

            // ── Temporarily assign the requester to this wagon ──────────────
            try
            {
                bestSource.AssignWorker(
                    _wagon,
                    LogisticsAssignment.AssignmentCategory.Default,
                    LogisticsAssignment.AssignmentPriority.Default);

                _data.CampHaulRequester = bestSource;

                WagonShopsEnhancedMod.Log.Msg(
                    $"[WSE] CampHaul: {_wagon.name} assigned to " +
                    $"{bestSource.gameObject.name} " +
                    $"({Vector3.Distance(_wagon.transform.position, bestSource.transform.position):F1}u away)");
            }
            catch (System.Exception ex)
            {
                WagonShopsEnhancedMod.Log.Warning(
                    $"[WSE] CampHaul assignment failed for {_wagon.name}: {ex.Message}");
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

            float radiusSqr = shop.WorkRadius * shop.WorkRadius;
            Vector3 shopPos = shop.transform.position;

            LogisticsRequester? bestRequester = null;
            float bestDistSqr = float.MaxValue;

            foreach (LogisticsRequester requester in gm.logisiticsAggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;
                if (requester.activeMoveOutRequests.Count == 0) continue;

                // Must be within camp work radius of the shop
                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Skip if wagon is already assigned here
                if (IsAlreadyAssigned(requester)) continue;

                // Skip storage buildings — camp wagons pick up from PRODUCTION
                // buildings, not shuffle between storages
                string tag = requester.gameObject.tag;
                if (!string.IsNullOrEmpty(tag) && StorageBuildingTags.Contains(tag))
                    continue;

                // Check if any move-out request matches our item routing config
                if (!HasEligibleMoveOutItem(requester)) continue;

                // Check bulk transport minimum
                if (!HasBulkEligibleRequest(requester)) continue;

                if (distSqr < bestDistSqr)
                {
                    bestDistSqr  = distSqr;
                    bestRequester = requester;
                }
            }

            return bestRequester;
        }

        /// <summary>
        /// Checks whether any of the requester's move-out requests contain
        /// items we're configured to haul (food, iron, etc.)
        /// </summary>
        private bool HasEligibleMoveOutItem(LogisticsRequester requester)
        {
            foreach (var kv in requester.activeMoveOutRequests)
            {
                ItemRequest request = kv.Value;

                // Get the item ID from the request
                if (request is SingleItemRequest singleReq)
                {
                    ItemID itemID = singleReq.itemID;

                    // Raw meat/fish → smokehouse routing
                    if (WagonShopsEnhancedMod.CampHaulRawToSmokehouse.Value
                        && RawMeatFish.Contains(itemID))
                        return true;

                    // Smoked items → hub routing
                    if (WagonShopsEnhancedMod.CampHaulFoodToHub.Value
                        && SmokedItems.Contains(itemID))
                        return true;

                    // Iron → hub routing
                    if (WagonShopsEnhancedMod.CampHaulIronToHub.Value
                        && IronItems.Contains(itemID))
                        return true;

                    // General food → hub routing
                    if (WagonShopsEnhancedMod.CampHaulFoodToHub.Value
                        && IsFoodItem(itemID))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks whether at least one move-out request passes the wagon's
        /// bulk minimum restriction.
        /// </summary>
        private bool HasBulkEligibleRequest(LogisticsRequester requester)
        {
            foreach (var kv in requester.activeMoveOutRequests)
            {
                if (PassesBulkCheck(kv.Value)) return true;
            }
            return false;
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

        /// <summary>
        /// Returns true when the wagon already has an assignment to this requester.
        /// </summary>
        private bool IsAlreadyAssigned(LogisticsRequester requester)
        {
            var assigned = _wagon.logisticsAssignment
                               .GetAssignedRequestsByCategory(
                                   LogisticsAssignment.AssignmentCategory.Default);
            if (assigned == null) return false;

            foreach (var kv in assigned)
            {
                if (kv.Key.requester == requester)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the given ItemID is a food item.
        /// Uses Villager.foodItemIDs if available, otherwise a hardcoded set.
        /// </summary>
        private static bool IsFoodItem(ItemID id)
        {
            // Try the game's static food list first
            try
            {
                ItemID[] foodIDs = Villager.foodItemIDs;
                if (foodIDs != null)
                {
                    for (int i = 0; i < foodIDs.Length; i++)
                        if (foodIDs[i] == id) return true;
                    return false;
                }
            }
            catch { }

            // Fallback hardcoded set
            switch (id)
            {
                case ItemID.Meat:
                case ItemID.Fish:
                case ItemID.SmokedMeat:
                case ItemID.SmokedFish:
                case ItemID.Bread:
                case ItemID.Pastry:
                case ItemID.Beans:
                case ItemID.RootVegetable:
                case ItemID.Greens:
                case ItemID.Mushroom:
                case ItemID.PreservedVeg:
                case ItemID.Berries:
                case ItemID.Nuts:
                case ItemID.Fruit:
                case ItemID.Preserves:
                case ItemID.Eggs:
                case ItemID.Milk:
                case ItemID.Cheese:
                    return true;
                default:
                    return false;
            }
        }
    }
}
