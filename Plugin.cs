using HarmonyLib;
using MelonLoader;
using UnityEngine;

// MelonLoader mod registration attributes (assembly-level)
[assembly: MelonInfo(typeof(ManifestDelivery.ManifestDeliveryMod), "Manifest Delivery", "1.0.3", "SageDragoon")]
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
                "CampWorkRadius", 60f,
                display_name: "Camp Work Radius",
                description:  "World-unit radius around a Camp-mode Wagon Shop. Roughly " +
                              "150% of a Fishing Shack radius — enough for a compact camp.");

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
                display_name: "Storage Cart Speed Multiplier",
                description:  "Multiplier for Transport Wagon movement speed when hauling " +
                              "to/from a Storage Cart in camp mode.");

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
            // ── Apply Harmony patches ────────────────────────────────────────
            HarmonyInstance.PatchAll();

            // ── Load saved shop modes from disk ─────────────────────────────
            Components.WagonShopEnhancement.LoadModesFromDisk();

            LoggerInstance.Msg("Manifest Delivery 1.0.3 loaded.");
        }
    }
}
