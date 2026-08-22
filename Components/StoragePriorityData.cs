using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ManifestDelivery.Components
{
    /// <summary>
    /// Storage priority ladder. Deliberately short and named rather than a 1–5
    /// numeric scale: fewer decisions for the player, and it matches the
    /// Standard/Camp/Hub vocabulary MD already uses in building windows.
    /// </summary>
    public enum StorageTier
    {
        /// <summary>No opinion — behaves exactly like vanilla.</summary>
        Unset = 0,
        /// <summary>Pull deliveries here even from further away.</summary>
        Preferred = 1,
        /// <summary>Explicitly vanilla (distinct from Unset only for the UI).</summary>
        Normal = 2,
        /// <summary>Avoid until better options are full.</summary>
        LastResort = 3,
    }

    /// <summary>
    /// Per-storage priority state (M1 of the Storage Priorities fold — see
    /// <c>_handoffs/2026-08-21_storage-priorities-fold.md</c>).
    ///
    /// WHY A COMPONENT, NOT A LOOKUP TABLE: tiers live on the building itself,
    /// so **relocation is free** — the data physically moves with the
    /// GameObject. The handoff warned that position keys "silently lose settings
    /// when a building is relocated"; that trap only bites designs where the
    /// live data is *keyed* by position. Here position is used solely as the
    /// on-disk key, written from the building's CURRENT position at save time,
    /// so a relocated building simply persists under its new key.
    ///
    /// (The handoff also suggested keying on "the save GUID / instance identity
    /// that MD's mode persistence already uses" — that parenthetical is wrong:
    /// <c>WagonShopEnhancement</c> keys on a position hash. There is no existing
    /// GUID pattern to copy, hence this approach.)
    ///
    /// Tiers are stored per item so "grain goes to the granary near the bakery"
    /// works, with <see cref="AllItemsKey"/> as the building-wide default.
    /// </summary>
    public class StoragePriorityData : MonoBehaviour
    {
        /// <summary>Item key meaning "every item" — the building-wide default.</summary>
        public const int AllItemsKey = -1;

        /// <summary>itemKey (int)ItemID, or AllItemsKey → tier.</summary>
        private readonly Dictionary<int, StorageTier> _tiers = new Dictionary<int, StorageTier>();

        /// <summary>Every live instance, so save can sync current positions without a scene scan.</summary>
        internal static readonly HashSet<StoragePriorityData> Live = new HashSet<StoragePriorityData>();

        private StorageBuilding? _storage;
        private bool _restored;

        public StorageBuilding? Storage => _storage;
        public bool HasAnyTier => _tiers.Count > 0;
        internal IEnumerable<KeyValuePair<int, StorageTier>> TiersRO => _tiers;

        /// <summary>
        /// False until this building has read its saved tiers. Saving must ignore
        /// un-restored components — otherwise a save during the attach window
        /// would persist "no tiers" over the player's real settings.
        /// </summary>
        internal bool Restored => _restored;

        /// <summary>
        /// Position key this building was last loaded/saved under. Lets the store
        /// drop the stale entry when a building is relocated.
        /// </summary>
        internal int PersistedKey { get; set; } = int.MinValue;

        private void Awake()
        {
            _storage = GetComponent<StorageBuilding>();
            Live.Add(this);
        }

        private void OnDestroy()
        {
            Live.Remove(this);
        }

        private void Start()
        {
            StartCoroutine(RestoreDelayed());
        }

        /// <summary>
        /// Restore one frame late. At Awake a save-loaded building is still at
        /// its prefab origin — MD hit this exact trap with wagon shops (see
        /// <c>WagonShopModePreloadPatches</c>), and a position-keyed lookup run
        /// too early silently reads the wrong key.
        /// </summary>
        private IEnumerator RestoreDelayed()
        {
            yield return null;
            if (_restored) yield break;
            _restored = true;
            Systems.StoragePriorityStore.RestoreInto(this);
        }

        /// <summary>
        /// Effective tier for an item: the per-item tier if set, otherwise the
        /// building-wide default, otherwise Unset (vanilla behaviour).
        /// </summary>
        public StorageTier GetTier(int itemKey)
        {
            if (_tiers.TryGetValue(itemKey, out var tier) && tier != StorageTier.Unset)
                return tier;
            if (itemKey != AllItemsKey && _tiers.TryGetValue(AllItemsKey, out var fallback))
                return fallback;
            return StorageTier.Unset;
        }

        public void SetTier(int itemKey, StorageTier tier)
        {
            if (tier == StorageTier.Unset) _tiers.Remove(itemKey);
            else                           _tiers[itemKey] = tier;
            Systems.StoragePriorityStore.MarkDirty();
        }

        /// <summary>Cycles the building-wide default. Used by the M1 test hotkey until M2 ships real UI.</summary>
        public StorageTier CycleDefaultTier()
        {
            StorageTier next = GetTier(AllItemsKey) switch
            {
                StorageTier.Unset      => StorageTier.Preferred,
                StorageTier.Preferred  => StorageTier.Normal,
                StorageTier.Normal     => StorageTier.LastResort,
                _                      => StorageTier.Unset,
            };
            SetTier(AllItemsKey, next);
            return next;
        }

        internal void LoadTiers(Dictionary<int, StorageTier> source)
        {
            _tiers.Clear();
            foreach (var kv in source) _tiers[kv.Key] = kv.Value;
        }
    }
}
