using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ManifestDelivery.Patches
{
    /// <summary>
    /// M0 ROUTING SPIKE for the Storage Priorities fold
    /// (see <c>_handoffs/2026-08-21_storage-priorities-fold.md</c>).
    ///
    /// GOAL: prove that biasing a storage's logistics base score actually
    /// redirects haulers, BEFORE any tier model, UI, or persistence is built.
    ///
    /// ═══ ANTI-PING-PONG CONTRACT (the load-bearing safety design) ═══════════
    /// A naive score bias WILL shuffle goods between storages forever. Three
    /// rules make that structurally impossible, not merely unlikely. Any future
    /// milestone (M1 tiers, M2 UI, M4 rebalancer) MUST preserve all three.
    ///
    /// RULE 1 — DESTINATION ONLY. GetBaseScore feeds two different candidate
    ///   lists: CanStore* buckets = "where can this go" (destinations,
    ///   decompile L124320) and HasItem* buckets = "where can this be taken
    ///   from" (sources, L124252). We bias ONLY CanStore.
    ///   Why it matters: if a Preferred storage were boosted in BOTH, it would
    ///   attract goods *and* be the preferred place to drain — so A pulls from
    ///   B, then B pulls right back from A, forever. Biasing only the
    ///   destination side means goods flow toward Preferred and STOP there.
    ///   The cycle has no return edge; it cannot close.
    ///
    /// RULE 2 — TAPER WITH FULLNESS. Vanilla's base score is
    ///   (1 - fullness) * 100, which naturally steers away from full storages.
    ///   A flat bias would override that and overfill the Preferred storage —
    ///   which then trips its max-quota shed (StorageQuotaHandler emits a
    ///   TakeOut above max*1.1), pushing goods back out, which the bias then
    ///   pulls back in. That is a genuine oscillator. We scale the bias by the
    ///   fraction of space actually free, so it fades to zero exactly as the
    ///   storage fills. Preference never beats physics.
    ///
    /// RULE 3 — NEVER BIAS THE OVER-CAPACITY FALLBACK. Vanilla has parallel
    ///   CanStore*OverCapacity buckets used when everything normal is full.
    ///   Cramming more into an already-over-capacity storage is the same
    ///   overfill→shed→refill loop as Rule 2. Those buckets are left untouched.
    ///
    /// Net effect: the bias can only ever REDIRECT haul work that vanilla
    /// already decided to do, toward a destination that has room. It never
    /// creates haul work, and never makes a storage attractive to empty.
    /// (Actively moving already-stored stock is the M4 rebalancer — a different
    /// risk class, deliberately deferred; see the handoff.)
    ///
    /// ── Why this patches Resource, not StorageBuilding ────────────────────
    /// The handoff named <c>StorageBuilding.GetBaseScore</c>, but StorageBuilding
    /// does not declare it: <c>StorageBuilding : Building : Resource</c>, and the
    /// method lives on <c>Resource</c> (decompile L165149). No storage type
    /// overrides it, so every storage funnels through this one non-virtual entry
    /// point — but it also means this postfix fires for EVERY Resource in the
    /// game, so the <c>is StorageBuilding</c> guard is load-bearing.
    /// Tempting-but-wrong alternative: <c>GetBaseScoreModifications()</c> looks
    /// like the natural "add your bonus" hook, but it is <c>protected virtual</c>
    /// and IS overridden by Granary / Root Cellar / Treasury (their built-in
    /// +100) — patching the base would silently never fire for exactly those.
    ///
    /// ── How the bias reaches routing ──────────────────────────────────────
    /// GetBaseScore runs on the MAIN THREAD while building logistics job data;
    /// the value is copied into the Burst job struct (L122925) and the scorer
    /// ranks candidates as <c>baseScore - distance + modifiers</c> (L123115).
    /// So a postfix here is the whole mechanism — no transpiler needed.
    ///
    /// ── Perf ──────────────────────────────────────────────────────────────
    /// On the logistics hot path, firing for all Resources: the disabled path is
    /// a single string-empty check; the enabled path is two HashSet/Dictionary
    /// lookups. No per-call allocation, no <c>gameObject.name</c> access (it
    /// allocates in Unity) and no enum ToString — the bucket classification and
    /// the name scan are each computed once and cached.
    /// </summary>
    [HarmonyPatch(typeof(Resource), "GetBaseScore", new Type[] { typeof(IQueryContainer) })]
    internal static class StoragePriorityPatches
    {
        /// <summary>Instance IDs of storages currently matching the test target.</summary>
        private static readonly HashSet<int> _biasedIds = new HashSet<int>();

        /// <summary>
        /// Per-bucket cache of "is this a biasable destination bucket?" so the
        /// enum-name classification (which allocates) happens once per bucket.
        /// </summary>
        private static readonly Dictionary<IQueryContainer, bool> _bucketIsDestination =
            new Dictionary<IQueryContainer, bool>();

        /// <summary>Throttles the "bias applied" log to once per building.</summary>
        private static readonly HashSet<int> _loggedIds = new HashSet<int>();

        private static float  _nextRescan;
        private static string _lastTarget = "";

        /// <summary>Rescan cadence — picks up newly-built or renamed storages.</summary>
        private const float RescanInterval = 10f;

        private static void Postfix(Resource __instance, IQueryContainer container, ref float __result)
        {
            try
            {
                var targetPref = ManifestDeliveryMod.StoragePriorityTestTarget;
                if (targetPref == null) return;

                string target = targetPref.Value;
                if (string.IsNullOrEmpty(target)) return;   // disabled — the hot-path exit

                // RULE 1: destinations only. Never bias the source side.
                if (!IsDestinationBucket(container)) return;

                if (target != _lastTarget || Time.time >= _nextRescan)
                    Rescan(target);

                if (_biasedIds.Count == 0) return;
                if (!(__instance is StorageBuilding storage)) return;   // load-bearing guard
                if (!_biasedIds.Contains(__instance.GetInstanceID())) return;

                float bias = ManifestDeliveryMod.StoragePriorityTestBias != null
                    ? ManifestDeliveryMod.StoragePriorityTestBias.Value : 0f;
                if (bias == 0f) return;

                // RULE 2: fade the bias out as the storage fills, so preference
                // can never overfill a storage into the shed/refill oscillation.
                float freeFraction = GetFreeFraction(storage);
                if (freeFraction <= 0f) return;

                float applied = bias * freeFraction;
                __result += applied;
                LogFirstTouch(storage, __result, applied, freeFraction);
            }
            catch (Exception ex)
            {
                // Never let a spike take down the logistics query — disable and move on.
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][StoragePri] Postfix error (disabling spike): {ex.Message}");
                _biasedIds.Clear();
                _lastTarget = "\0disabled";
                _nextRescan = float.MaxValue;
            }
        }

        /// <summary>
        /// RULE 1 + RULE 3. True only for normal CanStore* buckets (destinations
        /// with room). HasItem* (sources) and CanStore*OverCapacity (the
        /// already-full fallback) both return false. Cached per bucket instance:
        /// the enum ToString happens once, never on the hot path.
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
        /// RULE 2. Fraction of this storage's capacity that is still free
        /// (1 = empty, 0 = full). Mirrors how vanilla's own base score decays.
        /// </summary>
        private static float GetFreeFraction(StorageBuilding storage)
        {
            try
            {
                uint available = storage.GetAvailableStorageSpace(out uint currentCount);
                uint capacity  = available + currentCount;
                if (capacity == 0) return 0f;
                return (float)available / capacity;
            }
            catch
            {
                return 0f;   // unknown capacity → no bias, the safe direction
            }
        }

        /// <summary>
        /// Rebuilds the matching-storage set. Only place that touches
        /// gameObject.name or walks the scene — throttled to RescanInterval.
        /// </summary>
        private static void Rescan(string target)
        {
            _nextRescan = Time.time + RescanInterval;

            bool targetChanged = target != _lastTarget;
            if (targetChanged)
            {
                _lastTarget = target;
                _loggedIds.Clear();
            }

            _biasedIds.Clear();
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<StorageBuilding>();
                foreach (var sb in all)
                {
                    if (sb == null) continue;
                    if (sb.gameObject.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                        _biasedIds.Add(sb.GetInstanceID());
                }

                if (targetChanged)
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD][StoragePri] Spike target '{target}' matched {_biasedIds.Count} " +
                        $"storage building(s). Bias applies to DESTINATION routing only, " +
                        $"tapered by free space (anti-ping-pong).");
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Rescan failed: {ex.Message}");
            }
        }

        private static void LogFirstTouch(StorageBuilding storage, float finalScore, float applied, float freeFraction)
        {
            int id = storage.GetInstanceID();
            if (!_loggedIds.Add(id)) return;

            ManifestDeliveryMod.Log.Msg(
                $"[MD][StoragePri] +{applied:F0} → '{storage.gameObject.name}' " +
                $"(score now {finalScore:F0}, {freeFraction * 100f:F0}% free)");
        }

        /// <summary>Drops caches on scene unload — instance IDs don't survive a reload.</summary>
        internal static void ClearCaches()
        {
            _biasedIds.Clear();
            _bucketIsDestination.Clear();
            _loggedIds.Clear();
            _lastTarget = "";
            _nextRescan = 0f;
        }
    }
}
