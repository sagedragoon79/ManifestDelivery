using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Storage Priorities M2 — the in-window UI (see
    /// <c>_handoffs/2026-08-21_storage-priorities-fold.md</c>). Priority is the
    /// fleet's 1–9 scale, 9 highest, 5 = vanilla. Three pieces:
    ///
    /// 1. **Storage-wide row** at the top of a storage building's storage
    ///    section: <c>Storage Priority [▼] 5 [▲]</c>.
    /// 2. **Per-item row** inside vanilla's Storage Limits popup (opened by
    ///    clicking an item icon), under the min/max quota rows:
    ///    <c>Priority [▼] 7 [▲] [Default]</c>. Default is lit while the item
    ///    follows the storage-wide priority; clicking it clears the item's own.
    /// 3. **Marker** on each item icon: ▲ above 5, ▼ below 5, in the icon's free
    ///    bottom-left corner. Full strength = the item's own priority; faded =
    ///    inherited. The exact number is in the icon's tooltip. (Deliberately
    ///    NOT a number badge on the icon — that is 3am's Storage Priorities
    ///    design, which the clean-room rule keeps us from reproducing.)
    ///
    /// The stepper and its gold-on-dark palette come from Tended Wilds' forager
    /// priority arrows, so priority looks and reads the same across the fleet.
    /// Every hook is a manual patch in its own try/catch: a game update that
    /// renames one UI method disables only that piece, never MD's core patches.
    ///
    /// Anti-ping-pong: nothing here touches routing. The UI only reads and
    /// writes priorities; the contract lives in <see cref="StoragePriorityPatches"/>.
    /// </summary>
    internal static class StoragePriorityUIPatches
    {
        private const string BuildingRowName = "MD_StoragePriorityRow";
        private const string ItemRowName     = "MD_ItemPriorityRow";
        private const string MarkerName      = "MD_PriorityMarker";

        private const char UpChar   = '▲';   // ▲
        private const char DownChar = '▼';   // ▼

        // ── Palette ──────────────────────────────────────────────────────
        // Stepper colors are Tended Wilds' priority arrows; the Default button
        // matches MD's Standard/Camp/Hub buttons.
        private static readonly Color ArrowBg      = new Color(0.18f, 0.18f, 0.20f, 0.85f);
        private static readonly Color ArrowBgHover = new Color(0.30f, 0.30f, 0.33f, 0.95f);
        private static readonly Color ArrowGlyph   = new Color(0.85f, 0.70f, 0.30f, 1f);
        private static readonly Color ValueBg      = new Color(0.08f, 0.08f, 0.10f, 0.90f);
        private static readonly Color LabelCream   = new Color(0.96f, 0.89f, 0.76f, 1f);
        private static readonly Color HighColor    = new Color(0.95f, 0.82f, 0.35f, 1f);
        private static readonly Color LowColor     = new Color(0.92f, 0.46f, 0.38f, 1f);
        private static readonly Color NeutralColor = new Color(0.92f, 0.90f, 0.85f, 1f);
        private static readonly Color GoldBright   = new Color(0.95f, 0.82f, 0.35f, 1f);
        private static readonly Color GoldMuted    = new Color(0.55f, 0.45f, 0.22f, 1f);
        private static readonly Color BgActive     = new Color(0.18f, 0.26f, 0.12f, 0.95f);
        private static readonly Color BgInactive   = new Color(0.10f, 0.09f, 0.07f, 0.90f);
        private static readonly Color MarkerBg     = new Color(0.08f, 0.08f, 0.10f, 0.85f);

        // ── State ────────────────────────────────────────────────────────
        private static FieldInfo? _moduleParentField;
        private static bool _moduleParentFieldResolved;

        /// <summary>
        /// The icon whose storage-aware Initialize is running. That overload
        /// calls the base Initialize(Item) internally, which must not unbind
        /// the icon it is in the middle of binding.
        /// </summary>
        private static UIBuildingInfoWindowStorageModuleItem? _initializingCell;

        /// <summary>The per-item row in the limits popup, so a storage-wide change can refresh it.</summary>
        private static PriorityRowView? _openItemRow;

        private static bool _glyphsResolved;
        private static TMP_FontAsset? _glyphFont;
        private static string _upText = UpChar.ToString();
        private static string _downText = DownChar.ToString();

        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();

        // ── Registration ─────────────────────────────────────────────────

        public static void Register(HarmonyLib.Harmony harmony)
        {
            TryPatch(harmony, "storage section row",
                AccessTools.Method(typeof(UIBuildingInfoWindowStorageModuleCollapsible), "Init",
                    new[] { typeof(Building), typeof(UIWindow) }),
                postfix: nameof(CollapsibleInitPostfix));

            TryPatch(harmony, "item limits row",
                AccessTools.Method(typeof(UIResourceLimitSubWindow), "Initialize",
                    new[] { typeof(StorageBuilding), typeof(Item), typeof(UIBuildingInfoWindowStorageModuleItem) }),
                postfix: nameof(LimitSubWindowInitPostfix));

            TryPatch(harmony, "icon marker bind",
                AccessTools.Method(typeof(UIBuildingInfoWindowStorageModuleItem), "Initialize",
                    new[] { typeof(Building), typeof(ReadOnlyCollection<Item>), typeof(Item) }),
                prefix: nameof(CellInitPrefix), postfix: nameof(CellInitPostfix));

            TryPatch(harmony, "icon marker unbind",
                AccessTools.Method(typeof(UIStorageItem), "Initialize", new[] { typeof(Item) }),
                prefix: nameof(StorageItemInitPrefix));

            TryPatch(harmony, "icon marker pool reset",
                AccessTools.Method(typeof(UIBuildingInfoWindowStorageModuleItem), "SetToDefault"),
                postfix: nameof(CellSetToDefaultPostfix));

            TryPatch(harmony, "icon tooltip",
                AccessTools.DeclaredMethod(typeof(UIBuildingInfoWindowStorageModuleItem), "SetToolTipString",
                    new[] { typeof(string) }),
                postfix: nameof(CellTooltipPostfix));
        }

        private static void TryPatch(HarmonyLib.Harmony harmony, string label, MethodBase? target,
            string? prefix = null, string? postfix = null)
        {
            if (target == null)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][StoragePri] UI hook not found ({label}) — that part of the UI is disabled.");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix:  prefix  != null ? new HarmonyMethod(AccessTools.Method(typeof(StoragePriorityUIPatches), prefix))  : null,
                    postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(StoragePriorityUIPatches), postfix)) : null);
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][StoragePri] UI hook failed ({label}): {ex.Message}");
            }
        }

        /// <summary>Drops per-map references on scene load.</summary>
        internal static void ClearCaches()
        {
            _openItemRow = null;
            _initializingCell = null;
        }

        // ── Shared helpers ───────────────────────────────────────────────

        private static bool IsFeatureOn() => StoragePriorityPatches.IsActive;

        /// <summary>
        /// Real storages only. Markets and trading posts are StorageBuildings
        /// too, but they stock goods for their own jobs rather than serving as
        /// general hauling destinations.
        /// </summary>
        internal static bool IsEligible(Building? building) =>
            StoragePriorityPatches.IsEligibleStorage(building);

        private static StoragePriorityData? DataFor(Building? building)
        {
            if (!IsEligible(building)) return null;
            var data = StoragePriorityPatches.ResolveData(building!);
            data?.EnsureRestored();
            return data;
        }

        private static void LogOnce(string key, string message)
        {
            if (_loggedOnce.Add(key)) ManifestDeliveryMod.Log.Warning(message);
        }

        private static Color ValueColor(int priority)
        {
            if (priority > StoragePriorityData.DefaultPriority) return HighColor;
            if (priority < StoragePriorityData.DefaultPriority) return LowColor;
            return NeutralColor;
        }

        private static TMP_FontAsset? FindFont(Component root)
        {
            var text = root.GetComponentInChildren<TextMeshProUGUI>(true);
            return text != null ? text.font : null;
        }

        /// <summary>
        /// Checks once that a font can draw ▲/▼. Tended Wilds proves the game
        /// font can, but a font change should degrade to +/- rather than show
        /// empty boxes.
        /// </summary>
        private static void ResolveGlyphs(TMP_FontAsset? preferred)
        {
            if (_glyphsResolved) return;
            _glyphsResolved = true;
            try
            {
                if (preferred != null && preferred.HasCharacter(UpChar, true, true)
                                      && preferred.HasCharacter(DownChar, true, true))
                {
                    _glyphFont = preferred;
                    ManifestDeliveryMod.Log.Msg($"[MD][StoragePri] Arrow glyphs use font '{preferred.name}'.");
                    return;
                }
                foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                {
                    if (font != null && font.HasCharacter(UpChar, true, true)
                                     && font.HasCharacter(DownChar, true, true))
                    {
                        _glyphFont = font;
                        ManifestDeliveryMod.Log.Msg($"[MD][StoragePri] Arrow glyphs use font '{font.name}' (window font lacks them).");
                        return;
                    }
                }
            }
            catch { }
            _upText = "+";
            _downText = "-";
            ManifestDeliveryMod.Log.Warning(
                "[MD][StoragePri] No loaded font has ▲/▼ — priority arrows use +/- instead.");
        }

        // ── 1. Storage-wide row ──────────────────────────────────────────

        private static void CollapsibleInitPostfix(
            UIBuildingInfoWindowStorageModuleCollapsible __instance, Building _building)
        {
            try
            {
                var parent = GetModuleParent(__instance);
                if (parent == null) return;

                var existing = parent.Find(BuildingRowName);
                var data = IsFeatureOn() ? DataFor(_building) : null;
                if (data == null)
                {
                    if (existing != null) existing.gameObject.SetActive(false);
                    return;
                }

                PriorityRowView view;
                if (existing != null && existing.GetComponent<PriorityRowView>() is PriorityRowView found)
                {
                    view = found;
                }
                else
                {
                    if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
                    view = CreateRow(parent, BuildingRowName, "Storage Priority",
                        labelWidth: 120f, leftPad: 8, isItemRow: false, font: FindFont(__instance));
                }

                view.Data = data;
                view.ItemKey = StoragePriorityData.AllItemsKey;
                view.CellsRoot = parent;
                view.transform.SetAsFirstSibling();
                view.gameObject.SetActive(true);
                Refresh(view);
            }
            catch (Exception ex)
            {
                LogOnce("row", $"[MD][StoragePri] Storage row error: {ex.Message}");
            }
        }

        private static Transform? GetModuleParent(UIBuildingInfoWindowStorageModuleCollapsible collapsible)
        {
            if (!_moduleParentFieldResolved)
            {
                _moduleParentFieldResolved = true;
                _moduleParentField = AccessTools.Field(
                    typeof(UIBuildingInfoWindowStorageModuleCollapsible), "storageModuleParent");
                if (_moduleParentField == null)
                    LogOnce("parent", "[MD][StoragePri] storageModuleParent not found — storage row disabled.");
            }
            var group = _moduleParentField?.GetValue(collapsible) as Component;
            return group != null ? group.transform : null;
        }

        // ── 2. Per-item row in the Storage Limits popup ──────────────────

        private static void LimitSubWindowInitPostfix(
            UIResourceLimitSubWindow __instance, StorageBuilding _storageBuilding, Item _item,
            UIBuildingInfoWindowStorageModuleItem _storageUIIcon)
        {
            try
            {
                var existing = __instance.transform.Find(ItemRowName);
                var data = IsFeatureOn() && _item != null ? DataFor(_storageBuilding) : null;
                if (data == null)
                {
                    if (existing != null) existing.gameObject.SetActive(false);
                    _openItemRow = null;
                    return;
                }

                PriorityRowView view;
                if (existing != null && existing.GetComponent<PriorityRowView>() is PriorityRowView found)
                {
                    view = found;
                }
                else
                {
                    if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
                    // leftPad 60 lines the label up with vanilla's "Minimum Quota" text.
                    view = CreateRow(__instance.transform, ItemRowName, "Priority",
                        labelWidth: 96f, leftPad: 60, isItemRow: true, font: FindFont(__instance));
                }

                view.Data = data;
                view.ItemKey = (int)_item!.itemID;
                view.Cell = _storageUIIcon;
                view.ItemAllowed = _storageBuilding.DoesUserAllowItem(_item);
                view.transform.SetAsLastSibling();
                view.gameObject.SetActive(true);
                _openItemRow = view;
                Refresh(view);
            }
            catch (Exception ex)
            {
                LogOnce("itemrow", $"[MD][StoragePri] Item row error: {ex.Message}");
            }
        }

        // ── Row construction ─────────────────────────────────────────────

        private static PriorityRowView CreateRow(Transform parent, string name, string label,
            float labelWidth, int leftPad, bool isItemRow, TMP_FontAsset? font)
        {
            ResolveGlyphs(font);

            var row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(parent, false);

            // Transparent hit area so hovering anywhere on the row shows its tooltip.
            var hitArea = row.AddComponent<Image>();
            hitArea.color = new Color(0f, 0f, 0f, 0f);
            hitArea.raycastTarget = true;

            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            le.flexibleWidth = 1f;

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.spacing = 4f;
            hlg.padding = new RectOffset(leftPad, 6, 3, 3);
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            var view = row.AddComponent<PriorityRowView>();
            view.IsItemRow = isItemRow;
            view.Font = font;
            view.Group = row.AddComponent<CanvasGroup>();

            var rowPointer = row.AddComponent<MdPointer>();
            rowPointer.Enter = () => { view.Hovered = true; ShowRowTooltip(view); };
            rowPointer.Exit = () => { view.Hovered = false; ModeButtonPatches.HideTooltip(); };

            // Label — styled like vanilla's "Minimum Quota" text.
            var labelText = NewText(row.transform, "Label", label, font, 14f, LabelCream,
                TextAlignmentOptions.MidlineLeft);
            labelText.fontStyle = FontStyles.SmallCaps;
            var labelLe = labelText.gameObject.AddComponent<LayoutElement>();
            labelLe.minWidth = labelWidth;
            labelLe.preferredWidth = labelWidth;
            labelLe.preferredHeight = 24f;

            // [▼] value [▲] — Tended Wilds' stepper.
            var glyphFont = _glyphFont ?? font;
            view.DownGlyph = CreateArrowCell(row.transform, "Down", _downText, glyphFont, () => Step(view, -1));

            var valueObj = new GameObject("Value", typeof(RectTransform));
            valueObj.transform.SetParent(row.transform, false);
            var valueBg = valueObj.AddComponent<Image>();
            valueBg.color = ValueBg;
            valueBg.raycastTarget = true;
            var valueLe = valueObj.AddComponent<LayoutElement>();
            valueLe.minWidth = 34f;
            valueLe.preferredWidth = 34f;
            valueLe.preferredHeight = 22f;
            view.ValueText = NewText(valueObj.transform, "Text", "5", font, 14f, NeutralColor,
                TextAlignmentOptions.Center);
            view.ValueText.fontStyle = FontStyles.Bold;
            Stretch(view.ValueText.rectTransform);

            view.UpGlyph = CreateArrowCell(row.transform, "Up", _upText, glyphFont, () => Step(view, +1));

            if (isItemRow)
            {
                var spacer = new GameObject("Spacer", typeof(RectTransform));
                spacer.transform.SetParent(row.transform, false);
                spacer.AddComponent<LayoutElement>().minWidth = 8f;
                CreateDefaultButton(view);
            }

            ManifestDeliveryMod.Log.Msg(
                $"[MD][StoragePri] {(isItemRow ? "Item" : "Storage")} priority row added under '{parent.name}'.");
            return view;
        }

        private static TextMeshProUGUI CreateArrowCell(Transform parent, string name, string glyph,
            TMP_FontAsset? font, Action onClick)
        {
            var cell = new GameObject(name, typeof(RectTransform));
            cell.transform.SetParent(parent, false);
            var bg = cell.AddComponent<Image>();
            bg.color = ArrowBg;
            bg.raycastTarget = true;
            var le = cell.AddComponent<LayoutElement>();
            le.minWidth = 22f;
            le.preferredWidth = 22f;
            le.preferredHeight = 22f;

            var text = NewText(cell.transform, "Glyph", glyph, font, 12f, ArrowGlyph,
                TextAlignmentOptions.Center);
            Stretch(text.rectTransform);

            var pointer = cell.AddComponent<MdPointer>();
            pointer.Click = onClick;
            pointer.Enter = () => bg.color = ArrowBgHover;
            pointer.Exit = () => bg.color = ArrowBg;
            return text;
        }

        private static void CreateDefaultButton(PriorityRowView view)
        {
            // Outer = gold border, inner = background, sized to its label —
            // the same chrome as the Standard/Camp/Hub buttons.
            var btnObj = new GameObject("Default", typeof(RectTransform));
            btnObj.transform.SetParent(view.transform, false);
            var border = btnObj.AddComponent<Image>();
            border.raycastTarget = true;
            var btnLe = btnObj.AddComponent<LayoutElement>();
            btnLe.preferredHeight = 22f;
            var btnHlg = btnObj.AddComponent<HorizontalLayoutGroup>();
            btnHlg.padding = new RectOffset(2, 2, 2, 2);
            btnHlg.childControlWidth = true;
            btnHlg.childControlHeight = true;
            btnHlg.childForceExpandWidth = true;
            btnHlg.childForceExpandHeight = true;

            var innerObj = new GameObject("Inner", typeof(RectTransform));
            innerObj.transform.SetParent(btnObj.transform, false);
            var inner = innerObj.AddComponent<Image>();
            inner.raycastTarget = false;
            var innerHlg = innerObj.AddComponent<HorizontalLayoutGroup>();
            innerHlg.padding = new RectOffset(7, 7, 0, 0);
            innerHlg.childControlWidth = true;
            innerHlg.childControlHeight = true;
            innerHlg.childForceExpandWidth = true;
            innerHlg.childForceExpandHeight = true;

            var label = NewText(innerObj.transform, "Label", "Default", view.Font, 12f, GoldMuted,
                TextAlignmentOptions.Center);
            label.fontStyle = FontStyles.Bold;

            view.DefaultBorder = border;
            view.DefaultInner = inner;
            view.DefaultLabel = label;

            var pointer = btnObj.AddComponent<MdPointer>();
            pointer.Click = () => UseDefault(view);
        }

        private static TextMeshProUGUI NewText(Transform parent, string name, string text,
            TMP_FontAsset? font, float size, Color color, TextAlignmentOptions alignment)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // ── Row behavior ─────────────────────────────────────────────────

        private static void Step(PriorityRowView view, int delta)
        {
            var data = view.Data;
            if (data == null) return;
            try
            {
                data.EnsureRestored();
                int current = data.GetPriority(view.ItemKey);
                int next = Mathf.Clamp(current + delta,
                    StoragePriorityData.MinPriority, StoragePriorityData.MaxPriority);
                if (next == current) return;   // at 1 or 9: no-op, never pins an inherited value

                data.SetPriority(view.ItemKey, next);
                LogChange(view, data, next.ToString());
                AfterChange(view, data);
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Set priority failed: {ex.Message}");
            }
        }

        private static void UseDefault(PriorityRowView view)
        {
            var data = view.Data;
            if (data == null || !view.IsItemRow) return;
            try
            {
                data.EnsureRestored();
                if (data.GetOwnPriority(view.ItemKey) == 0) return;   // already following the storage
                data.SetPriority(view.ItemKey, 0);
                LogChange(view, data, $"Default ({data.GetPriority(view.ItemKey)})");
                AfterChange(view, data);
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Reset priority failed: {ex.Message}");
            }
        }

        private static void LogChange(PriorityRowView view, StoragePriorityData data, string value)
        {
            string target = view.IsItemRow ? ((ItemID)view.ItemKey).ToString() : "all items";
            ManifestDeliveryMod.Log.Msg($"[MD][StoragePri] '{data.gameObject.name}' {target} → {value}");
        }

        private static void AfterChange(PriorityRowView view, StoragePriorityData data)
        {
            Refresh(view);
            if (view.Hovered) ShowRowTooltip(view);

            if (view.IsItemRow)
            {
                var tag = view.Cell != null ? view.Cell.GetComponent<PriorityCellTag>() : null;
                if (tag != null) RefreshMarker(tag);
                return;
            }

            // Storage-wide change: every inheriting icon and an open item row may change.
            if (view.CellsRoot != null)
                foreach (var tag in view.CellsRoot.GetComponentsInChildren<PriorityCellTag>())
                    RefreshMarker(tag);
            if (_openItemRow != null && _openItemRow.Data == data && _openItemRow.gameObject.activeInHierarchy)
                Refresh(_openItemRow);
        }

        private static void Refresh(PriorityRowView view)
        {
            var data = view.Data;
            if (data == null || !IsFeatureOn())
            {
                view.gameObject.SetActive(false);
                return;
            }

            int effective = data.GetPriority(view.ItemKey);
            bool own = !view.IsItemRow || data.GetOwnPriority(view.ItemKey) > 0;

            if (view.ValueText != null)
            {
                var c = ValueColor(effective);
                c.a = own ? 1f : 0.6f;
                view.ValueText.text = effective.ToString();
                view.ValueText.color = c;
            }
            SetArrowEnabled(view.DownGlyph, effective > StoragePriorityData.MinPriority);
            SetArrowEnabled(view.UpGlyph, effective < StoragePriorityData.MaxPriority);

            if (view.IsItemRow && view.DefaultBorder != null && view.DefaultInner != null && view.DefaultLabel != null)
            {
                bool following = !own;   // lit while the item follows the storage
                view.DefaultBorder.color = following ? GoldBright : GoldMuted;
                view.DefaultInner.color = following ? BgActive : BgInactive;
                view.DefaultLabel.color = following ? GoldBright : GoldMuted;
            }

            if (view.Group != null)
                view.Group.alpha = view.ItemAllowed ? 1f : 0.55f;
        }

        private static void SetArrowEnabled(TextMeshProUGUI? glyph, bool enabled)
        {
            if (glyph == null) return;
            var c = ArrowGlyph;
            c.a = enabled ? 1f : 0.3f;
            glyph.color = c;
        }

        private static void ShowRowTooltip(PriorityRowView view)
        {
            try { ModeButtonPatches.ShowTooltip(view.transform, RowTooltip(view), view.Font!); }
            catch { }
        }

        private static string RowTooltip(PriorityRowView view)
        {
            var data = view.Data;
            if (data == null) return string.Empty;
            int effective = data.GetPriority(view.ItemKey);
            var sb = new StringBuilder();

            if (view.IsItemRow)
            {
                bool own = data.GetOwnPriority(view.ItemKey) > 0;
                sb.Append($"<b>Priority {effective}</b> ")
                  .Append(own ? "(set for this item)" : "(follows the storage)")
                  .Append("\nHaulers bring this item to higher-priority storages first. ");
            }
            else
            {
                sb.Append($"<b>Storage Priority {effective}</b>")
                  .Append("\nHaulers bring goods to higher-priority storages first. ");
            }

            sb.Append("9 is the strongest pull and 5 is vanilla. At 1 to 4, haulers use this " +
                      "storage only when others are full or much farther away. The pull fades " +
                      "as the storage fills, so goods are never pulled back out.");

            if (view.IsItemRow)
            {
                sb.Append("\n<i>Default makes this item follow the storage's overall priority.</i>");
                if (!view.ItemAllowed)
                    sb.Append("\n<i>This storage doesn't accept this item, so its priority has no " +
                              "effect until you allow it.</i>");
            }
            else
            {
                sb.Append("\n<i>Applies to every item this storage accepts. Click an item's icon " +
                          "to give that item its own priority.</i>");
            }
            return sb.ToString();
        }

        // ── 3. Icon marker + tooltip ─────────────────────────────────────

        private static void CellInitPrefix(
            UIBuildingInfoWindowStorageModuleItem __instance, Building building, Item _item)
        {
            try
            {
                _initializingCell = __instance;
                var tag = __instance.GetComponent<PriorityCellTag>();
                var data = IsFeatureOn() && _item != null ? DataFor(building) : null;
                if (data == null)
                {
                    if (tag != null) tag.Unbind();
                    return;
                }
                if (tag == null) tag = __instance.gameObject.AddComponent<PriorityCellTag>();
                tag.Bind(data, (int)_item!.itemID);
            }
            catch (Exception ex)
            {
                LogOnce("bind", $"[MD][StoragePri] Icon bind error: {ex.Message}");
            }
        }

        private static void CellInitPostfix(UIBuildingInfoWindowStorageModuleItem __instance)
        {
            _initializingCell = null;
            try
            {
                var tag = __instance.GetComponent<PriorityCellTag>();
                if (tag != null) RefreshMarker(tag);
            }
            catch (Exception ex)
            {
                LogOnce("marker", $"[MD][StoragePri] Icon marker error: {ex.Message}");
            }
        }

        /// <summary>
        /// A prefix, not a postfix: the base Initialize(Item) rebuilds the
        /// tooltip, so a stale binding must be gone before it runs. Pooled icons
        /// move between buildings, and an icon reused in a house window must not
        /// keep a storehouse's marker.
        /// </summary>
        private static void StorageItemInitPrefix(UIStorageItem __instance)
        {
            if (!(__instance is UIBuildingInfoWindowStorageModuleItem cell)) return;
            if (ReferenceEquals(cell, _initializingCell)) return;
            try
            {
                var tag = cell.GetComponent<PriorityCellTag>();
                if (tag != null && tag.Bound)
                {
                    tag.Unbind();
                    RefreshMarker(tag);
                }
            }
            catch { }
        }

        private static void CellSetToDefaultPostfix(UIBuildingInfoWindowStorageModuleItem __instance)
        {
            try
            {
                var tag = __instance.GetComponent<PriorityCellTag>();
                if (tag == null) return;
                tag.Unbind();
                RefreshMarker(tag);
            }
            catch { }
        }

        /// <summary>Adds "Hauling priority: 7" under vanilla's tooltip rows.</summary>
        private static void CellTooltipPostfix(UIBuildingInfoWindowStorageModuleItem __instance)
        {
            try
            {
                var tag = __instance.GetComponent<PriorityCellTag>();
                if (tag == null || !tag.Bound || tag.Data == null || !IsFeatureOn()) return;

                int own = tag.Data.GetOwnPriority(tag.ItemKey);
                int effective = tag.Data.GetPriority(tag.ItemKey);
                string line;
                if (own > 0)
                    line = $"Hauling priority: {own}";
                else if (effective != StoragePriorityData.DefaultPriority)
                    line = $"Hauling priority: {effective} (storage-wide)";
                else
                    return;

                var provider = __instance.GetComponent<GenericTooltipDataProvider>();
                if (provider == null) return;
                provider.toolTipRowKeyNames.Add(string.Empty);
                provider.toolTipRowKeyNames.Add(line);
            }
            catch { }
        }

        /// <summary>
        /// Shows the direction of the effective priority on the icon. Cached by
        /// state, so vanilla's once-a-second refresh costs a comparison, not a
        /// text rebuild.
        /// </summary>
        internal static void RefreshMarker(PriorityCellTag tag)
        {
            int state = 0;   // 0 none, 1/2 above 5 own/inherited, 3/4 below 5 own/inherited
            if (tag.Bound && tag.Data != null && IsFeatureOn())
            {
                int effective = tag.Data.GetPriority(tag.ItemKey);
                bool own = tag.Data.GetOwnPriority(tag.ItemKey) > 0;
                if (effective > StoragePriorityData.DefaultPriority)      state = own ? 1 : 2;
                else if (effective < StoragePriorityData.DefaultPriority) state = own ? 3 : 4;
            }

            if (state == tag.MarkerState && (state == 0 || tag.Marker != null)) return;
            tag.MarkerState = state;

            if (state == 0)
            {
                if (tag.Marker != null) tag.Marker.SetActive(false);
                return;
            }

            if (tag.Marker == null) CreateMarker(tag);
            if (tag.Marker == null || tag.MarkerText == null || tag.MarkerGroup == null) return;

            bool up = state <= 2;
            tag.MarkerText.text = up ? _upText : _downText;
            tag.MarkerText.color = up ? HighColor : LowColor;
            tag.MarkerGroup.alpha = (state == 2 || state == 4) ? 0.5f : 1f;
            tag.Marker.SetActive(true);
            tag.Marker.transform.SetAsLastSibling();
        }

        private static void CreateMarker(PriorityCellTag tag)
        {
            // Bottom-left is the icon's only free corner: the allow checkbox sits
            // top-left, the quota marker top-right, the count bottom-right.
            var root = new GameObject(MarkerName, typeof(RectTransform));
            root.transform.SetParent(tag.transform, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(13f, 13f);
            root.AddComponent<LayoutElement>().ignoreLayout = true;

            var bg = root.AddComponent<Image>();
            bg.color = MarkerBg;
            bg.raycastTarget = false;
            var group = root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            if (!_glyphsResolved) ResolveGlyphs(FindFont(tag));
            var text = NewText(root.transform, "Glyph", _upText, _glyphFont ?? FindFont(tag), 10f,
                HighColor, TextAlignmentOptions.Center);
            Stretch(text.rectTransform);

            tag.Marker = root;
            tag.MarkerText = text;
            tag.MarkerGroup = group;
        }
    }

    /// <summary>
    /// Click and hover without EventTrigger. EventTrigger implements every
    /// pointer interface, so it would swallow mouse-wheel events and stop the
    /// building window from scrolling while the cursor is over a row. (Button
    /// is avoided for the EventSystem focus trap noted in the modding guide.)
    /// </summary>
    public sealed class MdPointer : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        internal Action? Click;
        internal Action? Enter;
        internal Action? Exit;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) Click?.Invoke();
        }

        public void OnPointerEnter(PointerEventData eventData) => Enter?.Invoke();
        public void OnPointerExit(PointerEventData eventData) => Exit?.Invoke();
    }

    /// <summary>
    /// A priority row's binding. Window objects are pooled and retargeted, so
    /// handlers read the CURRENT storage from here instead of capturing the one
    /// the row was built for.
    /// </summary>
    public sealed class PriorityRowView : MonoBehaviour
    {
        internal bool IsItemRow;
        internal StoragePriorityData? Data;
        internal int ItemKey = StoragePriorityData.AllItemsKey;
        internal bool ItemAllowed = true;
        internal UIBuildingInfoWindowStorageModuleItem? Cell;
        internal Transform? CellsRoot;
        internal TMP_FontAsset? Font;
        internal CanvasGroup? Group;
        internal TextMeshProUGUI? ValueText;
        internal TextMeshProUGUI? DownGlyph;
        internal TextMeshProUGUI? UpGlyph;
        internal Image? DefaultBorder;
        internal Image? DefaultInner;
        internal TextMeshProUGUI? DefaultLabel;
        internal bool Hovered;
    }

    /// <summary>
    /// Rides on a vanilla item icon: which storage and item it currently shows,
    /// plus its marker. Pooled icons are rebound every time vanilla
    /// reinitializes them (about once a second while the window is open).
    /// </summary>
    public sealed class PriorityCellTag : MonoBehaviour
    {
        internal bool Bound;
        internal StoragePriorityData? Data;
        internal int ItemKey;
        internal int MarkerState;
        internal GameObject? Marker;
        internal TextMeshProUGUI? MarkerText;
        internal CanvasGroup? MarkerGroup;

        internal void Bind(StoragePriorityData data, int itemKey)
        {
            Bound = true;
            Data = data;
            ItemKey = itemKey;
        }

        internal void Unbind()
        {
            Bound = false;
            Data = null;
        }
    }
}
