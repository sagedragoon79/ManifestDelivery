using System.Collections.Generic;
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

        // ── MD work claims ────────────────────────────────────────────────────
        //
        // MD steers a wagon by assigning it to specific requests just before
        // vanilla's LogisticsProxy search runs. Only that search reads
        // assignments (LogisticsGlobalTaskSearch.HasValidRequests /
        // ProcessRequestsAssignedToWorker), so a claim has done its job once a
        // route is built — or once the wagon gives up and parks. Unassigning
        // never cancels a task that is already underway.
        //
        // Claims are per REQUEST, never per requester. LogisticsRequester.
        // AssignWorker also attaches every request the building creates later,
        // and its UnassignWorker strips assignments vanilla made itself (every
        // available wagon is assigned to every storage's quota requests). The
        // old per-requester claims were overwritten without being released, so
        // wagons stayed tied to most buildings they ever claimed.

        private readonly List<ItemRequest> _claims = new List<ItemRequest>();
        private readonly HashSet<ItemRequest> _hubHerdClaims = new HashSet<ItemRequest>();

        /// <summary>True while MD holds claims that no route has used yet.</summary>
        public bool HasClaims => _claims.Count > 0;

        /// <summary>Time.time of the most recent claim, for stale-claim cleanup.</summary>
        public float ClaimTime { get; private set; }

        /// <summary>
        /// Assigns the wagon to one request. Returns false, without claiming,
        /// when the wagon is already assigned to it — so a later release never
        /// removes an assignment MD didn't make. <paramref name="hubHerd"/>
        /// also counts the claim in HubHaul's per-request herd guard.
        /// </summary>
        public bool ClaimRequest(TransportWagon wagon, ItemRequest request, bool hubHerd = false)
        {
            if (wagon == null || request == null) return false;
            if (wagon.logisticsAssignment.GetAssignedPriorityForRequest(
                    request, LogisticsAssignment.AssignmentCategory.Default, out _))
                return false;

            request.AssignWorker(wagon,
                LogisticsAssignment.AssignmentCategory.Default,
                LogisticsAssignment.AssignmentPriority.Default);
            _claims.Add(request);
            if (hubHerd && _hubHerdClaims.Add(request))
                Tasks.HubHaulSearchEntry.RegisterHubClaim(request);
            ClaimTime = Time.time;
            return true;
        }

        /// <summary>
        /// Claims a building's active delivery and move-out requests, one by one.
        /// Pass the same test the scan used to pick the building as
        /// <paramref name="include"/>: claiming everything let vanilla pick a
        /// request the scan never approved — e.g. a camp Foundry picked for its
        /// big iron output got its 5-coal input request served instead.
        /// </summary>
        public int ClaimRequester(TransportWagon wagon, LogisticsRequester requester,
            System.Predicate<ItemRequest>? include = null)
        {
            if (requester == null) return 0;
            int claimed = 0;
            foreach (var kv in requester.activeDeliveryRequests)
                if ((include == null || include(kv.Value)) && ClaimRequest(wagon, kv.Value)) claimed++;
            foreach (var kv in requester.activeMoveOutRequests)
                if ((include == null || include(kv.Value)) && ClaimRequest(wagon, kv.Value)) claimed++;
            return claimed;
        }

        /// <summary>Releases every claim MD holds on this wagon. Safe to call at any time.</summary>
        public void ReleaseClaims(TransportWagon? wagon, string reason)
        {
            if (_claims.Count == 0) return;
            int released = _claims.Count;
            foreach (var request in _claims)
            {
                if (request == null) continue;
                if (_hubHerdClaims.Contains(request))
                    Tasks.HubHaulSearchEntry.ReleaseHubClaim(request);
                if (wagon == null) continue;
                try
                {
                    request.UnassignWorker(wagon, LogisticsAssignment.AssignmentCategory.Default);
                }
                catch (System.Exception ex)
                {
                    ManifestDeliveryMod.Log.Warning(
                        $"[MD] Releasing a claim failed for {wagon.name}: {ex.Message}");
                }
            }
            _claims.Clear();
            _hubHerdClaims.Clear();
            ManifestDeliveryMod.LogVerbose(
                $"[MD] {(wagon != null ? wagon.name : "wagon")}: released {released} claim(s) ({reason}).");
        }

        private void OnDestroy()
        {
            try { ReleaseClaims(GetComponent<TransportWagon>(), "wagon destroyed"); }
            catch { /* scene teardown — requests are going away too */ }
        }

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
                ManifestDeliveryMod.LogVerbose(
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

        // ── Hub haul state ────────────────────────────────────────────────────

        /// <summary>
        /// Cooldown: next Time.time when a hub haul scan is allowed.
        /// Mirrors NextCampHaulScanTime but for Hub-mode distribution.
        /// </summary>
        public float NextHubHaulScanTime { get; set; }

        /// <summary>
        /// True when the previous HubHaul scan found no eligible requester.
        /// Throttles the "HubHaul EMPTY" log line to state transitions only.
        /// </summary>
        public bool LastHubHaulScanWasEmpty { get; set; }

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

    }
}
