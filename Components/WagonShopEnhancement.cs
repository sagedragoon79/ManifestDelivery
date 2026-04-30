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
        // Modes are saved per-shop AND per-save-file. Previous version used a
        // single global file, which caused mode state to leak between different
        // save files (save A's shops overwrote save B's when the user switched
        // saves in the same session). Now each save file has its own modes file
        // under UserData/ManifestDelivery_Modes/<saveName>.txt.
        //
        // Position hash: prior version collapsed axes via (x*1000 + z) which
        // hit real collisions at close shop pairs. Now combined via (ix*397)^iz
        // on rounded integer coordinates — no axis collapse.

        private static readonly Dictionary<int, ShopMode> SavedModes =
            new Dictionary<int, ShopMode>();

        // Tracks which save the SavedModes dict was populated for. Empty string
        // means "not yet loaded for any save." Matches SaveManager.activeSaveFileName
        // once loaded, so re-entry after save-switch reloads correctly.
        private static string _loadedForSave = null!;

        private const string ModesDirName = "ManifestDelivery_Modes";

        private static string GetSaveFilePath(string saveName)
        {
            // Sanitize — a save name could in theory contain path-invalid chars.
            string safe = saveName;
            if (string.IsNullOrEmpty(safe)) safe = "default";
            foreach (char c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return Path.Combine(
                Application.dataPath, "..", "UserData", ModesDirName,
                $"{safe}.txt");
        }

        private static string GetActiveSaveName()
        {
            try { return SaveManager.activeSaveFileName ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Ensures SavedModes is populated for the currently-active save. Safe
        /// to call from anywhere; no-ops if already loaded for this save. Also
        /// handles the mid-session save-switch case (main menu → load a
        /// different save) by clearing + reloading when the save name changes.
        /// </summary>
        public static void EnsureLoadedForCurrentSave()
        {
            string current = GetActiveSaveName();
            if (_loadedForSave == current) return;
            _loadedForSave = current;
            SavedModes.Clear();

            string path = GetSaveFilePath(current);
            if (!File.Exists(path))
            {
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] No modes file for save '{current}' — starting fresh.");
                return;
            }

            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split(':');
                    if (parts.Length != 2) continue;
                    if (!int.TryParse(parts[0], out int key)) continue;
                    if (!System.Enum.TryParse(parts[1], out ShopMode mode)) continue;
                    SavedModes[key] = mode;
                }

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Loaded {SavedModes.Count} shop mode(s) for save '{current}'.");
                foreach (var kvp in SavedModes)
                    ManifestDeliveryMod.Log.Msg($"[MD]   key={kvp.Key} mode={kvp.Value}");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] EnsureLoadedForCurrentSave failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stable hash combining rounded X and Z as independent dimensions.
        /// Two positions need both round-X and round-Z to match to collide,
        /// which is extremely unlikely for two buildings on a real map.
        /// </summary>
        private static int ComputeShopKey(Vector3 pos)
        {
            int ix = Mathf.RoundToInt(pos.x);
            int iz = Mathf.RoundToInt(pos.z);
            unchecked { return (ix * 397) ^ iz; }
        }

        /// <summary>
        /// Position-based hash key for this shop. Survives building upgrades
        /// and save/load cycles since position doesn't change.
        /// </summary>
        private int GetShopKey() => ComputeShopKey(transform.position);

        /// <summary>
        /// Public lookup used by early-boot patches (before our enhancement
        /// component exists on the shop). Returns the saved mode for the
        /// given world position, or null if this shop hasn't been saved.
        /// </summary>
        public static ShopMode? GetSavedModeForPosition(Vector3 pos)
        {
            EnsureLoadedForCurrentSave();
            int key = ComputeShopKey(pos);
            if (SavedModes.TryGetValue(key, out ShopMode m))
                return m;
            return null;
        }

        /// <summary>
        /// Max wagons for a given mode — used by patches that can't resolve
        /// a live WagonShopEnhancement yet.
        /// </summary>
        public static int GetMaxWagonsForMode(ShopMode mode) =>
            mode switch
            {
                ShopMode.Camp => ManifestDeliveryMod.MaxWagonsCamp.Value,
                ShopMode.Hub  => ManifestDeliveryMod.MaxWagonsHub.Value,
                _             => ManifestDeliveryMod.MaxWagonsStandard.Value,
            };

        /// <summary>
        /// Save all shop modes to disk for the currently-active save.
        /// Called on mode change. Format: one line per shop, "key:mode".
        /// </summary>
        public static void SaveModesToDisk()
        {
            try
            {
                string current = GetActiveSaveName();
                string path = GetSaveFilePath(current);
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var lines = new List<string>();
                foreach (var kvp in SavedModes)
                    lines.Add($"{kvp.Key}:{kvp.Value}");

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] SaveModesToDisk failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kept for backward compatibility with any external callers. Delegates
        /// to EnsureLoadedForCurrentSave which is save-aware.
        /// </summary>
        public static void LoadModesFromDisk() => EnsureLoadedForCurrentSave();

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
            EnsureLoadedForCurrentSave();
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
        /// Returns true when this shop is in Camp mode and camp hauling is enabled.
        /// </summary>
        public bool IsCampHaulActive =>
            Mode == ShopMode.Camp && ManifestDeliveryMod.CampHaulEnabled.Value;

        // ── Camp zone helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Tests whether a world position is within this shop's work radius.
        /// Uses the mode-appropriate radius (Camp=CampWorkRadius,
        /// Hub=HubWorkRadius, Standard=ReturnTripRadius) via the WorkRadius
        /// property so the check matches what the other subsystems anchor to.
        /// </summary>
        public bool IsWithinWorkRadius(Vector3 position)
        {
            float radiusSqr = WorkRadius * WorkRadius;
            return (position - transform.position).sqrMagnitude <= radiusSqr;
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

        private SelectionCircle? _selectionCircle;

        /// <summary>
        /// Creates or updates the visual radius circle around the Wagon Shop.
        /// Shown in Camp and Hub modes, hidden in Standard.
        ///
        /// Uses a plain SelectionCircle (not WorkArea). WorkArea would register
        /// the shop as a work-assignment target in the game's global work-area
        /// system, which scrambles other work-area buildings (notably fishing
        /// shacks' fish-available UI). The shop's task filtering lives in
        /// CampHaulSearchEntry/ReturnTripSearchEntry via direct distance math
        /// on shop.WorkRadius — no WorkArea component required.
        /// </summary>
        private void UpdateWorkAreaCircle()
        {
            try
            {
                float radius = WorkRadius;

                if (radius <= 0f)
                {
                    // Standard mode — hide circle if it exists
                    if (_selectionCircle != null)
                        _selectionCircle.SetEnabled(false);
                    return;
                }

                // Create SelectionCircle component if needed
                if (_selectionCircle == null)
                {
                    _selectionCircle = gameObject.GetComponent<SelectionCircle>();
                    if (_selectionCircle == null)
                        _selectionCircle = gameObject.AddComponent<SelectionCircle>();
                }

                // Initialize with current position and radius, then force the
                // edge meshes to regenerate at the new radius. SelectionCircle
                // only bakes edge positions once in its Start() method, so
                // subsequent radius changes don't update the visual without
                // an explicit CreateEdgeObjects() call.
                _selectionCircle.Init(transform.position, radius);
                try { _selectionCircle.CreateEdgeObjects(); }
                catch (System.Exception ex)
                {
                    ManifestDeliveryMod.Log.Warning(
                        $"[MD] SelectionCircle.CreateEdgeObjects failed: {ex.Message}");
                }

                // Only enable visibility when the shop is currently selected.
                // Actual show/hide is handled in Update() based on IsSelected.
                var sel = GetComponent<SelectableComponent>();
                _selectionCircle.SetEnabled(sel != null && sel.IsSelected);

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

                // Sync userDefinedMaxWorkers to the new cap.
                //   Going Hub (4) → Camp (2): clamps down to 2.
                //   Going Camp/Std (2) → Hub (4): raises to 4.
                if (building.userDefinedMaxWorkers != targetMax)
                {
                    int before = building.userDefinedMaxWorkers;
                    building.userDefinedMaxWorkers = targetMax;
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] {gameObject.name} userDefinedMaxWorkers: {before} → {targetMax}");
                }

                // CRITICAL: setting the property directly bypasses the
                // hire-worker path that + button triggers. AttemptToAddMaxWorkers
                // computes (userDefined - currentCount) and calls AddWorkers()
                // for the diff — pulls idle wainwrights into empty slots.
                // Without this, cap shows 4 but only saved-restored workers
                // stay assigned; empty slots never fill until user clicks +.
                if (building.userDefinedMaxWorkers > 0)
                {
                    int currentWorkers = building.workersRO?.Count ?? 0;
                    if (currentWorkers < building.userDefinedMaxWorkers)
                    {
                        building.AttemptToAddMaxWorkers();
                        ManifestDeliveryMod.Log.Msg(
                            $"[MD] {gameObject.name} AttemptToAddMaxWorkers " +
                            $"(workers: {currentWorkers} → target {building.userDefinedMaxWorkers})");
                    }
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
            // Sync placement preview circle positions with cursor
            Patches.WagonShopPatches.UpdatePreviewCircles();

            SelectableComponent? sel = GetComponent<SelectableComponent>();
            bool selected = sel != null && sel.IsSelected;

            // Show circle when: this shop is selected OR a WagonShop is being placed
            bool shouldShow = selected || Patches.WagonShopPatches.IsPlacingWagonShop;

            if (shouldShow != _lastSelectedState)
            {
                _lastSelectedState = shouldShow;
                if (_selectionCircle != null && WorkRadius > 0f)
                    _selectionCircle.SetEnabled(shouldShow);
            }

            // Mode buttons are injected via Harmony postfix on
            // UIBuildingInfoWindow.SetTargetData — no polling needed.

            // Mode cycling: only respond when the shop's info window is open.
            if (!UnityEngine.Input.GetKeyDown(ManifestDeliveryMod.ModeCycleKey)) return;
            if (selected)
                CycleMode();
        }

        // OnGUI floating label removed — mode state is now shown via the
        // in-window UI buttons (ModeButtonPatches).
    }
}
