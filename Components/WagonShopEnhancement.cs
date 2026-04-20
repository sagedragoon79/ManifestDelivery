using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ManifestDelivery;

namespace ManifestDelivery.Components
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
        // ── Persistent mode storage ──────────────────────────────────────────
        // Modes are saved per-shop using position-based keys (same pattern as
        // Tended Wilds priority persistence). Survives save/load because the
        // dictionary is static and keyed by building position, not instance ID.

        private static readonly Dictionary<int, ShopMode> SavedModes =
            new Dictionary<int, ShopMode>();

        private static readonly string SaveFilePath =
            Path.Combine(Application.dataPath, "..", "UserData", "ManifestDelivery_ShopModes.txt");

        /// <summary>
        /// Position-based hash key for this shop. Survives building upgrades
        /// and save/load cycles since position doesn't change.
        /// </summary>
        private int GetShopKey()
        {
            var pos = transform.position;
            return Mathf.RoundToInt(pos.x * 1000f + pos.z);
        }

        /// <summary>
        /// Save all shop modes to disk. Called on mode change.
        /// Format: one line per shop, "key:mode" (e.g., "1234567:Camp").
        /// </summary>
        public static void SaveModesToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(SaveFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var lines = new List<string>();
                foreach (var kvp in SavedModes)
                    lines.Add($"{kvp.Key}:{kvp.Value}");

                File.WriteAllLines(SaveFilePath, lines.ToArray());
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] SaveModesToDisk failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Load shop modes from disk. Called once during mod initialization
        /// (scene load). Populates the static SavedModes dictionary so
        /// RestoreSavedMode() can find each shop's mode by position key.
        /// </summary>
        public static void LoadModesFromDisk()
        {
            try
            {
                if (!File.Exists(SaveFilePath)) return;

                SavedModes.Clear();
                foreach (var line in File.ReadAllLines(SaveFilePath))
                {
                    var parts = line.Split(':');
                    if (parts.Length != 2) continue;
                    if (!int.TryParse(parts[0], out int key)) continue;
                    if (!System.Enum.TryParse(parts[1], out ShopMode mode)) continue;
                    SavedModes[key] = mode;
                }

                ManifestDeliveryMod.Log.Msg($"[MD] Loaded {SavedModes.Count} shop mode(s) from disk.");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] LoadModesFromDisk failed: {ex.Message}");
            }
        }

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
                SavedModes[GetShopKey()] = value;
                SaveModesToDisk();
                OnModeChanged();
            }
        }

        /// <summary>
        /// Restores mode from the static dictionary if a previous session
        /// saved a mode for this position.
        /// </summary>
        private void RestoreSavedMode()
        {
            int key = GetShopKey();
            if (SavedModes.TryGetValue(key, out ShopMode savedMode) && savedMode != _mode)
            {
                _mode = savedMode;  // Set directly, then trigger OnModeChanged
                OnModeChanged();
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] {gameObject.name} restored mode '{ModeDisplayName}' from save (key={key})");
            }
        }

        // ── Computed properties ───────────────────────────────────────────────

        public int MaxWagons => Mode switch
        {
            ShopMode.Camp => ManifestDeliveryMod.MaxWagonsCamp.Value,
            ShopMode.Hub  => ManifestDeliveryMod.MaxWagonsHub.Value,
            _             => ManifestDeliveryMod.MaxWagonsStandard.Value,
        };

        /// <summary>
        /// Return-trip backhaul scan radius. Camp and Hub use their shop's
        /// WorkRadius (same area as the visual circle). Standard uses its
        /// own config value (scans around the wagon, not the shop).
        /// </summary>
        public float ReturnTripRadius => Mode switch
        {
            ShopMode.Camp => WorkRadius,
            ShopMode.Hub  => WorkRadius,
            _             => ManifestDeliveryMod.ReturnTripRadiusStandard.Value,
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
            ShopMode.Camp => ManifestDeliveryMod.CampWorkRadius.Value,
            ShopMode.Hub  => ManifestDeliveryMod.HubWorkRadius.Value,
            _             => 0f,
        };

        /// <summary>
        /// Camp work radius — kept for backward compat, delegates to WorkRadius.
        /// </summary>
        public float CampWorkRadius => ManifestDeliveryMod.CampWorkRadius.Value;

        /// <summary>
        /// Returns true when this shop is in Camp mode and camp hauling is enabled.
        /// </summary>
        public bool IsCampHaulActive =>
            Mode == ShopMode.Camp && ManifestDeliveryMod.CampHaulEnabled.Value;

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
            ManifestDeliveryMod.Log.Msg(
                $"[MD] {gameObject.name} mode → {ModeDisplayName}  " +
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
        ///
        /// CRITICAL: After Init() we must call SelectionCircle.CreateEdgeObjects()
        /// via reflection to regenerate edge mesh positions. SelectionCircle only
        /// bakes edge positions once in its Start() method; subsequent radius
        /// changes don't update the visual unless we force a rebuild.
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

                // Force the underlying SelectionCircle to regenerate edge meshes
                // with the new radius value. Without this, the visible ring
                // stays at whatever radius SelectionCircle.Start captured initially.
                RegenerateSelectionCircleEdges(_workArea);

                // Only enable visibility when the shop is currently selected.
                // Actual show/hide is handled in Update() based on IsSelected.
                var sel = GetComponent<SelectableComponent>();
                _workArea.SetEnabled(sel != null && sel.IsSelected);

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] {gameObject.name} work area circle: {radius:F0}u radius");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] UpdateWorkAreaCircle failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces the WorkArea's SelectionCircle to regenerate its edge meshes
        /// at the current radius. Uses reflection because `selectionCircle` is
        /// a private field on WorkArea, but CreateEdgeObjects is public.
        /// </summary>
        private static void RegenerateSelectionCircleEdges(WorkArea workArea)
        {
            try
            {
                var scField = typeof(WorkArea).GetField("selectionCircle", AllInstance);
                if (scField == null) return;

                var selectionCircle = scField.GetValue(workArea);
                if (selectionCircle == null) return;

                var createEdges = selectionCircle.GetType().GetMethod(
                    "CreateEdgeObjects",
                    BindingFlags.Public | BindingFlags.Instance);
                createEdges?.Invoke(selectionCircle, null);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] RegenerateSelectionCircleEdges failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Adjusts the building's maxWorkers and userDefinedMaxWorkers based
        /// on mode. Hub = 4, Camp/Standard = 2.
        ///
        /// maxWorkers is an auto-property on Resource with backing field
        /// &lt;maxWorkers&gt;k__BackingField — we set it directly via reflection.
        /// userDefinedMaxWorkers setter caps at maxWorkers, so we must raise
        /// maxWorkers FIRST before the userDefined value can go up.
        /// </summary>
        private void UpdateWorkerSlots()
        {
            Building? building = GetComponent<Building>();
            if (building == null) return;

            int targetMax = MaxWagons;  // Hub=4, Camp/Standard=2

            try
            {
                // Find the auto-property backing field for maxWorkers.
                // It's declared on Resource as: public int maxWorkers { get; protected set; }
                // Backing field name: "<maxWorkers>k__BackingField"
                var maxField = FindBackingField(building.GetType(), "maxWorkers");

                if (maxField != null)
                {
                    int current = (int)maxField.GetValue(building);
                    if (current != targetMax)
                    {
                        maxField.SetValue(building, targetMax);
                        ManifestDeliveryMod.Log.Msg(
                            $"[MD] {gameObject.name} maxWorkers: {current} → {targetMax}");
                    }
                }
                else
                {
                    ManifestDeliveryMod.Log.Warning(
                        $"[MD] Could not find maxWorkers backing field on {building.GetType().Name}");
                }

                // Clamp userDefinedMaxWorkers to the new cap if reducing slots.
                // Going from Hub (4) to Camp (2): clamps to 2, releasing extra workers.
                if (building.userDefinedMaxWorkers > targetMax)
                {
                    building.userDefinedMaxWorkers = targetMax;
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] {gameObject.name} userDefinedMaxWorkers clamped to {targetMax}");
                }
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] UpdateWorkerSlots failed for {gameObject.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks the class hierarchy to find an auto-property backing field
        /// with the standard C# compiler naming convention.
        /// </summary>
        private static FieldInfo? FindBackingField(System.Type startType, string propertyName)
        {
            string backingName = $"<{propertyName}>k__BackingField";
            System.Type? t = startType;
            while (t != null)
            {
                var field = t.GetField(backingName, AllInstance);
                if (field != null) return field;
                t = t.BaseType;
            }
            return null;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private bool _initialized = false;

        private void Start()
        {
            // Delay initial setup one frame so the Building component is fully ready
            StartCoroutine(InitializeDelayed());
        }

        private System.Collections.IEnumerator InitializeDelayed()
        {
            yield return null; // Wait one frame
            if (_initialized) yield break;
            _initialized = true;

            // Restore mode from position-based save dictionary.
            // Must happen before UpdateWorkerSlots/UpdateWorkAreaCircle
            // since those depend on the current mode.
            RestoreSavedMode();

            // Apply mode-based config
            UpdateWorkerSlots();
            UpdateWorkAreaCircle();

            ManifestDeliveryMod.Log.Msg(
                $"[MD] {gameObject.name} initialized as {ModeDisplayName} " +
                $"(max wagons: {MaxWagons}, radius: {WorkRadius:F0}u)");
        }

        private bool _lastSelectedState = false;

        private void Update()
        {
            SelectableComponent? sel = GetComponent<SelectableComponent>();
            bool selected = sel != null && sel.IsSelected;

            // Toggle work area visibility when selection changes
            if (selected != _lastSelectedState)
            {
                _lastSelectedState = selected;
                if (_workArea != null && WorkRadius > 0f)
                    _workArea.SetEnabled(selected);
            }

            // Mode cycling: only respond when the shop's info window is open.
            if (!UnityEngine.Input.GetKeyDown(ManifestDeliveryMod.ModeCycleKey)) return;
            if (selected)
                CycleMode();
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
                ShopMode.Camp => $"\nHaul: {(IsCampHaulActive ? "ON" : "OFF")}  Speed: +25%",
                ShopMode.Hub  => $"\nSpeed: -10%  Capacity: +20%",
                _             => "",
            };

            // Camp/Hub show their WorkRadius (same as backhaul — one radius per mode)
            // Standard shows its scan-around-wagon backhaul radius
            string radiusLabel = Mode switch
            {
                ShopMode.Standard => $"Backhaul radius: {ReturnTripRadius:F0}u",
                _                 => $"Work radius: {WorkRadius:F0}u",
            };

            string label =
                $"[MD] {ModeDisplayName}\n" +
                $"Max wagons: {MaxWagons}  " +
                $"{radiusLabel}{modeBonus}\n" +
                $"Press [{ManifestDeliveryMod.ModeCycleKey}] to cycle mode";

            GUI.Label(new Rect(screenPos.x - 110, y - 60, 220, 60), label);
            GUI.color = Color.white;
        }
    }
}
