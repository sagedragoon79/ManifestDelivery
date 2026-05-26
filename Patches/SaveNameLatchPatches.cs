using HarmonyLib;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Latches the authoritative save name at the two points where the game
    /// itself assigns <c>SaveManager.activeSaveFileName</c>:
    ///
    ///   • CESceneManager.LoadFromWithinGame(fileNameToLoad)
    ///       — loading a different save from inside a running game.
    ///   • StartSceneManager.StartGame(metaData, loadedGame)
    ///       — loading a save from the main menu (metaData.fileNameNoExtension).
    ///
    /// Both fire BEFORE the Frontier scene's buildings run Start/Finalize, so
    /// our WagonShopEnhancement persistence code is guaranteed a correct,
    /// non-empty save name and never falls back to default.txt due to a
    /// transiently-empty activeSaveFileName static.
    /// </summary>
    [HarmonyPatch]
    internal static class SaveNameLatchPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CESceneManager), nameof(CESceneManager.LoadFromWithinGame))]
        private static void LoadFromWithinGame_Postfix(string fileNameToLoad)
        {
            WagonShopEnhancement.LatchSaveName(fileNameToLoad);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartSceneManager), "StartGame",
            new System.Type[] { typeof(SavedGameMetaData), typeof(bool) })]
        private static void StartGame_Postfix(SavedGameMetaData metaData)
        {
            try
            {
                WagonShopEnhancement.LatchSaveName(metaData.fileNameNoExtension);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] StartGame save-name latch failed: {ex.Message}");
            }
        }
    }
}
