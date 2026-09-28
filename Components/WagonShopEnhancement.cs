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
        /// Tuned for the central stockpile / industry hub area. HubHaulSearchEntry
        /// serves deliveries and pickups anywhere in the work radius. Also drops
        /// IgnoreGloballyAssignedRequests, whose only effect in the game is fire
        /// duty: carrying water to burning buildings.
        /// Wagon count capped by MaxWagonsHub config.
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

        /// <summary>Sanitize — a save name could contain path-invalid chars (it always contains '/').</summary>
        private static string SanitizeSaveName(string saveName)
        {
            string safe = saveName;
            if (string.IsNullOrEmpty(safe)) safe = "default";
            foreach (char c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return safe;
        }

        private static string GetSaveFilePath(string saveName)
        {
            return Path.Combine(
                Application.dataPath, "..", "UserData", ModesDirName,
                $"{SanitizeSaveName(saveName)}.txt");
        }

        /// <summary>
        /// Path to read modes from, healing installs written before the
        /// town-folder fix. Those wrote one file per SAVE FILE
        /// (Town_ts_Town.txt, Town_ts_Town.sav.txt, Town_ts_AutoSave 1.txt);
        /// we now write one per TOWN (Town_ts.txt). If the canonical file does
        /// not exist yet, adopt the most recently written legacy file for this
        /// town so nobody loses the modes they already set. The next save
        /// rewrites them under the canonical name.
        /// </summary>
        private static string ResolveLoadPath(string saveIdentity)
        {
            string primary = GetSaveFilePath(saveIdentity);
            if (File.Exists(primary)) return primary;

            try
            {
                string? dir = Path.GetDirectoryName(primary);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return primary;

                // Build the prefix from the sanitized identity, NOT from the
                // filename — GetFileNameWithoutExtension would truncate a town
                // whose name contains a dot.
                string prefix = SanitizeSaveName(saveIdentity) + "_";
                string? newest = null;
                System.DateTime newestTime = System.DateTime.MinValue;

                foreach (string file in Directory.GetFiles(dir, "*.txt"))
                {
                    string name = Path.GetFileName(file);
                    if (!name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;

                    System.DateTime stamp = File.GetLastWriteTimeUtc(file);
                    if (stamp > newestTime) { newestTime = stamp; newest = file; }
                }

                if (newest != null)
                {
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD] Migrating shop modes from legacy file " +
                        $"'{Path.GetFileName(newest)}' → '{Path.GetFileName(primary)}'.");
                    return newest;
                }
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] Legacy modes lookup failed: {ex.Message}");
            }

            return primary;
        }

        // Last non-empty save name we observed. SaveManager.activeSaveFileName
        // is a mutable static the game nulls out during save/scene transitions
        // and recomputes lazily — querying it at the wrong moment (including
        // mid-game when the player changes a shop mode) returns "", which used
        // to send writes to default.txt and reads to the real <save>.txt,
        // silently reverting modes on reload. We latch the last good value and
        // only switch when a *different* non-empty name appears (real
        // save-switch), never when it transiently goes empty.
        private static string _lastKnownSaveName = "";

        /// <summary>
        /// Latch the authoritative save name the instant a load begins. Called
        /// from Harmony patches on the game's load entry points
        /// (CESceneManager.LoadFromWithinGame / StartSceneManager.StartGame),
        /// which set SaveManager.activeSaveFileName themselves. This closes the
        /// race where our shop Start/Finalize code could query the volatile
        /// activeSaveFileName static before the game populated it — reading or
        /// writing default.txt instead of the real per-save file. Ignores empty
        /// values so a transient clear never overwrites a good name.
        /// </summary>
        /// <summary>
        /// Forgets the latched town. Called after SaveManager.Init(), which the
        /// game runs on EVERY town change (new game, restart, reroll, loading
        /// from inside a game, and the start scene). Load paths set the real
        /// name right after Init and re-latch it; a new game has no name until
        /// its first save. Without this, a brand-new town kept the previous
        /// town's identity and read/wrote that town's files until it was saved.
        /// </summary>
        internal static void ResetSaveIdentity()
        {
            if (string.IsNullOrEmpty(_lastKnownSaveName)) return;
            _lastKnownSaveName = "";
            ManifestDeliveryMod.LogVerbose("[MD] Save identity cleared (town change).");
        }

        public static void LatchSaveName(string name)
        {
            name = NormalizeSaveIdentity(name);
            if (string.IsNullOrEmpty(name)) return;
            if (_lastKnownSaveName != name)
            {
                _lastKnownSaveName = name;
                ManifestDeliveryMod.Log.Msg($"[MD] Save identity latched: '{name}'");
            }
        }

        /// <summary>
        /// Reduces any save-file name to the identity of the TOWN it belongs to.
        ///
        /// This fixes a real, user-reported mode-revert bug. FF hands us several
        /// different strings for the same settlement:
        ///   "Grimtree_2026238203354/Grimtree"        (load hooks — no extension)
        ///   "Grimtree_2026238203354/Grimtree.sav"    (SaveManager.Save, when
        ///                                             activeSaveFileName was
        ///                                             empty it rebuilds it WITH
        ///                                             the extension)
        ///   "Grimtree_2026238203354/AutoSave 1"      (autosaves)
        /// Keying on the raw name produced a *different* modes file for each, so
        /// settings written while playing (…Grimtree.sav.txt, …AutoSave 1.txt)
        /// were never read back on reload (…Grimtree.txt) and every shop came
        /// back Standard. The UserData folder still shows the duplicates.
        ///
        /// The town folder is the stable identity — and it is also the *correct*
        /// one: a manual save and its autosaves are the same town, so they
        /// should share shop modes.
        /// </summary>
        private static string NormalizeSaveIdentity(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            try
            {
                // SaveManager.GameFolder returns everything up to and including
                // the first '/', e.g. "Grimtree_2026238203354/".
                string folder = SaveManager.GameFolder(raw);
                if (!string.IsNullOrEmpty(folder))
                    return folder.TrimEnd('/', '\\');
            }
            catch { /* fall through to the defensive path */ }

            // No folder separator (older saves / unexpected shapes): at least
            // strip the extension so ".sav" and bare names agree.
            if (raw.EndsWith(".sav", System.StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);
            return raw;
        }

        // internal so StoragePriorityStore can share the exact same latched
        // accessor rather than re-deriving it (the latch is what fixed the
        // v1.0.16/1.0.18 save-name races — one implementation, not two).
        internal static string GetActiveSaveName()
        {
            string live;
            try { live = SaveManager.activeSaveFileName ?? ""; }
            catch { live = ""; }

            // Normalize to the town folder BEFORE latching, so an autosave or a
            // ".sav"-suffixed name can never masquerade as a different save.
            live = NormalizeSaveIdentity(live);

            if (!string.IsNullOrEmpty(live))
            {
                _lastKnownSaveName = live;
                return live;
            }

            // Transient empty — fall back to the last good name so reads and
            // writes stay on the same file.
            return _lastKnownSaveName;
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

            // A brand-new town has no save name until its first save. Keep its
            // modes in memory only — never read or write default.txt — and adopt
            // them under the town's real name when it first saves (below).
            if (string.IsNullOrEmpty(current))
            {
                _loadedForSave = current;
                SavedModes.Clear();
                return;
            }

            // Switching to a real town here (a load) discards any unsaved new
            // town's modes; only OnGameSaved adopts them, at that town's first save.
            _loadedForSave = current;
            SavedModes.Clear();

            string path = ResolveLoadPath(current);
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

                // Finish the migration now. Modes are otherwise only written when
                // a mode CHANGES, so a migrated town would keep re-reading the
                // legacy file every load — and would lose its settings if that
                // file were ever cleaned up. Writing the canonical file here makes
                // the migration a one-time event. The legacy file is deliberately
                // left in place as a rollback safety net.
                if (!string.Equals(path, GetSaveFilePath(current), System.StringComparison.OrdinalIgnoreCase)
                    && SavedModes.Count > 0)
                {
                    SaveModesToDisk();
                }
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
                // No identity yet (new, unsaved town): stay in memory. The first
                // save adopts these modes under the real name.
                if (string.IsNullOrEmpty(current)) return;
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
        /// Called after the game writes a save. Adopts a new town's in-memory
        /// modes under its first real name, and keeps the modes file in step
        /// with the game's save cadence.
        /// </summary>
        internal static void OnGameSaved()
        {
            string current = GetActiveSaveName();
            if (_loadedForSave == "" && !string.IsNullOrEmpty(current) && SavedModes.Count > 0)
            {
                // First save of a brand-new town: the game just created its
                // folder, so no modes file exists yet. Keep what was set so far.
                _loadedForSave = current;
                SaveModesToDisk();
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] New town '{current}' saved for the first time — kept {SavedModes.Count} shop mode(s) set before the save.");
                return;
            }
            EnsureLoadedForCurrentSave();
            if (SavedModes.Count > 0) SaveModesToDisk();
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
                EnsureLoadedForCurrentSave();
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
        /// Hub wagons join the global request pool — in practice, fire duty
        /// (the only globally assigned requests carry water to fires).
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

        /// <summary>
        /// Returns true when this shop is in Hub mode and hub hauling is enabled.
        /// Drives the proactive Hub distributor (HubHaulSearchEntry).
        /// </summary>
        public bool IsHubHaulActive =>
            Mode == ShopMode.Hub && ManifestDeliveryMod.HubHaulEnabled.Value;

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

            // Repaint the mode buttons if this shop's window is open. Covers the
            // mode key, which changes the mode without touching the buttons.
            Patches.ModeButtonPatches.RefreshIfShowing(this);

            // Update the ShopEnhancement cache on all wagons currently assigned
            // to this shop so their flag overrides reflect the new mode.
            WagonShop? shop = GetComponent<WagonShop>();
            if (shop == null) return;

            foreach (TransportWagon wagon in shop.registeredWagonsRO)
            {
                WagonEnhancementData? data = wagon.GetComponent<WagonEnhancementData>();
                if (data != null)
                {
                    // Claims made under the old mode (e.g. Hub claims on town
                    // buildings) must not follow the wagon into the new mode.
                    data.ReleaseClaims(wagon, "mode change");
                    data.ShopEnhancement = this;
                }

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
            // ModeCycleKeyPressed requires the exact modifiers (so Ctrl+Shift+M,
            // the stats report, no longer also cycles a plain-M binding) and
            // ignores keys typed into a text field.
            if (!selected) return;
            if (ManifestDeliveryMod.ModeCycleKeyPressed())
                CycleMode();
        }

        // OnGUI floating label removed — mode state is now shown via the
        // in-window UI buttons (ModeButtonPatches).
    }
}
