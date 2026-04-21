using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Logs every wagon drop-off so we can see what actually got delivered
    /// (not just what got claimed). Pairs with the CLAIM lines emitted by
    /// CampHaul / ReturnTrip — a matching CLAIM + DELIVER pair means the
    /// wagon successfully completed the haul we assigned it.
    ///
    /// Hooks: TransportWagon.ItemBundleDroppedOff(originStorage, dropOffStorage, bundle)
    ///   Fires whenever the wagon unloads a bundle at a destination.
    ///   Before it reports to the game's TallyItemMoved, we log item + count
    ///   + mode context for diagnostics.
    /// </summary>
    [HarmonyPatch(typeof(TransportWagon), nameof(TransportWagon.ItemBundleDroppedOff))]
    internal static class TransportWagonDropOffPatch
    {
        // Simple dedupe — the game calls ItemBundleDroppedOff twice per drop-off
        // (once from the wagon side, once from the destination side). Suppress
        // the second call when we see the same bundle reference within 200ms.
        private static readonly System.Collections.Generic.Dictionary<int, float>
            _lastLogTime = new System.Collections.Generic.Dictionary<int, float>();

        private static void Postfix(
            TransportWagon __instance,
            ItemStorage originStorage,
            IContainsItems dropOffStorage,
            ItemBundle bundle)
        {
            try
            {
                if (bundle == null) return;

                // Dedupe by bundle reference hash
                int bundleKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(bundle);
                float now = Time.time;
                if (_lastLogTime.TryGetValue(bundleKey, out float last)
                    && now - last < 0.2f)
                    return;
                _lastLogTime[bundleKey] = now;

                string mode = "Standard";
                var data = __instance.GetComponent<WagonEnhancementData>();
                var shop = data?.ResolveShopEnhancement(__instance);
                if (shop != null)
                    mode = shop.Mode.ToString();

                string dest = DescribeContainer(dropOffStorage);
                string origin = DescribeStorage(originStorage);

                ManifestDeliveryMod.Log.Msg(
                    $"[MD] DELIVER ({mode}): {__instance.name} " +
                    $"{bundle.name}×{bundle.numberOfItems} " +
                    $"{origin} → {dest}");
            }
            catch (System.Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD] DeliveryLog postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// ItemStorage is a plain class — resolve its GameObject via the
        /// ReservableItemStorage MonoBehaviour that owns it.
        /// </summary>
        private static string DescribeStorage(ItemStorage storage)
        {
            // originStorage is usually the wagon's own inventory — either null
            // or an ItemStorage with no reservableItemStorageOwner link.
            if (storage == null) return "(wagon)";
            var owner = storage.reservableItemStorageOwner;
            if (owner != null) return owner.gameObject.name;
            return "(wagon)";
        }

        /// <summary>
        /// IContainsItems may be a MonoBehaviour (building storage) or a plain
        /// ItemStorage. Cast both ways to get a readable name.
        /// </summary>
        private static string DescribeContainer(IContainsItems container)
        {
            if (container == null) return "?";
            if (container is MonoBehaviour mb && mb != null) return mb.gameObject.name;
            if (container is ItemStorage store) return DescribeStorage(store);
            return container.GetType().Name;
        }
    }
}
