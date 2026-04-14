using HarmonyLib;
using MelonLoader;
using UnityEngine;

// MelonLoader mod registration attributes (assembly-level)
[assembly: MelonInfo(typeof(WagonShopsEnhanced.WagonShopsEnhancedMod), "Wagon Shops Enhanced", "1.0.0", "WSE")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace WagonShopsEnhanced
{
    public class WagonShopsEnhancedMod : MelonMod
    {
        public static WagonShopsEnhancedMod Instance { get; private set; } = null!;

        // ── Return-trip backhaul ──────────────────────────────────────────────
        public static MelonPreferences_Entry<bool>  ReturnTripEnabled        { get; private set; } = null!;
        public static MelonPreferences_Entry<float> ReturnTripRadiusStandard { get; private set; } = null!;
        public static MelonPreferences_Entry<float> ReturnTripRadiusCamp     { get; private set; } = null!;
        public static MelonPreferences_Entry<float> ReturnTripRadiusHub      { get; private set; } = null!;

        // ── Wagon caps ────────────────────────────────────────────────────────
        public static MelonPreferences_Entry<int> MaxWagonsStandard { get; private set; } = null!;
        public static MelonPreferences_Entry<int> MaxWagonsCamp     { get; private set; } = null!;
        public static MelonPreferences_Entry<int> MaxWagonsHub      { get; private set; } = null!;

        // ── Camp stockyard ────────────────────────────────────────────────────
        public static MelonPreferences_Entry<bool>  CampHaulEnabled         { get; private set; } = null!;
        public static MelonPreferences_Entry<float> CampWorkRadius          { get; private set; } = null!;
        public static MelonPreferences_Entry<float> HubWorkRadius           { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  CampHaulFoodToHub       { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  CampHaulRawToSmokehouse { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  CampHaulIronToHub       { get; private set; } = null!;

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

            // ── Return-trip settings ─────────────────────────────────────────
            var cat = MelonPreferences.CreateCategory("WagonShopsEnhanced");

            ReturnTripEnabled = cat.CreateEntry(
                "ReturnTripEnabled", true,
                display_name: "Return Trip Enabled",
                description:  "When true, wagons search for nearby logistics requests at their " +
                              "drop-off point before driving back empty to the Wagon Shop.");

            ReturnTripRadiusStandard = cat.CreateEntry(
                "ReturnTripRadiusStandard", 120f,
                display_name: "Return Trip Radius – Standard",
                description:  "World-unit search radius for Standard mode shops.");

            ReturnTripRadiusCamp = cat.CreateEntry(
                "ReturnTripRadiusCamp", 200f,
                display_name: "Return Trip Radius – Camp",
                description:  "World-unit search radius for Camp mode shops. Larger because " +
                              "remote camps deliver to a distant hub.");

            ReturnTripRadiusHub = cat.CreateEntry(
                "ReturnTripRadiusHub", 150f,
                display_name: "Return Trip Radius – Hub",
                description:  "World-unit search radius for Hub mode shops.");

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
                "HubWorkRadius", 100f,
                display_name: "Hub Work Radius",
                description:  "World-unit radius around a Hub-mode Wagon Shop. Roughly " +
                              "200% of a Market radius — covers a town center area.");

            CampHaulFoodToHub = cat.CreateEntry(
                "CampHaulFoodToHub", true,
                display_name: "Camp Haul Food → Hub",
                description:  "Haul smoked/processed food from camp to hub root cellars and markets.");

            CampHaulRawToSmokehouse = cat.CreateEntry(
                "CampHaulRawToSmokehouse", true,
                display_name: "Camp Haul Raw → Smokehouse",
                description:  "Route raw meat and fish to smokehouses within the camp radius.");

            CampHaulIronToHub = cat.CreateEntry(
                "CampHaulIronToHub", true,
                display_name: "Camp Haul Iron → Hub",
                description:  "Haul iron ingots from camp smelters to hub storehouses.");

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
                LoggerInstance.Warning($"[WSE] Could not parse ModeCycleKey \"{ModeCycleKeyName.Value}\", defaulting to M.");

            // ── Apply Harmony patches ────────────────────────────────────────
            HarmonyInstance.PatchAll();

            LoggerInstance.Msg("Wagon Shops Enhanced 1.0.0 loaded.");
        }
    }
}
