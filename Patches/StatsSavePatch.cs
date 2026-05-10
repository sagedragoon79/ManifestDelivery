using HarmonyLib;
using ManifestDelivery.Systems;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Postfix on SaveManager.Save that flushes per-shop hauling stats to
    /// disk alongside FF's own save. Same hook signature Pangu uses for its
    /// terrain-edit persistence.
    ///
    /// This isn't the only flush point — OnApplicationQuit and
    /// OnSceneWasLoaded also save, so we'd never lose data even if the user
    /// quits without saving. The Save hook just makes sure the stats
    /// snapshot matches the in-game save cadence.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save),
        new[] { typeof(string), typeof(bool), typeof(bool) })]
    internal static class StatsSavePatch
    {
        private static void Postfix(string savedGameFileNameNoExtension)
        {
            try
            {
                if (ManifestDeliveryMod.StatsEnabled == null) return;
                if (!ManifestDeliveryMod.StatsEnabled.Value) return;

                StatsTracker.SaveToDisk();
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][Stats] SaveToDisk failed: {ex.Message}");
            }
        }
    }
}
