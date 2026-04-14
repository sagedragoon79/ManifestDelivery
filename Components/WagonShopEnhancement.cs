using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WagonShopsEnhanced;

namespace WagonShopsEnhanced.Components
{
    // ── Mode enum ─────────────────────────────────────────────────────────────

    public enum ShopMode
    {
        /// <summary>
        /// Vanilla behaviour plus return-trip search.
        /// Wagon count capped by MaxWagonsStandard config (default 2).
        /// </summary>
        Standard,

        /// <summary>
        /// Tuned for remote extraction sites (mines, logging camps).
        /// Larger return-trip radius to maximise backhaul from the distant hub.
        /// Wagon count capped by MaxWagonsCamp config (default 2).
        /// One wagon handles outbound ore/logs; a second can bring supplies back.
        /// Wagons keep vanilla IgnoreGloballyAssignedRequests — they stay
        /// dedicated to explicitly routed work.
        /// </summary>
        Camp,

        /// <summary>
        /// Tuned for the central stockpile / industry hub area.
        /// Drops IgnoreGloballyAssignedRequests so wagons participate in the
        /// global logistics pool and handle any bulk request in the settlement.
        /// Wagon count capped by MaxWagonsHub config (default 4).
        /// </summary>
        Hub,
    }

    /// <summary>
    /// Attached to every WagonShop at Start time by <see cref="Patches.WagonShopPatches"/>.
    /// Stores the per-shop mode and exposes helpers used by patches and the
    /// return-trip search entry.
    /// </summary>
    public class WagonShopEnhancement : MonoBehaviour
    {
        // ── State ─────────────────────────────────────────────────────────────

        [SerializeField]
        private ShopMode _mode = ShopMode.Standard;

        public ShopMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                OnModeChanged();
            }
        }

        // ── Computed properties ───────────────────────────────────────────────

        public int MaxWagons => Mode switch
        {
            ShopMode.Camp => WagonShopsEnhancedMod.MaxWagonsCamp.Value,
            ShopMode.Hub  => WagonShopsEnhancedMod.MaxWagonsHub.Value,
            _             => WagonShopsEnhancedMod.MaxWagonsStandard.Value,
        };

        public float ReturnTripRadius => Mode switch
        {
            ShopMode.Camp => WagonShopsEnhancedMod.ReturnTripRadiusCamp.Value,
            ShopMode.Hub  => WagonShopsEnhancedMod.ReturnTripRadiusHub.Value,
            _             => WagonShopsEnhancedMod.ReturnTripRadiusStandard.Value,
        };

        /// <summary>
        /// Hub wagons participate in the global request pool.
        /// All other modes keep IgnoreGloballyAssignedRequests.
        /// </summary>
        public bool IgnoresGlobalRequests => Mode != ShopMode.Hub;

        /// <summary>
        /// Active work radius for the current mode.
        /// Camp = 60u, Hub = 100u, Standard = 0 (no radius).
        /// </summary>
        public float WorkRadius => Mode switch
        {
            ShopMode.Camp => WagonShopsEnhancedMod.CampWorkRadius.Value,
            ShopMode.Hub  => WagonShopsEnhancedMod.HubWorkRadius.Value,
            _             => 0f,
        };

        /// <summary>
        /// Camp work radius — kept for backward compat, delegates to WorkRadius.
        /// </summary>
        public float CampWorkRadius => WagonShopsEnhancedMod.CampWorkRadius.Value;

        /// <summary>
        /// Returns true when this shop is in Camp mode and camp hauling is enabled.
        /// </summary>
        public bool IsCampHaulActive =>
            Mode == ShopMode.Camp && WagonShopsEnhancedMod.CampHaulEnabled.Value;

        // ── Camp zone helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Tests whether a world position is within this shop's camp work radius.
        /// </summary>
        public bool IsWithinCampRadius(Vector3 position)
        {
            float radiusSqr = CampWorkRadius * CampWorkRadius;
            return (position - transform.position).sqrMagnitude <= radiusSqr;
        }

        /// <summary>
        /// Finds all LogisticsRequesters within the camp work radius that have
        /// active TakeOut (move-out) requests. These are production buildings
        /// with goods waiting to be picked up.
        /// </summary>
        public List<LogisticsRequester> FindNearbyMoveOutRequesters()
        {
            var results = new List<LogisticsRequester>();
            if (!IsCampHaulActive) return results;

            GameManager? gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return results;

            float radiusSqr = CampWorkRadius * CampWorkRadius;
            Vector3 shopPos = transform.position;

            foreach (LogisticsRequester requester in gm.logisiticsAggregator.activeStationaryRequestsRO)
            {
                if (!requester.hasActiveRequests) continue;

                float distSqr = (requester.transform.position - shopPos).sqrMagnitude;
                if (distSqr > radiusSqr) continue;

                // Only include requesters that have move-out requests (goods to pick up)
                if (requester.activeMoveOutRequests.Count > 0)
                    results.Add(requester);
            }

            return results;
        }

        // ── Mode display name (shown in HUD label) ────────────────────────────

        public string ModeDisplayName => Mode switch
        {
            ShopMode.Camp => "Camp Shop",
            ShopMode.Hub  => "Hub Shop",
            _             => "Standard Shop",
        };

        // ── Mode cycle (called from input patch when shop window is open) ─────

        public void CycleMode()
        {
            Mode = Mode switch
            {
                ShopMode.Standard => ShopMode.Camp,
                ShopMode.Camp     => ShopMode.Hub,
                _                 => ShopMode.Standard,
            };
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private void OnModeChanged()
        {
            WagonShopsEnhancedMod.Log.Msg(
                $"[WSE] {gameObject.name} mode → {ModeDisplayName}  " +
                $"(max wagons: {MaxWagons}, radius: {WorkRadius:F0}u, " +
                $"return-trip: {ReturnTripRadius:F0}u)");

            // Update worker slots based on mode
            UpdateWorkerSlots();

            // Update work area visual circle
            UpdateWorkAreaCircle();

            // Update the ShopEnhancement cache on all wagons currently assigned
            // to this shop so their flag overrides reflect the new mode.
            WagonShop? shop = GetComponent<WagonShop>();
            if (shop == null) return;

            foreach (TransportWagon wagon in shop.registeredWagonsRO)
            {
                WagonEnhancementData? data = wagon.GetComponent<WagonEnhancementData>();
                if (data != null)
                    data.ShopEnhancement = this;

                // Recalculate capacity for Hub mode bonus
                wagon.CalculateCarryCapacity();
            }
        }

        // ── Work area visual circle ──────────────────────────────────────────

        private WorkArea? _workArea;

        /// <summary>
        /// Creates or updates the visual radius circle around the Wagon Shop.
        /// Shown in Camp and Hub modes, hidden in Standard.
        /// </summary>
        private void UpdateWorkAreaCircle()
        {
            try
            {
                float radius = WorkRadius;

                if (radius <= 0f)
                {
                    // Standard mode — hide circle if it exists
                    if (_workArea != null)
                        _workArea.SetEnabled(false);
                    return;
                }

                // Create WorkArea component if needed
                if (_workArea == null)
                {
                    _workArea = gameObject.GetComponent<WorkArea>();
                    if (_workArea == null)
                        _workArea = gameObject.AddComponent<WorkArea>();
                }

                // Initialize with current position and radius
                _workArea.Init(transform.position, radius);
                _workArea.SetEnabled(true);

                WagonShopsEnhancedMod.Log.Msg(
                    $"[WSE] {gameObject.name} work area circle: {radius:F0}u radius");
            }
            catch (System.Exception ex)
            {
                WagonShopsEnhancedMod.Log.Warning(
                    $"[WSE] UpdateWorkAreaCircle failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Adjusts the building's maxWorkers and userDefinedMaxWorkers based
        /// on mode. Hub = 4, Camp/Standard = 2. Extra workers are released
        /// to the laborer pool when slots are reduced.
        /// </summary>
        private void UpdateWorkerSlots()
        {
            Building? building = GetComponent<Building>();
            if (building == null) return;

            int targetMax = MaxWagons;  // Hub=4, Camp/Standard=2

            try
            {
                // Set the hard cap (_maxWorkers) via reflection
                var maxField = typeof(Building).GetField("_maxWorkers", AllInstance);
                if (maxField == null)
                {
                    // Try base class (DataRecord path)
                    maxField = building.GetType().GetField("_maxWorkers", AllInstance);
                }

                if (maxField != null)
                {
                    int current = (int)maxField.GetValue(building);
                    if (current != targetMax)
                    {
                        maxField.SetValue(building, targetMax);
                        WagonShopsEnhancedMod.Log.Msg(
                            $"[WSE] {gameObject.name} maxWorkers: {current} → {targetMax}");
                    }
                }

                // Clamp userDefinedMaxWorkers to the new cap
                if (building.userDefinedMaxWorkers > targetMax)
                {
                    building.userDefinedMaxWorkers = targetMax;
                    WagonShopsEnhancedMod.Log.Msg(
                        $"[WSE] {gameObject.name} userDefinedMaxWorkers clamped to {targetMax}");
                }
            }
            catch (System.Exception ex)
            {
                WagonShopsEnhancedMod.Log.Warning(
                    $"[WSE] UpdateWorkerSlots failed for {gameObject.name}: {ex.Message}");
            }
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Update()
        {
            // Mode cycling: only respond when the shop's info window is open.
            if (!UnityEngine.Input.GetKeyDown(WagonShopsEnhancedMod.ModeCycleKey)) return;

            // Check whether this shop's window is currently open by looking for
            // a selected component on the same GameObject.
            SelectableComponent? sel = GetComponent<SelectableComponent>();
            if (sel != null && sel.IsSelected)
            {
                CycleMode();
            }
        }

        private void OnGUI()
        {
            // Only draw when selected.
            SelectableComponent? sel = GetComponent<SelectableComponent>();
            if (sel == null || !sel.IsSelected) return;

            // Simple world-to-screen label above the building.
            Vector3 screenPos = Camera.main != null
                ? Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 6f)
                : Vector3.zero;

            if (screenPos.z <= 0) return;

            // Flip Y (GUI uses top-left origin).
            float y = Screen.height - screenPos.y;

            GUI.color = Mode switch
            {
                ShopMode.Camp => new Color(0.8f, 1f, 0.6f),
                ShopMode.Hub  => new Color(0.6f, 0.8f, 1f),
                _             => Color.white,
            };

            string modeBonus = Mode switch
            {
                ShopMode.Camp => $"\nCamp radius: {CampWorkRadius:F0}u  Haul: {(IsCampHaulActive ? "ON" : "OFF")}" +
                                 $"\nSpeed: +25%",
                ShopMode.Hub  => $"\nSpeed: -10%  Capacity: +20%",
                _             => "",
            };

            string label =
                $"[WSE] {ModeDisplayName}\n" +
                $"Max wagons: {MaxWagons}  " +
                $"Backhaul radius: {ReturnTripRadius:F0}u{modeBonus}\n" +
                $"Press [{WagonShopsEnhancedMod.ModeCycleKey}] to cycle mode";

            GUI.Label(new Rect(screenPos.x - 110, y - 60, 220, 60), label);
            GUI.color = Color.white;
        }
    }
}
