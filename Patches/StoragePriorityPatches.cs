using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ManifestDelivery.Components;
using ManifestDelivery.Systems;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// Storage Priorities routing (M0 spike → M1 data → M2 1–9 scale). See
    /// <c>_handoffs/2026-08-21_storage-priorities-fold.md</c>.
    ///
    /// Priority is 1–9, 9 highest, 5 = vanilla (the fleet's convention). The
    /// bias is linear from 5: priority 9 adds the full Strength, 1 subtracts
    /// it, and each step is a quarter of Strength.
    ///
    /// ═══ ANTI-PING-PONG CONTRACT (non-negotiable — keep all three) ══════════
    /// A naive score bias WILL shuffle goods between storages forever.
    ///
    /// RULE 1 — DESTINATION ONLY. GetBaseScore feeds two candidate lists:
    ///   CanStore* = "where can this go" (destinations, decompile L124320) and
    ///   HasItem* = "where can this be taken from" (sources, L124252). We bias
    ///   ONLY CanStore. If a Preferred storage were boosted on both sides it
    ///   would attract goods *and* be the preferred place to drain, so A pulls
    ///   from B and B pulls back from A forever. Destination-only leaves the
    ///   cycle with no return edge: goods flow to Preferred and STOP.
    ///
    /// RULE 2 — TAPER POSITIVE BIAS WITH FULLNESS. Vanilla's base score is
    ///   (1 - fullness) * 100. A flat bonus would override that, overfill the
    ///   target, and trip its max-quota shed (TakeOut above max*1.1) — pushing
    ///   goods out that the bias pulls straight back in. Scaling by free space
    ///   means preference fades to nothing exactly as the storage fills.
    ///   (Negative bias, priority 1–4, is NOT tapered: an empty low-priority
    ///   storage should still be avoided, and it stays usable when all else
    ///   is full.)
    ///
    /// RULE 3 — NEVER BIAS CanStore*OverCapacity, vanilla's already-full
    ///   fallback. Same overfill→shed→refill loop as Rule 2.
    ///
    /// Net: the bias only ever REDIRECTS haul work vanilla already decided to
    /// do, toward a destination with room. It never creates haul work and never
    /// makes a storage attractive to empty. (Relocating already-stored stock is
    /// the M4 rebalancer — deliberately deferred.)
    ///
    /// ── Why Resource, not StorageBuilding ─────────────────────────────────
    /// StorageBuilding does not declare GetBaseScore: StorageBuilding : Building
    /// : Resource, and it lives on Resource (L165149) with no storage override.
    /// So this postfix fires for EVERY Resource — the <c>is StorageBuilding</c>
    /// path is load-bearing. Do NOT switch to GetBaseScoreModifications(): it is
    /// protected virtual and IS overridden by Granary/RootCellar/Treasury, so
    /// patching the base would silently miss exactly those storages.
    ///
    /// ── Perf ──────────────────────────────────────────────────────────────
    /// Hot path. Disabled = one bool check. Enabled = dictionary lookups only;
    /// bucket classification, bucket→item mapping and component resolution are
    /// each computed once and cached. No per-call allocation.
    /// </summary>
    [HarmonyPatch(typeof(Resource), "GetBaseScore", new Type[] { typeof(IQueryContainer) })]
    internal static class StoragePriorityPatches
    {
        /// <summary>Bucket → is it a biasable destination bucket (Rules 1 + 3).</summary>
        private static readonly Dictionary<IQueryContainer, bool> _bucketIsDestination =
            new Dictionary<IQueryContainer, bool>();

        /// <summary>Bucket → (int)ItemID, so tiers can be per item.</summary>
        private static readonly Dictionary<IQueryContainer, int> _bucketItemKey =
            new Dictionary<IQueryContainer, int>();

        /// <summary>Resource instance ID → its priority component (null-cached too).</summary>
        private static readonly Dictionary<int, StoragePriorityData?> _dataByInstance =
            new Dictionary<int, StoragePriorityData?>();

        private static bool _reverseMapBuilt;

        /// <summary>
        /// Set when the postfix throws. Pauses routing until the next map load
        /// WITHOUT touching the saved setting — the old handler wrote
        /// StoragePriorityEnabled=false into the player's config file.
        /// </summary>
        private static bool _disabledThisSession;

        /// <summary>Feature on, and not paused by an error this map load.</summary>
        internal static bool IsActive
        {
            get
            {
                var enabled = ManifestDeliveryMod.StoragePriorityEnabled;
                return enabled != null && enabled.Value && !_disabledThisSession;
            }
        }

        // ── Interop: Storage Priorities by 3am ───────────────────────────────
        //
        // His mod postfixes Resource.GetBaseScore too, and two postfixes add
        // up: a storage prioritized in both mods gets both pulls. MD reads and
        // changes nothing of his — it only tells the player, by assembly name.

        private const string OtherModAssembly = "StoragePriorities";
        private static bool? _otherModLoaded;
        private static bool _warnedStacking;

        /// <summary>
        /// True when the separate Storage Priorities mod (by 3am) is loaded.
        /// First asked on map load or later, when every mod assembly is in.
        /// </summary>
        internal static bool OtherModLoaded
        {
            get
            {
                if (_otherModLoaded.HasValue) return _otherModLoaded.Value;
                bool found = false;
                try
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(assembly.GetName().Name, OtherModAssembly,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                catch { /* treat as not installed */ }
                _otherModLoaded = found;
                return found;
            }
        }

        /// <summary>
        /// Logs once per session when both mods are steering deliveries. Called
        /// when routing first applies a priority and when a storage window shows
        /// MD's row, so it also fires if the feature is switched on mid-session.
        /// </summary>
        internal static void WarnIfStacking()
        {
            if (_warnedStacking || !IsActive || !OtherModLoaded) return;
            _warnedStacking = true;
            ManifestDeliveryMod.Log.Warning(
                "[MD][StoragePri] Storage Priorities by 3am is also installed. Both mods steer " +
                "deliveries, so priorities set in both add up. Set priorities in one mod only.");
        }

        /// <summary>
        /// Storages that take part in priorities. Markets and trading posts are
        /// StorageBuildings too, but they stock goods for their own jobs; the UI
        /// hides the controls for them, so routing must ignore them as well —
        /// otherwise a market rebuilt on a demolished prioritized footprint
        /// would inherit a priority nobody can see or clear.
        /// </summary>
        internal static bool IsEligibleStorage(Resource? resource)
        {
            return resource is StorageBuilding
                   && !(resource is MarketBuilding)
                   && !(resource is TradingPost);
        }

        private static void Postfix(Resource __instance, IQueryContainer container, ref float __result)
        {
            try
            {
                if (!IsActive) return;      // hot-path exit

                // RULE 1 + 3: destinations only, never the over-capacity fallback.
                if (!IsDestinationBucket(container)) return;

                var data = ResolveData(__instance);
                if (data == null || !data.HasAnyPriority) return;

                int priority = data.GetPriority(ResolveItemKey(container));
                if (priority == StoragePriorityData.DefaultPriority) return;

                float strength = ManifestDeliveryMod.StoragePriorityStrength != null
                    ? ManifestDeliveryMod.StoragePriorityStrength.Value : 0f;
                if (strength <= 0f) return;

                // Linear from 5: 9 → +1, 1 → -1, each step ±0.25.
                float weight = (priority - StoragePriorityData.DefaultPriority)
                    / (float)(StoragePriorityData.MaxPriority - StoragePriorityData.DefaultPriority);

                if (weight > 0f)
                {
                    // RULE 2: fade out as it fills.
                    var storage = data.Storage;
                    if (storage == null) return;
                    float freeFraction = GetFreeFraction(storage);
                    if (freeFraction <= 0f) return;
                    __result += strength * weight * freeFraction;
                }
                else
                {
                    __result += strength * weight;   // weight < 0: avoid until others fill
                }
            }
            catch (Exception ex)
            {
                _disabledThisSession = true;
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][StoragePri] Routing error — paused until the next map load: {ex.Message}");
            }
        }

        // ── Bucket classification ────────────────────────────────────────────

        /// <summary>
        /// RULES 1 + 3. True only for normal CanStore* buckets. HasItem*
        /// (sources) and CanStore*OverCapacity (already-full fallback) are false.
        /// Cached per bucket so the enum ToString happens once, never hot.
        /// </summary>
        private static bool IsDestinationBucket(IQueryContainer container)
        {
            if (container == null) return false;
            if (_bucketIsDestination.TryGetValue(container, out bool cached)) return cached;

            bool isDestination = false;
            if (container is WorkBucket bucket)
            {
                string id = bucket.bucketIdentifier.ToString();   // once per bucket, ever
                isDestination = id.StartsWith("CanStore", StringComparison.Ordinal)
                                && id.IndexOf("OverCapacity", StringComparison.Ordinal) < 0;
            }

            _bucketIsDestination[container] = isDestination;
            return isDestination;
        }

        /// <summary>
        /// Maps a work bucket back to the item it is for, so priorities can be
        /// per item. Built once by reflecting the WorkBucketManager's
        /// (protected) canStoreWorkBucketByItem dictionary — reading it directly
        /// avoids the public getter, which logs a warning for items that have no
        /// bucket. Falls back to the building-wide default if unavailable, so
        /// the feature degrades gracefully rather than breaking.
        /// </summary>
        private static int ResolveItemKey(IQueryContainer container)
        {
            if (_bucketItemKey.TryGetValue(container, out int cached)) return cached;
            if (!_reverseMapBuilt) BuildReverseMap();
            return _bucketItemKey.TryGetValue(container, out int key)
                ? key : StoragePriorityData.AllItemsKey;
        }

        private static void BuildReverseMap()
        {
            _reverseMapBuilt = true;
            WarnIfStacking();   // routing is about to apply a priority for the first time
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var wbm = gm != null ? gm.workBucketManager : null;
                if (wbm == null) { _reverseMapBuilt = false; return; }   // retry once ready

                var field = AccessTools.Field(typeof(WorkBucketManager), "canStoreWorkBucketByItem");
                if (field?.GetValue(wbm) is System.Collections.IDictionary map)
                {
                    foreach (System.Collections.DictionaryEntry entry in map)
                    {
                        if (entry.Key is Item item && entry.Value is WorkBucket bucket)
                            _bucketItemKey[bucket] = (int)item.itemID;
                    }
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD][StoragePri] Mapped {_bucketItemKey.Count} storage bucket(s) to items.");
                }
                else
                {
                    ManifestDeliveryMod.Log.Warning(
                        "[MD][StoragePri] Could not read canStoreWorkBucketByItem — " +
                        "per-item priorities unavailable, storage-wide ones still work.");
                }
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Reverse map failed: {ex.Message}");
            }
        }

        // ── Component resolution ─────────────────────────────────────────────

        /// <summary>
        /// Resolves (and lazily creates) a storage's priority component.
        ///
        /// Attaching here rather than on a timer is deliberate: an earlier build
        /// swept the scene with FindObjectsOfType&lt;StorageBuilding&gt;() every
        /// 5 s, which walks every GameObject in the map and caused a visible
        /// periodic hitch — and it kept scanning forever even once every storage
        /// already had its component. This path costs one dictionary hit after
        /// the first sight of a building, and AddComponent runs at most once per
        /// storage. GetBaseScore is main-thread (it builds logistics job data),
        /// so AddComponent here is safe. The UI resolves through here too, so
        /// both paths share one component and one cache.
        /// </summary>
        internal static StoragePriorityData? ResolveData(Resource resource)
        {
            int id = resource.GetInstanceID();
            if (_dataByInstance.TryGetValue(id, out var cached))
                return cached;   // cached negatives return here too — no rework, no write

            StoragePriorityData? data = null;
            if (IsEligibleStorage(resource))          // load-bearing guard
            {
                data = resource.GetComponent<StoragePriorityData>()
                       ?? resource.gameObject.AddComponent<StoragePriorityData>();
            }

            _dataByInstance[id] = data;
            return data;
        }

        /// <summary>RULE 2 helper. 1 = empty, 0 = full.</summary>
        private static float GetFreeFraction(StorageBuilding storage)
        {
            try
            {
                uint available = storage.GetAvailableStorageSpace(out uint currentCount);
                uint capacity = available + currentCount;
                if (capacity == 0) return 0f;
                return (float)available / capacity;
            }
            catch
            {
                return 0f;   // unknown capacity → no bias, the safe direction
            }
        }

        /// <summary>Drops caches on scene unload — instance IDs and buckets don't survive a reload.</summary>
        internal static void ClearCaches()
        {
            _bucketIsDestination.Clear();
            _bucketItemKey.Clear();
            _dataByInstance.Clear();
            _reverseMapBuilt = false;
            _disabledThisSession = false;
        }
    }
}
