using System;
using System.Reflection;
using HarmonyLib;
using ManifestDelivery.Components;
using ManifestDelivery.Systems;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Save-cadence and town-change hooks. Registered manually, each in its own
    /// try/catch, so a renamed game method disables only that hook instead of
    /// aborting PatchAll.
    ///
    /// SAVE — postfix on <c>SaveManager.SaveInternal</c>. <c>SaveManager.Save</c>
    /// only starts a coroutine; a NEW town's save name (folder
    /// "Name_timestamp/") is created frames later inside SaveInternal. The old
    /// postfix on Save therefore flushed a new town's first save under the
    /// PREVIOUS town's name. Falls back to Save if SaveInternal is missing.
    ///
    /// TOWN CHANGE — clears MD's latched town on StartNewGame, RestartMap and
    /// RerollMap, and on SaveManager.Init itself. Init is what the game calls
    /// on every town change, but it's two lines long and may be inlined into
    /// its callers, which would skip a patch on it — hence the callers too.
    /// Loads re-latch the real name right after (SaveNameLatchPatches).
    /// </summary>
    internal static class SaveHooks
    {
        public static void Register(HarmonyLib.Harmony harmony)
        {
            var onSaved = new HarmonyMethod(AccessTools.Method(typeof(SaveHooks), nameof(OnGameSaved)));
            MethodBase? saveInternal = AccessTools.Method(typeof(SaveManager), "SaveInternal",
                new[] { typeof(string), typeof(bool) });
            if (saveInternal != null)
            {
                TryPatch(harmony, saveInternal, onSaved, "SaveManager.SaveInternal");
            }
            else
            {
                ManifestDeliveryMod.Log.Warning(
                    "[MD] SaveManager.SaveInternal not found — flushing on SaveManager.Save instead " +
                    "(a new town's first save may be written under the previous town).");
                TryPatch(harmony,
                    AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Save),
                        new[] { typeof(string), typeof(bool), typeof(bool) }),
                    onSaved, "SaveManager.Save");
            }

            var onTownChange = new HarmonyMethod(AccessTools.Method(typeof(SaveHooks), nameof(OnTownChange)));
            TryPatch(harmony, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Init)),
                onTownChange, "SaveManager.Init");
            TryPatch(harmony, AccessTools.Method(typeof(StartSceneManager), nameof(StartSceneManager.StartNewGame)),
                onTownChange, "StartSceneManager.StartNewGame");
            TryPatch(harmony, AccessTools.Method(typeof(CESceneManager), nameof(CESceneManager.RestartMap)),
                onTownChange, "CESceneManager.RestartMap");
            TryPatch(harmony, AccessTools.Method(typeof(CESceneManager), nameof(CESceneManager.RerollMap)),
                onTownChange, "CESceneManager.RerollMap");
        }

        private static void TryPatch(HarmonyLib.Harmony harmony, MethodBase? target,
            HarmonyMethod postfix, string label)
        {
            if (target == null)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] Save hook target not found: {label}");
                return;
            }
            try
            {
                harmony.Patch(target, postfix: postfix);
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] Save hook failed ({label}): {ex.Message}");
            }
        }

        /// <summary>Runs after the game has written a save (manual or auto) and named it.</summary>
        private static void OnGameSaved()
        {
            try
            {
                WagonShopEnhancement.OnGameSaved();
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD] Saving shop modes failed: {ex.Message}");
            }

            try
            {
                if (ManifestDeliveryMod.StatsEnabled != null && ManifestDeliveryMod.StatsEnabled.Value)
                    StatsTracker.OnGameSaved();
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][Stats] SaveToDisk failed: {ex.Message}");
            }

            // Storage priorities ride the same save cadence; separately gated.
            try
            {
                if (ManifestDeliveryMod.StoragePriorityEnabled != null
                    && ManifestDeliveryMod.StoragePriorityEnabled.Value)
                    StoragePriorityStore.SaveToDisk();
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] SaveToDisk failed: {ex.Message}");
            }
        }

        private static void OnTownChange() => WagonShopEnhancement.ResetSaveIdentity();
    }
}
