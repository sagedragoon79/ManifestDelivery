using HarmonyLib;
using MelonLoader;
using UnityEngine;

// MelonLoader mod registration attributes (assembly-level)
[assembly: MelonInfo(typeof(ManifestDelivery.ManifestDeliveryMod), "Manifest Delivery", "1.0.21", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace ManifestDelivery
{
    public class ManifestDeliveryMod : MelonMod
    {
        public static ManifestDeliveryMod Instance { get; private set; } = null!;

        // ── Master switch ─────────────────────────────────────────────────────
        public static MelonPreferences_Entry<bool>  ModEnabled               { get; private set; } = null!;

        // ── Return-trip backhaul ──────────────────────────────────────────────
        public static MelonPreferences_Entry<bool>  ReturnTripEnabled        { get; private set; } = null!;
        public static MelonPreferences_Entry<float> ReturnTripRadiusStandard { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  PreferWorkshopInput      { get; private set; } = null!;
        // Camp and Hub modes use their WorkRadius (CampWorkRadius / HubWorkRadius)
        // for ReturnTrip scans — keeping one radius per mode (the shop's service area).

        // ── Wagon caps ────────────────────────────────────────────────────────
        public static MelonPreferences_Entry<int> MaxWagonsStandard { get; private set; } = null!;
        public static MelonPreferences_Entry<int> MaxWagonsCamp     { get; private set; } = null!;
        public static MelonPreferences_Entry<int> MaxWagonsHub      { get; private set; } = null!;

        // ── Camp stockyard ────────────────────────────────────────────────────
        public static MelonPreferences_Entry<bool>  CampHaulEnabled { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  HubHaulEnabled  { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  HubMultiSourcePickup { get; private set; } = null!;
        public static MelonPreferences_Entry<int>   MinLoadPercent          { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  CampMultiPickup         { get; private set; } = null!;
        public static MelonPreferences_Entry<int>   CampMultiPickupMaxStops { get; private set; } = null!;
        public static MelonPreferences_Entry<float> CampMultiPickupDetour   { get; private set; } = null!;
        public static MelonPreferences_Entry<float> CampWorkRadius  { get; private set; } = null!;
        public static MelonPreferences_Entry<float> HubWorkRadius   { get; private set; } = null!;

        // ── Storage Cart ──────────────────────────────────────────────────────
        public static MelonPreferences_Entry<int>   StorageCartCapacity  { get; private set; } = null!;
        public static MelonPreferences_Entry<float> StorageCartSpeedMult { get; private set; } = null!;

        // ── Mode cycling keybind ──────────────────────────────────────────────
        public static MelonPreferences_Entry<string> ModeCycleKeyName { get; private set; } = null!;

        // ── Per-shop hauling stats ────────────────────────────────────────────
        public static MelonPreferences_Entry<bool>   StatsEnabled    { get; private set; } = null!;
        public static MelonPreferences_Entry<string> StatsReportKey  { get; private set; } = null!;
        private static KeyCode _statsReportKey = KeyCode.M;
        public static KeyCode StatsReportKey_Resolved => _statsReportKey;

        // ── Resolved keybind (parsed from ModeCycleKeyName) ──────────────────
        // Supports modifier combos like "Shift+M". The whole combo must match
        // exactly, so Ctrl+Shift+M (the stats report) doesn't also fire M.
        private static KeyCode _modeCycleKey = KeyCode.M;
        private static bool _modeCycleShift, _modeCycleCtrl, _modeCycleAlt;
        public static KeyCode ModeCycleKey => _modeCycleKey;

        /// <summary>
        /// True on the frame the mode-cycle combo is pressed: the key, with
        /// exactly the configured modifiers, while no text field has focus.
        /// </summary>
        public static bool ModeCycleKeyPressed()
        {
            if (!Input.GetKeyDown(_modeCycleKey)) return false;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt   = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (shift != _modeCycleShift || ctrl != _modeCycleCtrl || alt != _modeCycleAlt) return false;
            return !IsTypingInTextField();
        }

        private static bool IsTypingInTextField()
        {
            var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (selected == null) return false;
            var tmp = selected.GetComponent<TMPro.TMP_InputField>();
            if (tmp != null && tmp.isFocused) return true;
            var legacy = selected.GetComponent<UnityEngine.UI.InputField>();
            return legacy != null && legacy.isFocused;
        }

        /// <summary>
        /// Parses "M", "Shift+M", "Ctrl+Alt+F8" and similar. Returns false when
        /// the key part isn't a Unity KeyCode name.
        /// </summary>
        private static bool TryParseKeyCombo(string raw, out KeyCode key, out bool shift, out bool ctrl, out bool alt)
        {
            key = KeyCode.None; shift = ctrl = alt = false;
            if (string.IsNullOrEmpty(raw)) return false;
            var parts = raw.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "shift":                 shift = true; break;
                    case "ctrl": case "control":  ctrl  = true; break;
                    case "alt":                   alt   = true; break;
                    default: return false;
                }
            }
            return System.Enum.TryParse(parts[parts.Length - 1].Trim(), ignoreCase: true, out key)
                   && key != KeyCode.None;
        }

        // ── Verbose logging toggle ────────────────────────────────────────────
        public static MelonPreferences_Entry<bool> VerboseLogging { get; private set; } = null!;

        // ── Haul diagnostics (Approach A instrumentation) ─────────────────────
        public static MelonPreferences_Entry<bool> HaulDiagnostics { get; private set; } = null!;

        // ── Storage priorities (see _handoffs/…storage-priorities-fold.md) ────
        public static MelonPreferences_Entry<bool>   StoragePriorityEnabled  { get; private set; } = null!;
        public static MelonPreferences_Entry<float>  StoragePriorityStrength { get; private set; } = null!;

        // ── Logger shortcut used throughout the mod ───────────────────────────
        public static MelonLogger.Instance Log => Instance.LoggerInstance;

        /// <summary>
        /// Per-event diagnostic logging (DELIVER / CLAIM / EMPTY / start / park).
        /// Suppressed unless VerboseLogging is enabled — these fire many times
        /// per second on a busy map. Warnings and errors always log via Log.*.
        /// </summary>
        public static void LogVerbose(string message)
        {
            if (VerboseLogging != null && VerboseLogging.Value)
                Instance.LoggerInstance.Msg(message);
        }

        /// <summary>
        /// Guard for verbose lines that are costly to build (string formatting,
        /// request walks). LogVerbose alone still builds its argument when
        /// verbose logging is off.
        /// </summary>
        public static bool IsVerbose => VerboseLogging != null && VerboseLogging.Value;

        public override void OnInitializeMelon()
        {
            Instance = this;

            var cat = MelonPreferences.CreateCategory("ManifestDelivery");

            ModEnabled = cat.CreateEntry(
                "ModEnabled", true,
                display_name: "Mod Enabled",
                description:  "Master switch to enable/disable Manifest Delivery. Requires game restart.");

            if (!ModEnabled.Value)
            {
                LoggerInstance.Msg("Manifest Delivery is DISABLED via config.");
                return;
            }

            VerboseLogging = cat.CreateEntry(
                "VerboseLogging", false,
                display_name: "Verbose Logging",
                description:  "When true, logs every per-delivery and per-scan event " +
                              "(DELIVER / ReturnTrip / CampHaul / HubHaul CLAIM + EMPTY lines) " +
                              "to the MelonLoader log. Useful for diagnosing wagon routing, " +
                              "but noisy on a busy map. Warnings and errors always log " +
                              "regardless. Default false.");

            HaulDiagnostics = cat.CreateEntry(
                "HaulDiagnostics", false,
                display_name: "Haul Diagnostics",
                description:  "Diagnostic instrumentation. When true, dumps the full shape of " +
                              "every wagon haul the moment it is built — pickup (TakeOut) vs " +
                              "dropoff (Deliver) stops, items + counts, source/dest names, each " +
                              "served request's throttle params (maxTripsPerQuery / " +
                              "maxItemCountPerTrip / minItemCountForBulkTransport), and the " +
                              "wagon's carry capacity. Use it to see whether wagons already do " +
                              "multi-source pickups and which limiter caps them. Live toggle; " +
                              "near-zero cost when off. Default false.");

            // ── Storage priorities ───────────────────────────────────────────
            StoragePriorityEnabled = cat.CreateEntry(
                "StoragePriorityEnabled", false,
                display_name: "Storage Priorities (experimental)",
                description:  "EXPERIMENTAL. Adds a 1-9 hauling priority to storage buildings " +
                              "(9 highest, 5 = vanilla), set for the whole storage or per item " +
                              "from the building window. Haulers deliver to higher-priority " +
                              "storages first. Affects DESTINATION choice only — it never makes " +
                              "a storage attractive to empty, so it cannot ping-pong goods " +
                              "between storages. Default off.");

            StoragePriorityStrength = cat.CreateEntry(
                "StoragePriorityStrength", 150f,
                display_name: "Storage Priority — Strength",
                description:  "How hard priority pulls, in routing score points: priority 9 adds " +
                              "this much, 1 subtracts it, and each step from 5 is a quarter of " +
                              "it. Calibration: vanilla score is 0-100 (emptier ranks higher) and " +
                              "distance subtracts ~1 point per world unit, so at 150 a priority-9 " +
                              "storage wins over an equally full one ~150u closer. Granary/Root " +
                              "Cellar/Treasury carry a built-in +100. Priorities above 5 fade " +
                              "automatically as the storage fills.");

            // ── Return-trip settings ─────────────────────────────────────────
            ReturnTripEnabled = cat.CreateEntry(
                "ReturnTripEnabled", true,
                display_name: "Return Trip Enabled",
                description:  "When true, wagons search for nearby logistics requests at their " +
                              "drop-off point before driving back empty to the Wagon Shop.");

            ReturnTripRadiusStandard = cat.CreateEntry(
                "ReturnTripRadiusStandard", 120f,
                display_name: "Return Trip Radius – Standard",
                description:  "World-unit search radius for Standard mode shops.");

            PreferWorkshopInput = cat.CreateEntry(
                "PreferWorkshopInput", false,
                display_name: "Prefer Workshop Input on Backhaul",
                description:  "When true, after a drop-off the wagon prefers backhaul targets " +
                              "that feed PRODUCTION (Bakery, Smithy, Tannery, Carpenter, etc.) " +
                              "over storage shuffling (Storehouse, Storage Depot, Root Cellar, " +
                              "Granary, Marketplace). Workshops always win when in range, " +
                              "regardless of distance. Falls back to closest-storage when no " +
                              "workshops have requests. Default false (vanilla closest-wins).");

            // ── Wagon cap settings ───────────────────────────────────────────
            MaxWagonsStandard = cat.CreateEntry(
                "MaxWagonsStandard", 2,
                display_name: "Max Wagons – Standard",
                description:  "Max wagons a Standard Wagon Shop can produce (1–4).");

            MaxWagonsCamp = cat.CreateEntry(
                "MaxWagonsCamp", 2,
                display_name: "Max Wagons – Camp",
                description:  "Max wagons a Camp Wagon Shop can produce. " +
                              "Recommended 2: one hauls output, one brings supplies back.");

            MaxWagonsHub = cat.CreateEntry(
                "MaxWagonsHub", 4,
                display_name: "Max Wagons – Hub",
                description:  "Max wagons a Hub Wagon Shop can produce. Hub shops serve " +
                              "the whole settlement and benefit most from additional wagons.");

            // ── Camp stockyard settings ──────────────────────────────────────
            CampHaulEnabled = cat.CreateEntry(
                "CampHaulEnabled", true,
                display_name: "Camp Haul Enabled",
                description:  "When true, Camp-mode wagons proactively haul goods from " +
                              "nearby production buildings to hub storage.");

            HubHaulEnabled = cat.CreateEntry(
                "HubHaulEnabled", true,
                display_name: "Hub Haul Enabled",
                description:  "When true, Hub-mode wagons proactively distribute goods to " +
                              "ANY building with an active request within the Hub work radius " +
                              "(markets, shelters/residences, producers, storages). Without " +
                              "this, Hub wagons only do opportunistic backhaul after a vanilla-" +
                              "assigned delivery. Default true.");

            HubMultiSourcePickup = cat.CreateEntry(
                "HubMultiSourcePickup", false,
                display_name: "Hub Multi-Source Pickup (experimental)",
                description:  "EXPERIMENTAL. When true, Hub-mode wagons claim only DELIVER " +
                              "(restock) requests — never producer TakeOut requests — and claim " +
                              "the specific delivery request rather than the whole building. A " +
                              "Deliver-shaped claim routes through the game's multi-source path " +
                              "(FindBestRouteDeliver), so one wagon fans across several source " +
                              "storages up to carry capacity before delivering, instead of one " +
                              "near-empty pickup per trip. Hub mode only (Camp is unaffected). " +
                              "Default false — flip on to test, watch Haul Diagnostics for " +
                              "multi-PICKUP hauls.");

            // ── Wagon efficiency ─────────────────────────────────────────────
            MinLoadPercent = cat.CreateEntry(
                "MinLoadPercent", 20,
                display_name: "Minimum Wagon Load (%)",
                description:  "MD only sends a wagon for a job when it fills at least this " +
                              "percent of the wagon's carry capacity (by weight). Stops wagons " +
                              "crossing town for a handful of items; smaller jobs are left to " +
                              "villagers or wait until they grow. Camp supplies (firewood and " +
                              "food for camp homes) are exempt. With Camp Multi-Pickup on, a " +
                              "Camp wagon counts the same item at nearby camp producers too. " +
                              "0 turns the rule off. Default 20.");

            CampMultiPickup = cat.CreateEntry(
                "CampMultiPickup", false,
                display_name: "Camp Multi-Pickup (experimental)",
                description:  "EXPERIMENTAL. When a Camp wagon picks up a producer's output, " +
                              "it also stops at other camp producers of the same item on the " +
                              "way, up to its carry capacity and the destination's free space, " +
                              "then makes one trip to storage. The extra stops reserve their " +
                              "items the same way the game's own trips do. Default false.");

            CampMultiPickupMaxStops = cat.CreateEntry(
                "CampMultiPickupMaxStops", 3,
                display_name: "Camp Multi-Pickup — Max Extra Stops",
                description:  "How many extra producers a Camp wagon may visit on one trip. " +
                              "Default 3.");

            CampMultiPickupDetour = cat.CreateEntry(
                "CampMultiPickupDetour", 80f,
                display_name: "Camp Multi-Pickup — Max Detour",
                description:  "An extra stop must be within this many world units of the " +
                              "producer the trip was planned for. Keeps the extra stops from " +
                              "zig-zagging across a large camp. Default 80.");

            CampWorkRadius = cat.CreateEntry(
                "CampWorkRadius", 120f,
                display_name: "Camp Work Radius",
                description:  "World-unit radius around a Camp-mode Wagon Shop. Default " +
                              "120u covers a typical remote camp (hunter + smokehouse + " +
                              "forager + small mine) comfortably.");

            HubWorkRadius = cat.CreateEntry(
                "HubWorkRadius", 200f,
                display_name: "Hub Work Radius",
                description:  "World-unit radius around a Hub-mode Wagon Shop. " +
                              "Covers a full town center area including outlying crafters.");

            // ── Storage Cart settings ───────────────────────────────────────
            StorageCartCapacity = cat.CreateEntry(
                "StorageCartCapacity", 1500,
                display_name: "Storage Cart Capacity",
                description:  "Override Storage Cart (SupplyWagon) item capacity. Vanilla is 750.");

            StorageCartSpeedMult = cat.CreateEntry(
                "StorageCartSpeedMult", 1.5f,
                display_name: "Storage Cart Relocation Speed Multiplier",
                description:  "Storage Carts are classified as buildings, not wagons. When " +
                              "the player clicks the 'rally to point' button the game plays " +
                              "a movement animation but internally issues a building " +
                              "relocation. This multiplier scales that relocation speed " +
                              "(i.e. how fast the cart 'drives' itself to the rally point). " +
                              "Does NOT affect Transport Wagons hauling items to/from carts.");

            // ── Mode cycling key ─────────────────────────────────────────────
            ModeCycleKeyName = cat.CreateEntry(
                "ModeCycleKey", "M",
                display_name: "Mode Cycle Key",
                description:  "While a Wagon Shop's info window is open, press this key to " +
                              "cycle between Standard / Camp / Hub modes. A Unity KeyCode " +
                              "name, optionally with modifiers (e.g. M, Shift+M, Ctrl+F8).");

            // Parse keybind; fall back to plain M on failure.
            if (TryParseKeyCombo(ModeCycleKeyName.Value, out KeyCode parsed,
                    out _modeCycleShift, out _modeCycleCtrl, out _modeCycleAlt))
                _modeCycleKey = parsed;
            else
            {
                _modeCycleKey = KeyCode.M;
                _modeCycleShift = _modeCycleCtrl = _modeCycleAlt = false;
                LoggerInstance.Warning($"[MD] Could not parse ModeCycleKey \"{ModeCycleKeyName.Value}\", defaulting to M.");
            }

            // ── Per-shop hauling stats ───────────────────────────────────────
            StatsEnabled = cat.CreateEntry(
                "StatsEnabled", true,
                display_name: "Hauling Stats — Enabled",
                description:  "When true, the mod records every wagon delivery into a " +
                              "per-shop stats file (lifetime + year-to-date totals, " +
                              "per-item breakdown, raw vs produced split). Press the " +
                              "report key (default Ctrl+Shift+M) to dump a formatted " +
                              "report to the MelonLoader log.");

            StatsReportKey = cat.CreateEntry(
                "StatsReportKey", "M",
                display_name: "Hauling Stats — Report Key",
                description:  "Press CTRL+SHIFT+<this key> to dump the per-shop hauling " +
                              "report to the MelonLoader log. Use Unity KeyCode name " +
                              "(e.g. M, J, F8).");

            if (System.Enum.TryParse(StatsReportKey.Value, ignoreCase: true, out KeyCode statsKey))
                _statsReportKey = statsKey;
            else
                LoggerInstance.Warning($"[MD] Could not parse StatsReportKey \"{StatsReportKey.Value}\", defaulting to M.");

            // ── Apply Harmony patches ────────────────────────────────────────
            HarmonyInstance.PatchAll();

            // Manual Harmony patch for mode buttons — matches TW's working
            // pattern (attribute-based patch on UIBuildingInfoWindow had
            // silent-failure issues).
            Patches.ModeButtonPatches.Register(HarmonyInstance);
            Patches.SaveHooks.Register(HarmonyInstance);
            Patches.StoragePriorityUIPatches.Register(HarmonyInstance);
            Patches.WagonSelectButtonPatches.Register(HarmonyInstance);
            Patches.WagonShopAwakePrefix.Register(HarmonyInstance);

            // Modes are now loaded lazily per-save via EnsureLoadedForCurrentSave()
            // (called from RestoreSavedMode + GetSavedModeForPosition). Loading at
            // mod init would use an empty save-name, and the file would leak across
            // different save games — this sidesteps both.

            LoggerInstance.Msg("Manifest Delivery 1.0.21 loaded.");

            // Optional: register with Keep Clarity's settings panel if installed.
            KeepClarityIntegration.TryRegisterAll();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Drop the per-requester storage-classification cache between
            // saves — its keys are LogisticsRequester references that get
            // replaced on map reload, and a Unity-null Object key still
            // hashes (would leak slowly across multiple loads).
            ManifestDelivery.Tasks.ReturnTripSearchEntry.ClearStorageCache();

            // Drop the Hub multi-source claim map — its keys are ItemRequest
            // references from the previous map that get replaced on reload.
            ManifestDelivery.Tasks.HubHaulSearchEntry.ClearHubClaims();

            // Storage-priority caches key on instance IDs / bucket refs, which
            // don't survive a map reload; the store reloads per save.
            ManifestDelivery.Patches.StoragePriorityPatches.ClearCaches();
            ManifestDelivery.Patches.StoragePriorityUIPatches.ClearCaches();
            ManifestDelivery.Systems.StoragePriorityStore.Clear();

            // Stats are per-save: drop in-memory snapshot so the next
            // delivery on a different save doesn't append onto the previous
            // save's totals. Reload happens lazily on first delivery.
            ManifestDelivery.Systems.StatsTracker.Clear();
        }

        public override void OnUpdate()
        {
            // Stats report keybind: CTRL+SHIFT+<configured key>
            if (StatsEnabled != null && StatsEnabled.Value
                && Input.GetKeyDown(_statsReportKey)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                ManifestDelivery.Systems.StatsTracker.DumpReportToLog();
            }
        }

        public override void OnApplicationQuit()
        {
            // Flush stats to disk on game exit so we don't lose unsaved data
            // when the player quits without triggering SaveManager.Save.
            try { ManifestDelivery.Systems.StatsTracker.SaveToDisk(); }
            catch { /* best effort on shutdown */ }

            try
            {
                if (StoragePriorityEnabled != null && StoragePriorityEnabled.Value)
                    ManifestDelivery.Systems.StoragePriorityStore.SaveToDisk();
            }
            catch { /* best effort on shutdown */ }
        }
    }
}
