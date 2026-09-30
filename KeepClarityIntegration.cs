using System;
using System.Reflection;
using MelonLoader;

namespace ManifestDelivery
{
    /// <summary>
    /// Optional integration with Keep Clarity's settings panel. No-op when
    /// KeepClarity.dll is absent. All access reflective — no compile-time dep.
    /// </summary>
    internal static class KeepClarityIntegration
    {
        private static bool _resolved;
        private static bool _present;
        private static MethodInfo? _registerMod;
        private static MethodInfo? _registerEntry;
        private static Type? _settingsMetaType;

        private const string ModId = "ManifestDelivery";
        private const string ModDisplayName = "Manifest Delivery";

        public static void TryRegisterAll()
        {
            if (!ResolveApi()) return;
            try
            {
                RegisterMod();
                RegisterEntries();
                MelonLogger.Msg("[MD] Registered with Keep Clarity settings panel");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MD] Keep Clarity registration failed: {ex.Message}");
            }
        }

        private static bool ResolveApi()
        {
            if (_resolved) return _present;
            _resolved = true;
            var apiType = Type.GetType("FFUIOverhaul.Settings.SettingsAPI, KeepClarity");
            if (apiType == null) { _present = false; return false; }
            _settingsMetaType = Type.GetType("FFUIOverhaul.Settings.SettingsMeta, KeepClarity");
            if (_settingsMetaType == null) { _present = false; return false; }
            _registerMod = apiType.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Register" && m.IsGenericMethodDefinition) { _registerEntry = m; break; }
            _present = _registerMod != null && _registerEntry != null;
            return _present;
        }

        private static void RegisterMod()
        {
            _registerMod!.Invoke(null, new object?[] {
                ModId, ModDisplayName,
                "Wagon Shop overhaul: Camp/Hub modes, return-trip backhaul AI, Storage Cart buffs",
                /*version*/ null,
                /*iconResourcePath*/ null,
                /*accentRgb — wagon tan*/ new[] { 0.70f, 0.55f, 0.30f, 1f },
                /*order*/ 20
            });
        }

        private static object NewMeta(string? label = null, string? tooltip = null,
            object? min = null, object? max = null, string? group = null,
            bool restartRequired = false, bool reloadRequired = false,
            int order = 0, Func<bool>? visibleWhen = null)
        {
            var m = Activator.CreateInstance(_settingsMetaType!);
            void Set(string field, object? value)
            {
                var f = _settingsMetaType!.GetField(field);
                if (f != null) f.SetValue(m, value);
            }
            Set("Label", label);
            Set("Tooltip", tooltip);
            Set("Min", min);
            Set("Max", max);
            Set("Group", group);
            Set("RestartRequired", restartRequired);
            Set("ReloadRequired", reloadRequired);
            Set("Order", order);
            Set("VisibleWhen", visibleWhen);
            return m!;
        }

        private static void Reg<T>(string category, MelonPreferences_Entry<T> entry, object meta)
        {
            var closed = _registerEntry!.MakeGenericMethod(typeof(T));
            closed.Invoke(null, new object?[] { ModId, ModDisplayName, category, entry, meta });
        }

        private static void RegisterEntries()
        {
            // === Master ===
            Reg("Master", ManifestDeliveryMod.ModEnabled,
                NewMeta("Mod Enabled", "Disable to fall back to vanilla Wagon Shop behavior", restartRequired: true));

            // === Wagon Caps ===
            // reloadRequired: the worker-slot count is provisioned in
            // WagonShopEnhancement.UpdateWorkerSlots, which runs on map load and
            // on mode change — not re-evaluated live. (The wagon-production
            // ceiling is read live, but the visible slot count bakes on load.)
            Reg("Wagon Caps", ManifestDeliveryMod.MaxWagonsStandard,
                NewMeta("Max Wagons — Standard", min: 1, max: 4, reloadRequired: true));
            Reg("Wagon Caps", ManifestDeliveryMod.MaxWagonsCamp,
                NewMeta("Max Wagons — Camp", min: 1, max: 4, reloadRequired: true,
                    tooltip: "2 recommended: one hauls output, one returns supplies"));
            Reg("Wagon Caps", ManifestDeliveryMod.MaxWagonsHub,
                NewMeta("Max Wagons — Hub", min: 1, max: 6, reloadRequired: true,
                    tooltip: "Hub shops serve the whole settlement"));

            // === Return-trip / backhaul ===
            Reg("Backhaul AI", ManifestDeliveryMod.ReturnTripEnabled,
                NewMeta("Return-trip Pickup",
                    "Wagons search for nearby logistics requests at drop-off before returning empty"));
            Reg("Backhaul AI", ManifestDeliveryMod.ReturnTripRadiusStandard,
                NewMeta("Return-trip Search Radius", min: 30f, max: 300f,
                    visibleWhen: () => ManifestDeliveryMod.ReturnTripEnabled.Value));
            Reg("Backhaul AI", ManifestDeliveryMod.PreferWorkshopInput,
                NewMeta("Prefer Workshops on Backhaul",
                    "Prefer feeding production buildings (Bakery, Smithy, Tannery, etc.) over storage shuffling",
                    visibleWhen: () => ManifestDeliveryMod.ReturnTripEnabled.Value));

            // === Camp / Hub ===
            // All live: the gating flags (IsCampHaulActive / IsHubHaulActive)
            // and the work radii are re-read on every CampHaul/HubHaul scan.
            Reg("Camp & Hub", ManifestDeliveryMod.CampHaulEnabled,
                NewMeta("Camp Proactive Haul",
                    "Camp wagons proactively haul from nearby production to hub storage"));
            Reg("Camp & Hub", ManifestDeliveryMod.HubHaulEnabled,
                NewMeta("Hub Proactive Distribution",
                    "Hub wagons proactively serve any request in radius (markets, shelters, producers, storage)"));
            Reg("Camp & Hub", ManifestDeliveryMod.HubMultiSourcePickup,
                NewMeta("Hub Multi-Source Pickup (experimental)",
                    "Hub wagons claim only Deliver/restock requests (the specific request, not the whole building), " +
                    "so one wagon fans across several source storages up to capacity instead of one near-empty pickup. " +
                    "Hub mode only. Off by default — experimental."));
            Reg("Camp & Hub", ManifestDeliveryMod.CampWorkRadius,
                NewMeta("Camp Work Radius", min: 50f, max: 250f,
                    tooltip: "Default 120u covers a typical remote camp"));
            Reg("Camp & Hub", ManifestDeliveryMod.HubWorkRadius,
                NewMeta("Hub Work Radius", min: 80f, max: 400f,
                    tooltip: "Default 200u covers a full town center"));

            // === Storage Cart ===
            // Capacity is baked in SupplyWagon.Start (read once when the cart
            // spins up), so a change applies on reload / to newly-built carts.
            Reg("Storage Cart", ManifestDeliveryMod.StorageCartCapacity,
                NewMeta("Capacity", min: 100, max: 5000, reloadRequired: true,
                    tooltip: "Vanilla 750"));
            // Speed mult is read live in get_movementSpeed — applies immediately.
            Reg("Storage Cart", ManifestDeliveryMod.StorageCartSpeedMult,
                NewMeta("Relocation Speed Multiplier", min: 0.5f, max: 5.0f,
                    tooltip: "How fast the cart 'drives' itself to a rally point"));

            // === Wagon Efficiency ===
            // Live: read on every claim scan and every route build.
            Reg("Wagon Efficiency", ManifestDeliveryMod.MinLoadPercent,
                NewMeta("Minimum Wagon Load (%)", min: 0, max: 100,
                    tooltip: "MD only sends a wagon for a job that fills at least this share of it " +
                             "(by weight). Smaller jobs are left to villagers or wait until they grow. " +
                             "Camp supplies for camp homes are exempt. 0 turns it off. Default 20."));
            Reg("Wagon Efficiency", ManifestDeliveryMod.CampMultiPickup,
                NewMeta("Camp Multi-Pickup (experimental)",
                    "Camp wagons also stop at other camp producers of the same item on the way, " +
                    "then make one trip to storage with a fuller load."));
            Reg("Wagon Efficiency", ManifestDeliveryMod.CampMultiPickupMaxStops,
                NewMeta("Max Extra Stops", min: 1, max: 6,
                    tooltip: "How many extra producers a Camp wagon may visit on one trip. Default 3.",
                    visibleWhen: () => ManifestDeliveryMod.CampMultiPickup.Value));
            Reg("Wagon Efficiency", ManifestDeliveryMod.CampMultiPickupDetour,
                NewMeta("Max Detour", min: 20f, max: 250f,
                    tooltip: "An extra stop must be within this many units of the original pickup. Default 80.",
                    visibleWhen: () => ManifestDeliveryMod.CampMultiPickup.Value));

            // === Storage Priorities ===
            // Live: the routing postfix reads both on every score, and the
            // window rows re-check the toggle each time a window opens.
            Reg("Storage Priorities", ManifestDeliveryMod.StoragePriorityEnabled,
                NewMeta("Storage Priorities (experimental)",
                    "Adds a 1-9 hauling priority to storage buildings (9 highest, 5 = vanilla). " +
                    "Set it for the whole storage in the building window, or per item by clicking " +
                    "an item's icon. Haulers deliver to higher-priority storages first. It only " +
                    "chooses where goods go, never pulls them back out, so goods can't bounce " +
                    "between storages."));
            Reg("Storage Priorities", ManifestDeliveryMod.StoragePriorityStrength,
                NewMeta("Priority Strength", min: 25f, max: 400f,
                    tooltip: "Routing points that priority 9 adds and priority 1 subtracts; each step " +
                             "from 5 is a quarter of this. About 1 point per unit of travel. Default 150.",
                    visibleWhen: () => ManifestDeliveryMod.StoragePriorityEnabled.Value));

            // === Hotkeys ===
            // restartRequired: the key string is parsed into _modeCycleKey once
            // in OnInitializeMelon; the resolved KeyCode is what's read live.
            Reg("Hotkeys", ManifestDeliveryMod.ModeCycleKeyName,
                NewMeta("Cycle Wagon Shop Mode", restartRequired: true,
                    tooltip: "A Unity KeyCode name, optionally with modifiers (M, Shift+M, Ctrl+F8). " +
                             "Cycles Standard / Camp / Hub while a Wagon Shop is selected."));

            // === Diagnostics ===
            // Live — the pref is re-read on every wagon haul build (OnSearchSuccess).
            Reg("Diagnostics", ManifestDeliveryMod.HaulDiagnostics,
                NewMeta("Haul Diagnostics",
                    "Logs the full shape of each wagon haul (pickup/dropoff stops, items, " +
                    "served-request throttle params, carry capacity) to the MelonLoader log. " +
                    "Investigative tool for multi-pickup tuning. Off by default."));
        }
    }
}
