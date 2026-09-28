using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ManifestDelivery.Components
{
    /// <summary>
    /// Per-storage hauling priority (Storage Priorities fold — see
    /// <c>_handoffs/2026-08-21_storage-priorities-fold.md</c>).
    ///
    /// SCALE: 1–9, **9 highest**, default 5 = vanilla. This is the fleet's
    /// priority convention (Tended Wilds' forager priorities use the same
    /// 1–9 / default-5 scale), chosen by the user for consistency across mods.
    ///
    /// TWO LEVELS: a storage-wide priority (<see cref="AllItemsKey"/>) plus
    /// optional per-item priorities. An item with its own priority uses it;
    /// every other item follows the storage-wide one. Setting one item never
    /// changes another.
    ///
    /// WHY A COMPONENT, NOT A LOOKUP TABLE: priorities live on the building
    /// itself, so **relocation is free** — the data physically moves with the
    /// GameObject. Position is used solely as the on-disk key, written from the
    /// building's CURRENT position at save time, so a relocated building simply
    /// persists under its new key. (The handoff suggested keying on "the save
    /// GUID / instance identity that MD's mode persistence already uses" — that
    /// was wrong: <c>WagonShopEnhancement</c> keys on a position hash.)
    /// </summary>
    public class StoragePriorityData : MonoBehaviour
    {
        /// <summary>Item key meaning "every item" — the storage-wide priority.</summary>
        public const int AllItemsKey = -1;

        public const int MinPriority = 1;
        public const int MaxPriority = 9;

        /// <summary>Vanilla behavior; also what an unset storage reports.</summary>
        public const int DefaultPriority = 5;

        /// <summary>itemKey ((int)ItemID, or AllItemsKey) → priority 1–9.</summary>
        private readonly Dictionary<int, int> _priorities = new Dictionary<int, int>();

        /// <summary>Every live instance, so save can sync current positions without a scene scan.</summary>
        internal static readonly HashSet<StoragePriorityData> Live = new HashSet<StoragePriorityData>();

        private StorageBuilding? _storage;
        private bool _restored;

        public StorageBuilding? Storage => _storage;
        public bool HasAnyPriority => _priorities.Count > 0;
        internal IEnumerable<KeyValuePair<int, int>> PrioritiesRO => _priorities;

        /// <summary>
        /// False until this building has read its saved priorities. Saving must
        /// ignore un-restored components — otherwise a save during the attach
        /// window would persist "nothing set" over the player's real settings.
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
            EnsureRestored();
        }

        /// <summary>
        /// Restores saved priorities now if that has not happened yet. The UI
        /// calls this before reading or writing: it can attach the component and
        /// take a click before the delayed restore runs, and that restore would
        /// then overwrite the click. Safe here because a building shown in a
        /// window is already at its real position (the prefab-origin trap only
        /// applies during load).
        /// </summary>
        internal void EnsureRestored()
        {
            if (_restored) return;
            _restored = true;
            Systems.StoragePriorityStore.RestoreInto(this);
        }

        /// <summary>
        /// Effective priority for an item: its own priority if set, otherwise the
        /// storage-wide one, otherwise <see cref="DefaultPriority"/> (vanilla).
        /// </summary>
        public int GetPriority(int itemKey)
        {
            if (_priorities.TryGetValue(itemKey, out int own))
                return own;
            if (itemKey != AllItemsKey && _priorities.TryGetValue(AllItemsKey, out int storageWide))
                return storageWide;
            return DefaultPriority;
        }

        /// <summary>
        /// The priority set on this exact key, or 0 when it has none (an item
        /// then follows the storage-wide priority). The UI uses this to tell an
        /// item's own setting apart from an inherited one.
        /// </summary>
        public int GetOwnPriority(int itemKey)
        {
            return _priorities.TryGetValue(itemKey, out int own) ? own : 0;
        }

        /// <summary>
        /// Sets a priority, clamped to 1–9. Pass 0 to clear it. A storage-wide 5
        /// is stored as "nothing set" because it is vanilla; an item's own 5 is
        /// kept, since it deliberately exempts that item from the storage-wide
        /// priority.
        /// </summary>
        public void SetPriority(int itemKey, int priority)
        {
            if (priority <= 0 || (itemKey == AllItemsKey && priority == DefaultPriority))
                _priorities.Remove(itemKey);
            else
                _priorities[itemKey] = Mathf.Clamp(priority, MinPriority, MaxPriority);
            Systems.StoragePriorityStore.MarkDirty();
        }

        internal void LoadPriorities(Dictionary<int, int> source)
        {
            _priorities.Clear();
            foreach (var kv in source) _priorities[kv.Key] = kv.Value;
        }
    }
}
