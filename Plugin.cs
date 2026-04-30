using HarmonyLib;
using MelonLoader;
using UnityEngine;

// MelonLoader mod registration attributes (assembly-level)
[assembly: MelonInfo(typeof(ManifestDelivery.ManifestDeliveryMod), "Manifest Delivery", "1.0.7", "SageDragoon")]
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
        public static MelonPreferences_Entry<float> CampWorkRadius  { get; private set; } = null!;
        public static MelonPreferences_Entry<float> HubWorkRadius   { get; private set; } = null!;

        // ── Storage Cart ──────────────────────────────────────────────────────
        public static MelonPreferences_Entry<int>   StorageCartCapacity  { get; private set; } = null!;
        public static MelonPreferences_Entry<float> StorageCartSpeedMult { get; private set; } = null!;

        // ── Mode cycling keybind ──────────────────────────────────────────────
        public static MelonPreferences_Entry<string> ModeCycleKeyName { get; private set; } = null!;

        // ── Resolved keybind (parsed from ModeCycleKeyName) ──────────────────
        private static KeyCode _modeCycleKey = KeyCode.M;
        public static KeyCode ModeCycleKey => _modeCycleKey;

        // ── Logger shortcut used throughout the mod ───────────────────────────
        public static MelonLogger.Instance Log => Instance.LoggerInstance;

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
                              "cycle between Standard / Camp / Hub modes. Use Unity KeyCode " +
                              "name (e.g. M, F, Tab).");

            // Parse keybind; fall back to M on failure.
            if (System.Enum.TryParse(ModeCycleKeyName.Value, ignoreCase: true, out KeyCode parsed))
                _modeCycleKey = parsed;
            else
                LoggerInstance.Warning($"[MD] Could not parse ModeCycleKey \"{ModeCycleKeyName.Value}\", defaulting to M.");

            // ── Apply Harmony patches ────────────────────────────────────────
            HarmonyInstance.PatchAll();

            // Manual Harmony patch for mode buttons — matches TW's working
            // pattern (attribute-based patch on UIBuildingInfoWindow had
            // silent-failure issues).
            Patches.ModeButtonPatches.Register(HarmonyInstance);
            Patches.WagonSelectButtonPatches.Register(HarmonyInstance);
            Patches.WagonShopAwakePrefix.Register(HarmonyInstance);

            // Modes are now loaded lazily per-save via EnsureLoadedForCurrentSave()
            // (called from RestoreSavedMode + GetSavedModeForPosition). Loading at
            // mod init would use an empty save-name, and the file would leak across
            // different save games — this sidesteps both.

            LoggerInstance.Msg("Manifest Delivery 1.0.7 loaded.");
        }
    }
}
