using HarmonyLib;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ManifestDelivery;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Injects Camp / Hub / Standard mode buttons into the WagonShop's
    /// building info window. Uses the same pattern as Tended Wilds:
    /// manual Harmony postfix on the window's SetTargetData, __instance
    /// gives us the window directly — no searching for pooled UI objects.
    /// </summary>
    internal static class ModeButtonPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const string ButtonRowName = "MD_ModeButtonRow";

        /// <summary>
        /// Registers the manual Harmony patch. Called from Plugin.cs after
        /// PatchAll(). Manual patching matches TW's proven pattern and avoids
        /// the silent-failure issues we saw with attribute-based patches on
        /// this particular UI class.
        /// </summary>
        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                // The game uses UIBuildingInfoWindow_New (NOT UIBuildingInfoWindow
                // — that's legacy). Patch its SetTargetData override directly.
                var newWindowType = System.Type.GetType(
                    "UIBuildingInfoWindow_New, Assembly-CSharp");
                if (newWindowType == null)
                {
                    // Try loaded assemblies as fallback
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        newWindowType = asm.GetType("UIBuildingInfoWindow_New");
                        if (newWindowType != null) break;
                    }
                }
                if (newWindowType == null)
                {
                    ManifestDeliveryMod.Log.Warning(
                        "[MD] ModeButton: UIBuildingInfoWindow_New type not found.");
                    return;
                }

                var setTargetData = newWindowType.GetMethod(
                    "SetTargetData", AllInstance);
                if (setTargetData == null)
                {
                    ManifestDeliveryMod.Log.Warning(
                        "[MD] ModeButton: SetTargetData not found on _New.");
                    return;
                }

                var postfix = typeof(ModeButtonPatches).GetMethod(
                    nameof(SetTargetDataPostfix_New),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(setTargetData, postfix: new HarmonyMethod(postfix));
                ManifestDeliveryMod.Log.Msg(
                    "[MD] ModeButton: Patched UIBuildingInfoWindow_New.SetTargetData");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ModeButton register failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on UIBuildingInfoWindow.SetTargetData. Fires whenever the
        /// window is populated for a new target. If the target is a WagonShop
        /// with our enhancement, inject mode buttons.
        /// </summary>
        private static void SetTargetDataPostfix_New(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                // Read the `building` private field on UIBuildingInfoWindow_New
                var buildingField = __instance.GetType().GetField(
                    "building", AllInstance);
                var building = buildingField?.GetValue(__instance) as Building;
                if (building == null)
                {
                    RemoveButtonRow(comp.transform);
                    return;
                }

                // Only inject for WagonShops that have our enhancement
                var enhancement = building.GetComponent<WagonShopEnhancement>();
                if (enhancement == null)
                {
                    // Not a wagon shop — remove any leftover button row if present
                    RemoveButtonRow(comp.transform);
                    return;
                }

                InjectButtons(comp, enhancement);
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] ModeButton SetTargetData postfix error: {ex.Message}");
            }
        }

        private static void RemoveButtonRow(Transform root)
        {
            var existing = root.Find(ButtonRowName);
            if (existing != null)
                Object.Destroy(existing.gameObject);
            HideTooltip();
        }

        private static void InjectButtons(
            Component window, WagonShopEnhancement enhancement)
        {
            // Destroy old row and recreate (handles re-entry cleanly)
            RemoveButtonRow(window.transform);

            // Find a TMP font from any existing text in the window for styling
            TMP_FontAsset gameFont = null;
            float gameFontSize = 14f;
            var existingText = window.GetComponentInChildren<TextMeshProUGUI>(true);
            if (existingText != null)
            {
                gameFont = existingText.font;
                gameFontSize = existingText.fontSize;
            }

            // Parent row to the window itself. Absolute positioned (ignoreLayout)
            // so it does NOT disturb vanilla layout — same trick TW uses.
            var row = new GameObject(ButtonRowName);
            row.transform.SetParent(window.transform, false);

            var rowRT = row.AddComponent<RectTransform>();
            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.ignoreLayout = true;

            // Anchor to top-center of the window, sit just below the title bar.
            // buttonsPanel is top-right; we sit as a strip under the header.
            rowRT.anchorMin = new Vector2(0.5f, 1f);
            rowRT.anchorMax = new Vector2(0.5f, 1f);
            rowRT.pivot = new Vector2(0.5f, 1f);
            rowRT.anchoredPosition = new Vector2(0f, -50f);
            rowRT.sizeDelta = new Vector2(330f, 30f);

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.padding = new RectOffset(4, 4, 2, 2);

            CreateModeButton(row.transform, "Standard", ShopMode.Standard,
                enhancement, gameFont, gameFontSize);
            CreateModeButton(row.transform, "Camp", ShopMode.Camp,
                enhancement, gameFont, gameFontSize);
            CreateModeButton(row.transform, "Hub", ShopMode.Hub,
                enhancement, gameFont, gameFontSize);

            ManifestDeliveryMod.Log.Msg(
                $"[MD] ModeButton: Injected row into '{window.gameObject.name}'");
        }

        // ── Gold palette ─────────────────────────────────────────────────
        private static readonly Color GoldBright = new Color(0.95f, 0.82f, 0.35f, 1f);
        private static readonly Color GoldMuted  = new Color(0.55f, 0.45f, 0.22f, 1f);
        private static readonly Color BgActive   = new Color(0.18f, 0.26f, 0.12f, 0.95f);
        private static readonly Color BgInactive = new Color(0.10f, 0.09f, 0.07f, 0.90f);

        private static void CreateModeButton(Transform parent, string label,
            ShopMode mode, WagonShopEnhancement enhancement,
            TMP_FontAsset font, float fontSize)
        {
            bool isActive = enhancement.Mode == mode;

            // Outer frame = gold border (solid color fill visible 2px around inner)
            var btnObj = new GameObject($"MD_ModeBtn_{label}");
            btnObj.transform.SetParent(parent, false);

            var borderImg = btnObj.AddComponent<Image>();
            borderImg.color = isActive ? GoldBright : GoldMuted;
            borderImg.raycastTarget = true;

            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredHeight = 28f;
            le.flexibleWidth = 1f;
            le.minWidth = 90f;

            // Inner background = mode-indicator color, inset 2px from border
            var innerObj = new GameObject("Inner");
            innerObj.transform.SetParent(btnObj.transform, false);
            var innerRT = innerObj.AddComponent<RectTransform>();
            innerRT.anchorMin = Vector2.zero;
            innerRT.anchorMax = Vector2.one;
            innerRT.offsetMin = new Vector2(2f, 2f);
            innerRT.offsetMax = new Vector2(-2f, -2f);

            var innerImg = innerObj.AddComponent<Image>();
            innerImg.color = isActive ? BgActive : BgInactive;
            innerImg.raycastTarget = false;

            // Label
            var textObj = new GameObject("Label");
            textObj.transform.SetParent(innerObj.transform, false);
            var textRT = textObj.AddComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero;
            textRT.offsetMax = Vector2.zero;

            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = fontSize * 0.90f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.text = label;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = isActive ? GoldBright : GoldMuted;
            tmp.raycastTarget = false;
            // Subtle dark outline to pop against bg
            tmp.outlineWidth = 0.15f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);

            // EventTrigger: click + hover tooltip
            var trigger = btnObj.AddComponent<EventTrigger>();
            var capturedParent = parent;
            var capturedEnh = enhancement;
            var capturedMode = mode;
            var capturedBtn = btnObj;

            // Click
            var clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            clickEntry.callback.AddListener((data) =>
            {
                capturedEnh.Mode = capturedMode;
                ManifestDeliveryMod.Log.Msg(
                    $"[MD] Mode button clicked: {capturedMode}");
                RefreshButtonColors(capturedParent, capturedEnh);
            });
            trigger.triggers.Add(clickEntry);

            // Hover enter — show tooltip
            var enterEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enterEntry.callback.AddListener((data) =>
            {
                ShowTooltip(capturedBtn.transform, GetTooltipText(capturedMode, capturedEnh), font);
            });
            trigger.triggers.Add(enterEntry);

            // Hover exit — hide tooltip
            var exitEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exitEntry.callback.AddListener((data) => HideTooltip());
            trigger.triggers.Add(exitEntry);
        }

        // ── Tooltip ──────────────────────────────────────────────────────
        private static GameObject _tooltipObj = null;
        private static TextMeshProUGUI _tooltipText = null;

        private static string GetTooltipText(ShopMode mode, WagonShopEnhancement enh)
        {
            switch (mode)
            {
                case ShopMode.Standard:
                    return "<b>Standard Shop</b>\n" +
                           $"Max wagons: {ManifestDeliveryMod.MaxWagonsStandard.Value}\n" +
                           $"Backhaul radius: {ManifestDeliveryMod.ReturnTripRadiusStandard.Value:F0}u\n" +
                           "<i>Vanilla hauling plus return-trip backhaul — wagons " +
                           "scan for nearby work at drop-off before heading home empty.</i>";
                case ShopMode.Camp:
                    return "<b>Camp Shop</b>\n" +
                           $"Max wagons: {ManifestDeliveryMod.MaxWagonsCamp.Value}\n" +
                           $"Work radius: {ManifestDeliveryMod.CampWorkRadius.Value:F0}u\n" +
                           "Speed: +25%\n" +
                           "<i>Local logistics hub — wagons proactively haul from " +
                           "producers inside the ring to main-town storage.</i>";
                case ShopMode.Hub:
                    return "<b>Hub Shop</b>\n" +
                           $"Max wagons: {ManifestDeliveryMod.MaxWagonsHub.Value}\n" +
                           $"Work radius: {ManifestDeliveryMod.HubWorkRadius.Value:F0}u\n" +
                           "Capacity: +20%  Speed: -10%\n" +
                           "<i>Global logistics — drops IgnoreGloballyAssignedRequests " +
                           "so wagons accept any bulk request in the settlement.</i>";
                default:
                    return mode.ToString();
            }
        }

        private static void ShowTooltip(Transform nearTransform, string text, TMP_FontAsset font)
        {
            // Find root Canvas so the tooltip draws on top of everything
            var canvas = nearTransform.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var rootCanvas = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;

            if (_tooltipObj == null)
            {
                _tooltipObj = new GameObject("MD_ModeTooltip");
                _tooltipObj.transform.SetParent(rootCanvas.transform, false);

                var bgRT = _tooltipObj.AddComponent<RectTransform>();
                bgRT.pivot = new Vector2(0.5f, 0f);           // anchored bottom-center
                bgRT.anchorMin = new Vector2(0f, 0f);
                bgRT.anchorMax = new Vector2(0f, 0f);
                bgRT.sizeDelta = new Vector2(300f, 90f);

                var bg = _tooltipObj.AddComponent<Image>();
                bg.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);
                bg.raycastTarget = false;

                // Gold border via outline
                var outline = _tooltipObj.AddComponent<UnityEngine.UI.Outline>();
                outline.effectColor = GoldMuted;
                outline.effectDistance = new Vector2(2f, -2f);

                var textObj = new GameObject("Text");
                textObj.transform.SetParent(_tooltipObj.transform, false);
                var textRT = textObj.AddComponent<RectTransform>();
                textRT.anchorMin = Vector2.zero;
                textRT.anchorMax = Vector2.one;
                textRT.offsetMin = new Vector2(10f, 8f);
                textRT.offsetMax = new Vector2(-10f, -8f);

                _tooltipText = textObj.AddComponent<TextMeshProUGUI>();
                if (font != null) _tooltipText.font = font;
                _tooltipText.fontSize = 13f;
                _tooltipText.alignment = TextAlignmentOptions.TopLeft;
                _tooltipText.color = new Color(0.95f, 0.90f, 0.75f, 1f);
                _tooltipText.enableWordWrapping = true;
                _tooltipText.raycastTarget = false;
            }
            else
            {
                // Re-parent to the current canvas in case it changed
                _tooltipObj.transform.SetParent(rootCanvas.transform, false);
            }

            _tooltipObj.SetActive(true);
            _tooltipText.text = text;

            // Position above the hovered button — convert its world corners to canvas space
            var buttonRT = nearTransform as RectTransform;
            var tipRT = _tooltipObj.GetComponent<RectTransform>();
            if (buttonRT != null && tipRT != null)
            {
                Vector3[] corners = new Vector3[4];
                buttonRT.GetWorldCorners(corners);  // 0=BL, 1=TL, 2=TR, 3=BR
                Vector3 topMid = (corners[1] + corners[2]) * 0.5f;

                // Convert world → local for the canvas
                var canvasRT = rootCanvas.transform as RectTransform;
                Vector2 localPoint;
                UnityEngine.RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRT,
                    UnityEngine.RectTransformUtility.WorldToScreenPoint(
                        rootCanvas.worldCamera, topMid),
                    rootCanvas.worldCamera,
                    out localPoint);

                tipRT.anchoredPosition = localPoint + new Vector2(0f, 6f);
            }

            // Preferred size based on text
            _tooltipText.ForceMeshUpdate();
            var textSize = _tooltipText.GetRenderedValues(false);
            if (tipRT != null)
                tipRT.sizeDelta = new Vector2(
                    Mathf.Max(200f, textSize.x + 24f),
                    textSize.y + 20f);
        }

        private static void HideTooltip()
        {
            if (_tooltipObj != null) _tooltipObj.SetActive(false);
        }

        private static void RefreshButtonColors(
            Transform buttonRow, WagonShopEnhancement enhancement)
        {
            foreach (Transform child in buttonRow)
            {
                string name = child.name;
                bool isActive = false;
                if (name.Contains("Standard")) isActive = enhancement.Mode == ShopMode.Standard;
                else if (name.Contains("Camp")) isActive = enhancement.Mode == ShopMode.Camp;
                else if (name.Contains("Hub")) isActive = enhancement.Mode == ShopMode.Hub;

                // Border (outer Image on root)
                var borderImg = child.GetComponent<Image>();
                if (borderImg != null)
                    borderImg.color = isActive ? GoldBright : GoldMuted;

                // Inner bg Image (child named "Inner")
                var innerT = child.Find("Inner");
                if (innerT != null)
                {
                    var innerImg = innerT.GetComponent<Image>();
                    if (innerImg != null)
                        innerImg.color = isActive ? BgActive : BgInactive;
                }

                var tmp = child.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                    tmp.color = isActive ? GoldBright : GoldMuted;
            }
        }
    }
}
