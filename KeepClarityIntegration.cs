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
            bool restartRequired = false, int order = 0, Func<bool>? visibleWhen = null)
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
            Reg("Wagon Caps", ManifestDeliveryMod.MaxWagonsStandard,
                NewMeta("Max Wagons — Standard", min: 1, max: 4));
            Reg("Wagon Caps", ManifestDeliveryMod.MaxWagonsCamp,
                NewMeta("Max Wagons — Camp", min: 1, max: 4,
                    tooltip: "2 recommended: one hauls output, one returns supplies"));
            Reg("Wagon Caps", ManifestDeliveryMod.MaxWagonsHub,
                NewMeta("Max Wagons — Hub", min: 1, max: 6,
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
            Reg("Camp & Hub", ManifestDeliveryMod.CampHaulEnabled,
                NewMeta("Camp Proactive Haul",
                    "Camp wagons proactively haul from nearby production to hub storage"));
            Reg("Camp & Hub", ManifestDeliveryMod.CampWorkRadius,
                NewMeta("Camp Work Radius", min: 50f, max: 250f,
                    tooltip: "Default 120u covers a typical remote camp"));
            Reg("Camp & Hub", ManifestDeliveryMod.HubWorkRadius,
                NewMeta("Hub Work Radius", min: 80f, max: 400f,
                    tooltip: "Default 200u covers a full town center"));

            // === Storage Cart ===
            Reg("Storage Cart", ManifestDeliveryMod.StorageCartCapacity,
                NewMeta("Capacity", min: 100, max: 5000, tooltip: "Vanilla 750"));
            Reg("Storage Cart", ManifestDeliveryMod.StorageCartSpeedMult,
                NewMeta("Relocation Speed Multiplier", min: 0.5f, max: 5.0f,
                    tooltip: "How fast the cart 'drives' itself to a rally point"));

            // === Hotkeys ===
            Reg("Hotkeys", ManifestDeliveryMod.ModeCycleKeyName,
                NewMeta("Cycle Wagon Shop Mode",
                    "Unity KeyCode name. Cycles Standard / Camp / Hub while a Wagon Shop is selected."));
        }
    }
}
