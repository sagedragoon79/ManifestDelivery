using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ManifestDelivery.Components;

namespace ManifestDelivery.Systems
{
    /// <summary>
    /// Per-save persistence for storage priorities (1–9, 9 highest).
    ///
    /// Mirrors MD's proven persistence shape (see <c>WagonShopEnhancement</c> and
    /// <c>StatsTracker</c>): one file per save under
    /// <c>UserData/ManifestDelivery_StoragePriorities/&lt;save&gt;.txt</c>, loaded
    /// lazily, reloaded when the active save changes mid-session.
    ///
    /// Two traps the handoff called out, and how this avoids them:
    /// - **Global file leaking between towns** → keyed per save, via the same
    ///   latched save name that fixed MD v1.0.16/1.0.18 (a transiently-empty
    ///   <c>SaveManager.activeSaveFileName</c> used to send writes to
    ///   default.txt while reads came from the real file).
    /// - **Position keys losing settings on relocation** → the game relocates
    ///   by constructing a NEW building at the destination, so
    ///   <see cref="CarryTo"/> (called from RelocationPatches when the move is
    ///   confirmed) copies the priorities to the destination's key. Live
    ///   priorities ride on a <see cref="StoragePriorityData"/> component, and
    ///   <see cref="SyncFromLive"/> merges each building into the on-disk map at
    ///   its CURRENT position immediately before writing.
    ///
    /// Format (one row per building/item, '|' separated):
    ///   <c>positionKey|itemKey|priority</c>   itemKey -1 = storage-wide priority,
    ///   priority 1–9.
    /// </summary>
    public static class StoragePriorityStore
    {
        private const string DirName = "ManifestDelivery_StoragePriorities";

        /// <summary>positionKey → (itemKey → priority 1–9)</summary>
        private static readonly Dictionary<int, Dictionary<int, int>> _byKey =
            new Dictionary<int, Dictionary<int, int>>();

        private static string _loadedForSave = null!;

        /// <summary>
        /// Same hash MD's mode persistence uses: combines rounded X and Z as
        /// independent dimensions so close-but-distinct buildings don't collide
        /// (an earlier x*1000+z scheme did).
        /// </summary>
        public static int ComputeKey(Vector3 pos)
        {
            int ix = Mathf.RoundToInt(pos.x);
            int iz = Mathf.RoundToInt(pos.z);
            unchecked { return (ix * 397) ^ iz; }
        }

        /// <summary>Kept for callers; saving happens on the game's save cadence.</summary>
        public static void MarkDirty() { }

        // ── Load ─────────────────────────────────────────────────────────────

        public static void EnsureLoadedForCurrentSave()
        {
            string current = GetActiveSaveName();
            if (_loadedForSave == current) return;

            _loadedForSave = current;
            _byKey.Clear();

            // A brand-new town has no save name until its first save. Don't read
            // default.txt for it: its priorities live on the building components
            // and SaveToDisk writes them under the real name at that first save.
            if (string.IsNullOrEmpty(current)) return;

            string path = GetSaveFilePath(current);
            if (!File.Exists(path)) return;

            try
            {
                int rows = 0;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length != 3) continue;
                    if (!int.TryParse(parts[0], out int key)) continue;
                    if (!int.TryParse(parts[1], out int itemKey)) continue;
                    if (!int.TryParse(parts[2], out int priority)) continue;
                    if (priority < StoragePriorityData.MinPriority
                        || priority > StoragePriorityData.MaxPriority) continue;

                    if (!_byKey.TryGetValue(key, out var priorities))
                        _byKey[key] = priorities = new Dictionary<int, int>();
                    priorities[itemKey] = priority;
                    rows++;
                }

                ManifestDeliveryMod.Log.Msg(
                    $"[MD][StoragePri] Loaded {rows} priority row(s) for save '{current}'.");
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Load failed: {ex.Message}");
            }
        }

        /// <summary>Pushes any saved priorities for this building's position into its component.</summary>
        public static void RestoreInto(StoragePriorityData data)
        {
            if (data == null) return;
            try
            {
                EnsureLoadedForCurrentSave();
                int key = ComputeKey(data.transform.position);
                data.PersistedKey = key;
                if (_byKey.TryGetValue(key, out var priorities) && priorities.Count > 0)
                    data.LoadPriorities(priorities);
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Restore failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Keeps a storage's priorities when you relocate it. The game takes the
        /// building down and constructs a new one at the destination, which
        /// restores by ITS position and would find nothing. Called when you
        /// confirm the move (RelocationPatches); the next game save writes it.
        /// "Nothing set" is carried too, so an old entry at the destination
        /// (from a demolished storage) can't leak into the moved one.
        /// </summary>
        public static void CarryTo(GameObject building, Vector3 destination)
        {
            if (building == null) return;
            try
            {
                EnsureLoadedForCurrentSave();

                // The live component when it has restored; otherwise the saved
                // row, since components attach lazily and an untouched storage
                // may not have one yet.
                Dictionary<int, int>? source = null;
                var data = building.GetComponent<StoragePriorityData>();
                if (data != null && data.Restored)
                {
                    source = new Dictionary<int, int>();
                    foreach (var kv in data.PrioritiesRO) source[kv.Key] = kv.Value;
                }
                else if (_byKey.TryGetValue(ComputeKey(building.transform.position), out var saved))
                {
                    source = saved;
                }

                bool any = source != null && source.Count > 0;
                foreach (int key in WagonShopEnhancement.KeysNear(destination))
                {
                    if (any) _byKey[key] = new Dictionary<int, int>(source!);
                    else _byKey.Remove(key);
                }

                if (any)
                    ManifestDeliveryMod.Log.Msg(
                        $"[MD][StoragePri] {building.name} relocating: keeping {source!.Count} priority " +
                        $"setting(s) for the new site at ({destination.x:F1},{destination.z:F1}).");
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning(
                    $"[MD][StoragePri] Carrying priorities to a relocated storage failed: {ex.Message}");
            }
        }

        // ── Save ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Folds live components into the on-disk map at their CURRENT positions.
        ///
        /// Deliberately a MERGE, not a rebuild-from-scratch. Components attach
        /// lazily and restore one frame later, so a save during that window
        /// would otherwise see "nothing set" on every building and persist an
        /// empty map over the player's real settings. Only components that have
        /// actually restored may write, and untouched keys are left alone.
        ///
        /// Handles each case explicitly:
        /// - **Relocated** → its remembered <see cref="StoragePriorityData.PersistedKey"/>
        ///   differs from its current key, so the stale entry is removed.
        /// - **Cleared** (nothing set) → its key is removed.
        /// - **Not yet restored / not yet attached** → skipped, disk entry preserved.
        /// - **Demolished** → entry preserved; a storage later rebuilt on the same
        ///   footprint simply inherits the old priority, which is the friendly
        ///   behaviour and costs a few bytes.
        /// </summary>
        private static void SyncFromLive()
        {
            foreach (var data in StoragePriorityData.Live)
            {
                if (data == null || !data.Restored) continue;   // never let an un-restored component write

                int key = ComputeKey(data.transform.position);

                // Relocated since load/last save — drop the entry at the old spot.
                if (data.PersistedKey != int.MinValue && data.PersistedKey != key)
                    _byKey.Remove(data.PersistedKey);

                if (!data.HasAnyPriority)
                {
                    _byKey.Remove(key);
                    data.PersistedKey = key;
                    continue;
                }

                var priorities = new Dictionary<int, int>();
                foreach (var kv in data.PrioritiesRO)
                    priorities[kv.Key] = kv.Value;

                _byKey[key] = priorities;
                data.PersistedKey = key;
            }
        }

        public static void SaveToDisk()
        {
            try
            {
                // Only meaningful once a real save is active; otherwise we'd
                // write a stray default.txt (the v1.0.16 bug class).
                string current = GetActiveSaveName();
                if (string.IsNullOrEmpty(current)) return;

                // Load first, THEN merge. Without this, a save that happens
                // before the store ever read the file would start from an empty
                // map and delete the player's existing priorities.
                EnsureLoadedForCurrentSave();
                SyncFromLive();

                string path = GetSaveFilePath(current);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (_byKey.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                var lines = new List<string>();
                foreach (var kv in _byKey)
                    foreach (var t in kv.Value)
                        lines.Add($"{kv.Key}|{t.Key}|{t.Value}");

                File.WriteAllLines(path, lines.ToArray());
                _loadedForSave = current;
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][StoragePri] Save failed: {ex.Message}");
            }
        }

        /// <summary>Drops the in-memory map so the next save loads its own rows.</summary>
        public static void Clear()
        {
            _byKey.Clear();
            _loadedForSave = null!;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string GetActiveSaveName()
        {
            // Reuse MD's latched accessor — it guards the transient-empty
            // activeSaveFileName window that caused the v1.0.16/1.0.18 bugs.
            return WagonShopEnhancement.GetActiveSaveName();
        }

        private static string GetSaveFilePath(string saveName)
        {
            string safe = string.IsNullOrEmpty(saveName) ? "default" : saveName;
            safe = safe.Replace('/', '_').Replace('\\', '_');
            foreach (char c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return Path.Combine(ResolveUserDataDir(), DirName, safe + ".txt");
        }

        // Same MelonEnvironment-then-MelonUtils probe StatsTracker uses, so this
        // compiles and runs across MelonLoader pre-0.7 and 0.7+.
        private static string? _userDataDirCached;
        private static string ResolveUserDataDir()
        {
            if (_userDataDirCached != null) return _userDataDirCached;
            try
            {
                foreach (string typeName in new[]
                {
                    "MelonLoader.MelonEnvironment, MelonLoader",
                    "MelonLoader.MelonUtils, MelonLoader",
                })
                {
                    var t = Type.GetType(typeName);
                    var p = t?.GetProperty("UserDataDirectory",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var v = p?.GetValue(null, null) as string;
                    if (!string.IsNullOrEmpty(v)) return _userDataDirCached = v!;
                }
            }
            catch { }
            return _userDataDirCached =
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
        }
    }
}
