using UnityEngine;
using ManifestDelivery;

namespace ManifestDelivery.Components
{
    /// <summary>
    /// Attached to every TransportWagon at Start time by <see cref="Patches.TransportWagonPatches"/>.
    /// Tracks the state needed for return-trip backhaul logic.
    /// </summary>
    public class WagonEnhancementData : MonoBehaviour
    {
        // ── Return-trip state ─────────────────────────────────────────────────

        /// <summary>
        /// Set to true by ItemBundleDroppedOff patch the moment a delivery
        /// completes.  Cleared by ReturnTripSearchEntry once it processes the
        /// drop-off location.
        /// </summary>
        public bool JustDelivered { get; set; }

        /// <summary>World-space position of the most-recent delivery.</summary>
        public Vector3 LastDeliveryPosition { get; set; }

        /// <summary>
        /// The requester that was temporarily assigned to this wagon during a
        /// return-trip search.  Stored so we can clean up the assignment if the
        /// wagon ultimately parks without executing any logistics work (e.g. the
        /// request was filled by someone else first).
        /// </summary>
        public LogisticsRequester? TemporaryRequester { get; set; }

        // ── Shop-mode cache ───────────────────────────────────────────────────

        /// <summary>
        /// Cached reference to the owning shop's enhancement component, set
        /// when the wagon is assigned to a shop.  Avoids repeated GetComponent
        /// calls on the hot path.
        /// </summary>
        public WagonShopEnhancement? ShopEnhancement { get; set; }

        /// <summary>
        /// Lazily resolves the ShopEnhancement reference from the wagon's
        /// current <c>wagonShop</c> property. Call from hot paths (search
        /// entries, drop-off logging) to back-fill the link when wagons
        /// were created before our mod attached, or when AssignedToWagonShop
        /// fired before our data component existed.
        /// </summary>
        public WagonShopEnhancement? ResolveShopEnhancement(TransportWagon wagon)
        {
            if (ShopEnhancement != null) return ShopEnhancement;
            if (wagon == null || wagon.wagonShop == null) return null;

            ShopEnhancement = wagon.wagonShop.GetComponent<WagonShopEnhancement>();
            if (ShopEnhancement != null)
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Back-linked {wagon.name} → " +
                    $"{wagon.wagonShop.gameObject.name} ({ShopEnhancement.Mode})");
            return ShopEnhancement;
        }

        // ── Camp haul state ───────────────────────────────────────────────────

        /// <summary>
        /// Cooldown: next Time.time when camp haul scan is allowed.
        /// Prevents spamming the search every task cycle when idle.
        /// </summary>
        public float NextCampHaulScanTime { get; set; }

        /// <summary>
        /// True when the previous CampHaul scan found no eligible source.
        /// Used to throttle the "CampHaul EMPTY" log line to state transitions
        /// only (was-finding → just-emptied), avoiding many lines per second of
        /// disk I/O when wagons are idle in a slow camp.
        /// </summary>
        public bool LastCampHaulScanWasEmpty { get; set; }

        /// <summary>
        /// The requester assigned during a camp haul search.
        /// Stored for cleanup if the wagon parks without executing.
        /// </summary>
        public LogisticsRequester? CampHaulRequester { get; set; }

        /// <summary>
        /// Cleans up a camp haul assignment if one is pending.
        /// </summary>
        public void ClearCampHaulAssignment(TransportWagon wagon)
        {
            if (CampHaulRequester == null) return;

            try
            {
                CampHaulRequester.UnassignWorker(
                    wagon,
                    LogisticsAssignment.AssignmentCategory.Default);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ClearCampHaulAssignment failed for {wagon?.name}: {ex.Message}");
            }

            CampHaulRequester = null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the configured search radius based on the owning shop's mode.
        /// Standard mode uses its own config value (around the wagon).
        /// Camp/Hub modes use their WorkRadius (around the shop) — one radius
        /// per shop mode, consistent with the visual service area circle.
        /// Falls back to the Standard radius when no shop is known.
        /// </summary>
        public float ReturnTripRadius =>
            ShopEnhancement == null
                ? ManifestDeliveryMod.ReturnTripRadiusStandard.Value
                : ShopEnhancement.Mode switch
                {
                    ShopMode.Camp => ShopEnhancement.WorkRadius,
                    ShopMode.Hub  => ShopEnhancement.WorkRadius,
                    _             => ManifestDeliveryMod.ReturnTripRadiusStandard.Value,
                };

        /// <summary>
        /// Returns the max-wagon count for the owning shop's mode.
        /// </summary>
        public int MaxWagons =>
            ShopEnhancement == null
                ? ManifestDeliveryMod.MaxWagonsStandard.Value
                : ShopEnhancement.Mode switch
                {
                    ShopMode.Camp => ManifestDeliveryMod.MaxWagonsCamp.Value,
                    ShopMode.Hub  => ManifestDeliveryMod.MaxWagonsHub.Value,
                    _             => ManifestDeliveryMod.MaxWagonsStandard.Value,
                };

        /// <summary>
        /// Cleans up a temporary requester assignment if one is pending.
        /// Safe to call even when TemporaryRequester is null.
        /// </summary>
        public void ClearTemporaryAssignment(TransportWagon wagon)
        {
            if (TemporaryRequester == null) return;

            try
            {
                TemporaryRequester.UnassignWorker(
                    wagon,
                    LogisticsAssignment.AssignmentCategory.Default);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ClearTemporaryAssignment failed for wagon " +
                    $"{wagon?.name}: {ex.Message}");
            }

            TemporaryRequester = null;
        }
    }
}
