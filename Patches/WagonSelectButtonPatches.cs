using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Injects two extra "Select Wagon" buttons (slots 3 and 4) into the
    /// WagonShop info window. The vanilla UI only exposes buttons for wagons
    /// 1 and 2, but Hub-mode shops can have up to 4 wagons — without this
    /// patch, wagons 3 and 4 are unclickable from the UI.
    ///
    /// Strategy:
    ///   - Clone the vanilla selectWagon1Container twice (preserves the
    ///     game's button sprite, layout, hover behaviour, tooltip hookup).
    ///   - Rewire each clone's Button.onClick to call the window's public
    ///     SelectWagon(int) method with index 2 or 3.
    ///   - Mirror vanilla visibility: show only when registeredWagons count
    ///     exceeds the button's index.
    ///
    /// Hooks:
    ///   - UIBuildingInfoWindow_New.SetTargetData (postfix) — inject on open
    ///   - UIBuildingInfoWindow_New.CheckSelectWagonContainerStatus (postfix)
    ///     — update visibility when wagon count changes mid-session
    /// </summary>
    internal static class WagonSelectButtonPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const string Extra3Name = "MD_SelectWagon3Container";
        private const string Extra4Name = "MD_SelectWagon4Container";

        /// <summary>
        /// Registers both Harmony postfixes. Called from Plugin.cs alongside
        /// the mode-button patch registration.
        /// </summary>
        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                System.Type windowType = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    windowType = asm.GetType("UIBuildingInfoWindow_New");
                    if (windowType != null) break;
                }
                if (windowType == null)
                {
                    ManifestDeliveryMod.Log.Warning(
                        "[MD] WagonSelectButtons: UIBuildingInfoWindow_New not found.");
                    return;
                }

                // Postfix on SetTargetData — inject when window opens for a
                // new target.
                var setTargetData = windowType.GetMethod("SetTargetData", AllInstance);
                if (setTargetData != null)
                {
                    var postfix = typeof(WagonSelectButtonPatches).GetMethod(
                        nameof(SetTargetDataPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(setTargetData, postfix: new HarmonyMethod(postfix));
                }

                // Postfix on CheckSelectWagonContainerStatus — keep our
                // extra-button visibility in sync with vanilla.
                var checkStatus = windowType.GetMethod(
                    "CheckSelectWagonContainerStatus", AllInstance);
                if (checkStatus != null)
                {
                    var postfix = typeof(WagonSelectButtonPatches).GetMethod(
                        nameof(CheckStatusPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(checkStatus, postfix: new HarmonyMethod(postfix));
                }

                ManifestDeliveryMod.Log.Msg(
                    "[MD] WagonSelectButtons: patches registered.");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] WagonSelectButtons register failed: {ex.Message}");
            }
        }

        private static void SetTargetDataPostfix(object __instance)
        {
            try
            {
                InjectIfWagonShop(__instance);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] WagonSelectButtons SetTargetData postfix error: {ex.Message}");
            }
        }

        private static void CheckStatusPostfix(object __instance)
        {
            try
            {
                UpdateExtraVisibility(__instance);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] WagonSelectButtons CheckStatus postfix error: {ex.Message}");
            }
        }

        // ── Core logic ───────────────────────────────────────────────────

        private static void InjectIfWagonShop(object windowInstance)
        {
            if (!TryGetShopContext(windowInstance, out WagonShop shop,
                out GameObject container1)) return;

            // Anchor clones AFTER the vanilla selectWagon2Container so the
            // stack reads 1, 2, 3, 4 in visual order.
            var type = windowInstance.GetType();
            var c2Field = type.GetField("selectWagon2Container", AllInstance);
            var container2 = c2Field?.GetValue(windowInstance) as GameObject;

            // Fall back to container1 if somehow container2 isn't there.
            var anchor = container2 != null ? container2 : container1;
            var parent = anchor.transform.parent;

            // Detect layout direction by comparing W1 and W2 sibling indices.
            // If W1 has HIGHER index than W2, this container renders bottom-up
            // (highest index at visual top); use negative offsets from W2.
            // If W1 has LOWER index, it renders top-down; use positive offsets.
            int c1Idx = container1.transform.GetSiblingIndex();
            int c2Idx = container2 != null ? container2.transform.GetSiblingIndex() : c1Idx;
            bool bottomUp = c1Idx > c2Idx;
            int sign = bottomUp ? -1 : 1;

            // Place Clone3 adjacent to the anchor (W2) and Clone4 one further,
            // in the direction that matches the layout so visual reads 1,2,3,4.
            EnsureClone(windowInstance, container1, parent, Extra3Name, 2, anchor, sign);
            EnsureClone(windowInstance, container1, parent, Extra4Name, 3, anchor, 2 * sign);

            // Rewire the vanilla W1 and W2 buttons to use our wainwright-indexed
            // selection (workersRO[N] → their assigned wagon) so clicking
            // button N always jumps to wainwright N's wagon.
            RewireVanillaButton(windowInstance, "selectWagon1Button", 0);
            RewireVanillaButton(windowInstance, "selectWagon2Button", 1);

            UpdateExtraVisibility(windowInstance);
        }

        /// <summary>
        /// Replaces the vanilla button's onClick with our wainwright-indexed
        /// handler so all four buttons follow the same mental model.
        /// </summary>
        private static void RewireVanillaButton(
            object windowInstance, string buttonField, int wainwrightIndex)
        {
            try
            {
                var field = windowInstance.GetType().GetField(buttonField, AllInstance);
                var btn = field?.GetValue(windowInstance) as Button;
                if (btn == null) return;

                DisablePersistentClicks(btn);
                btn.onClick.RemoveAllListeners();

                var capturedWindow = windowInstance;
                var capturedIndex = wainwrightIndex;
                btn.onClick.AddListener(() => SelectWainwrightsWagon(capturedWindow, capturedIndex));
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] RewireVanillaButton({buttonField}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Selects the wagon currently assigned to workersRO[wainwrightIndex].
        /// Falls back to registeredWagons[wainwrightIndex] if the worker has
        /// no assigned wagon (still constructing / just arrived).
        /// </summary>
        private static void SelectWainwrightsWagon(object windowInstance, int wainwrightIndex)
        {
            try
            {
                // Get the shop
                var type = windowInstance.GetType();
                var buildingField = type.GetField("building", AllInstance);
                var shop = buildingField?.GetValue(windowInstance) as WagonShop;
                if (shop == null) return;

                // Resolve target wagon: prefer worker→wagon mapping
                TransportWagon target = null;

                if (shop.workersRO != null && wainwrightIndex < shop.workersRO.Count)
                {
                    var worker = shop.workersRO[wainwrightIndex];
                    var assignedField = typeof(WagonShop).GetField(
                        "assignedWagonsByWorker", AllInstance);
                    var assignedDict = assignedField?.GetValue(shop)
                        as System.Collections.IDictionary;

                    if (assignedDict != null && worker != null && assignedDict.Contains(worker))
                        target = assignedDict[worker] as TransportWagon;
                }

                // Fallback: plain index into registeredWagons
                if (target == null
                    && shop.registeredWagonsRO != null
                    && wainwrightIndex < shop.registeredWagonsRO.Count)
                {
                    target = shop.registeredWagonsRO[wainwrightIndex];
                }

                if (target == null) return;

                // Select the wagon directly via InputManager.SelectGameObject
                // rather than going through vanilla's SelectWagon(index),
                // which would fail when the wagon is present on the shop but
                // NOT in registeredWagons (happens after cross-shop scavenge).
                var inputMgr = UnitySingleton<GameManager>.Instance?.inputManager;
                inputMgr?.SelectGameObject(target.gameObject, shouldFocus: true);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] SelectWainwrightsWagon({wainwrightIndex}) error: {ex.Message}");
            }
        }

        private static void UpdateExtraVisibility(object windowInstance)
        {
            // First, find the vanilla container regardless of target — we
            // may need to hide stale clones when the target isn't a WagonShop
            // (e.g., user viewed a shop then switched to a Storage Cart).
            var type = windowInstance.GetType();
            var containerField = type.GetField("selectWagon1Container", AllInstance);
            var container1 = containerField?.GetValue(windowInstance) as GameObject;
            if (container1 == null) return;

            var parent = container1.transform.parent;

            // If target isn't a WagonShop, force-hide our clones so they
            // don't linger from a previous WagonShop view.
            if (!TryGetShopContext(windowInstance, out WagonShop shop, out _))
            {
                var stale3 = parent.Find(Extra3Name);
                var stale4 = parent.Find(Extra4Name);
                if (stale3 != null) stale3.gameObject.SetActive(false);
                if (stale4 != null) stale4.gameObject.SetActive(false);
                return;
            }

            int count = shop.registeredWagonsRO.Count;

            // Also capture vanilla state for comparison
            var c2Field = type.GetField("selectWagon2Container", AllInstance);
            var c2 = c2Field?.GetValue(windowInstance) as GameObject;

            var e3 = parent.Find(Extra3Name);
            var e4 = parent.Find(Extra4Name);
            if (e3 != null) e3.gameObject.SetActive(count > 2);
            if (e4 != null) e4.gameObject.SetActive(count > 3);

        }

        /// <summary>
        /// Disables all persistent (inspector-serialized) listeners on a
        /// Button's onClick. Unity's Instantiate copies these, and
        /// RemoveAllListeners only clears runtime-added handlers, so without
        /// this the original SelectWagon(0) call keeps firing alongside
        /// ours and wins the final focus/selection.
        /// </summary>
        private static void DisablePersistentClicks(Button btn)
        {
            try
            {
                int count = btn.onClick.GetPersistentEventCount();
                for (int i = 0; i < count; i++)
                {
                    btn.onClick.SetPersistentListenerState(
                        i, UnityEngine.Events.UnityEventCallState.Off);
                }
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] DisablePersistentClicks failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Overrides the cloned button's tooltip text. The vanilla tooltip is
        /// driven by GenericTooltipDataProvider's toolTipRowKeyNames list;
        /// we replace its entries with our literal text (bypassing the
        /// localization lookup that would otherwise re-resolve to the
        /// inherited "Select Wagon 1" key).
        /// </summary>
        private static void OverrideTooltipText(GameObject clone, string text)
        {
            try
            {
                var provider = clone.GetComponentInChildren<GenericTooltipDataProvider>(true);
                if (provider == null) return;

                // Stop auto-re-localization from stomping our text
                var onLocMethod = typeof(I2.Loc.LocalizationManager).GetEvent("OnLocalizeEvent");
                // (Leaving the event alone; instead we overwrite the list after localize.)

                var keysField = typeof(GenericTooltipDataProvider).GetField(
                    "_toolTipRowKeyNames", AllInstance);
                var keys = keysField?.GetValue(provider) as System.Collections.Generic.List<string>;
                if (keys != null)
                {
                    keys.Clear();
                    keys.Add(text);
                }

                // Also overwrite the serialized localization tags so an
                // OnLocalize re-run doesn't restore the "Select Wagon 1" key.
                var tagsField = typeof(GenericTooltipDataProvider).GetField(
                    "tooltipRowKeyLocalizationTags", AllInstance);
                var tags = tagsField?.GetValue(provider) as System.Collections.Generic.List<string>;
                if (tags != null)
                    tags.Clear();

                // Clear the value side of the row too — the source button
                // had a "1" value that, combined with our new key, rendered
                // as "Select Wagon 4 1".
                var valuesField = typeof(GenericTooltipDataProvider).GetField(
                    "_toolTipRowValues", AllInstance);
                var values = valuesField?.GetValue(provider) as System.Collections.Generic.List<string>;
                if (values != null)
                    values.Clear();
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] OverrideTooltipText failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves the WagonShop target and vanilla container from the
        /// window instance via reflection. Returns false when the target
        /// isn't a WagonShop (no injection needed).
        /// </summary>
        private static bool TryGetShopContext(
            object windowInstance,
            out WagonShop shop,
            out GameObject container1)
        {
            shop = null;
            container1 = null;

            var type = windowInstance.GetType();

            var buildingField = type.GetField("building", AllInstance);
            var building = buildingField?.GetValue(windowInstance) as Building;
            shop = building as WagonShop;
            if (shop == null) return false;

            var containerField = type.GetField("selectWagon1Container", AllInstance);
            container1 = containerField?.GetValue(windowInstance) as GameObject;
            return container1 != null;
        }

        /// <summary>
        /// Clones the source container if no existing clone with the given
        /// name is found under the parent. Rewires the button click to call
        /// SelectWagon(wagonIndex) on the window instance.
        /// </summary>
        private static void EnsureClone(
            object windowInstance,
            GameObject source,
            Transform parent,
            string cloneName,
            int wagonIndex,
            GameObject anchor,
            int offsetFromAnchor)
        {
            var existing = parent.Find(cloneName);
            if (existing != null)
            {
                // Reposition in case anchor changed between builds / sessions.
                int anchorIdxExisting = anchor.transform.GetSiblingIndex();
                existing.SetSiblingIndex(anchorIdxExisting + offsetFromAnchor);
                return;
            }

            var clone = Object.Instantiate(source, parent, false);
            clone.name = cloneName;

            // Position relative to the anchor (selectWagon2Container) so the
            // visual stack reads W1, W2, W3, W4 top-to-bottom.
            int anchorIdx = anchor.transform.GetSiblingIndex();
            clone.transform.SetSiblingIndex(anchorIdx + offsetFromAnchor);

            // Rewire the first Button in the clone. Vanilla's onClick had
            // a persistent (inspector-set) listener calling SelectWagon(0)
            // that survives Instantiate + RemoveAllListeners. Disable the
            // persistent calls explicitly before adding our own.
            var btn = clone.GetComponentInChildren<Button>(true);
            if (btn != null)
            {
                DisablePersistentClicks(btn);
                btn.onClick.RemoveAllListeners();

                var capturedWindow = windowInstance;
                var capturedWainIdx = wagonIndex;  // 2 or 3
                btn.onClick.AddListener(() =>
                    SelectWainwrightsWagon(capturedWindow, capturedWainIdx));
            }

            // Override the tooltip text so it reads "Select Wagon 3" / "Select Wagon 4"
            // instead of the inherited "Select Wagon 1".
            OverrideTooltipText(clone, $"Select Wagon {wagonIndex + 1}");

            // Start hidden — visibility is controlled by UpdateExtraVisibility
            clone.SetActive(false);

            ManifestDeliveryMod.Log.Msg(
                $"[MD] WagonSelectButtons: cloned '{cloneName}' for wagon slot {wagonIndex + 1}");
        }
    }
}
