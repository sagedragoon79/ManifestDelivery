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
    /// Storage Priorities routing (M0 spike → M1 tiers). See
    /// <c>_handoffs/2026-08-21_storage-priorities-fold.md</c>.
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
    ///   (Negative/LastResort bias is NOT tapered: an empty last-resort storage
    ///   should still be avoided, and it stays usable when all else is full.)
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

        private static void Postfix(Resource __instance, IQueryContainer container, ref float __result)
        {
            try
            {
                var enabled = ManifestDeliveryMod.StoragePriorityEnabled;
                if (enabled == null || !enabled.Value) return;      // hot-path exit

                // RULE 1 + 3: destinations only, never the over-capacity fallback.
                if (!IsDestinationBucket(container)) return;

                var data = ResolveData(__instance);
                if (data == null || !data.HasAnyTier) return;

                StorageTier tier = data.GetTier(ResolveItemKey(container));
                if (tier == StorageTier.Unset || tier == StorageTier.Normal) return;

                float strength = ManifestDeliveryMod.StoragePriorityStrength != null
                    ? ManifestDeliveryMod.StoragePriorityStrength.Value : 0f;
                if (strength <= 0f) return;

                if (tier == StorageTier.Preferred)
                {
                    // RULE 2: fade out as it fills.
                    var storage = data.Storage;
                    if (storage == null) return;
                    float freeFraction = GetFreeFraction(storage);
                    if (freeFraction <= 0f) return;
                    __result += strength * freeFraction;
                }
                else if (tier == StorageTier.LastResort)
                {
                    __result -= strength;
                }
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][StoragePri] Postfix error (disabling): {ex.Message}");
                try { ManifestDeliveryMod.StoragePriorityEnabled.Value = false; } catch { }
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
        /// Maps a work bucket back to the item it is for, so tiers can be
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
                        "per-item tiers unavailable, building-wide defaults still work.");
                }
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Reverse map failed: {ex.Message}");
            }
        }

        // ── Component resolution ─────────────────────────────────────────────

        private static StoragePriorityData? ResolveData(Resource resource)
        {
            int id = resource.GetInstanceID();
            if (_dataByInstance.TryGetValue(id, out var cached))
                return cached;   // may be null — negative results are cached too

            StoragePriorityData? data = null;
            if (resource is StorageBuilding)          // load-bearing guard
                data = resource.GetComponent<StoragePriorityData>();

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
        }
    }

    /// <summary>
    /// Attaches <see cref="StoragePriorityData"/> to storage buildings.
    /// Done on a throttled sweep rather than an Awake patch: StorageBuilding does
    /// not declare its own Awake, so patching "StorageBuilding.Awake" would
    /// resolve to a base-class Awake and fire for every building in the game —
    /// the same trap as GetBaseScore. A sweep is simpler, keeps the hot path
    /// clean, and picks up buildings constructed mid-session.
    /// </summary>
    internal static class StoragePriorityAttacher
    {
        private const float SweepInterval = 5f;
        private static float _nextSweep;

        internal static void Tick()
        {
            var enabled = ManifestDeliveryMod.StoragePriorityEnabled;
            if (enabled == null || !enabled.Value) return;
            if (Time.time < _nextSweep) return;
            _nextSweep = Time.time + SweepInterval;

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<StorageBuilding>();
                int added = 0;
                foreach (var sb in all)
                {
                    if (sb == null) continue;
                    if (sb.GetComponent<StoragePriorityData>() != null) continue;
                    sb.gameObject.AddComponent<StoragePriorityData>();
                    added++;
                }
                if (added > 0)
                {
                    StoragePriorityPatches.ClearCaches();   // re-resolve negative caches
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD][StoragePri] Tracking {added} new storage building(s).");
                }
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Attach sweep failed: {ex.Message}");
            }
        }

        internal static void Reset() => _nextSweep = 0f;
    }
}
