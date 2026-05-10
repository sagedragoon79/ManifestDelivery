using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using ManifestDelivery.Components;

namespace ManifestDelivery.Systems
{
    /// <summary>
    /// Singleton aggregator for per-shop delivery statistics. Records every
    /// completed delivery (one ItemBundleDroppedOff event = one trip) into
    /// a position-keyed shop record. Holds lifetime + YTD totals and
    /// persists per-save under UserData/ManifestDelivery_Stats/.
    ///
    /// Public API:
    ///   • RecordDelivery(shopPos, shopName, mode, item, count, year)
    ///   • DumpReportToLog()
    ///   • LoadFromDisk()  / SaveToDisk()  — save-name aware
    ///   • Clear()         — used on scene unload
    /// </summary>
    public static class StatsTracker
    {
        // Position-keyed dictionary, same key derivation as
        // WagonShopEnhancement modes. Value = full stats record.
        private static readonly Dictionary<int, WagonShopStats> _shops
            = new Dictionary<int, WagonShopStats>();

        // Track which save we're loaded for so save-switches in the same
        // session don't bleed previous-save stats into current-save reports.
        private static string _loadedForSave = "";

        private const string DirName = "ManifestDelivery_Stats";

        // ── Hash key (matches WagonShopEnhancement.GetShopKey pattern) ────────
        public static int GetShopKey(Vector3 pos)
        {
            int ix = Mathf.RoundToInt(pos.x);
            int iz = Mathf.RoundToInt(pos.z);
            // Same hash as the modes file — same-shop matching
            return (ix * 397) ^ iz;
        }

        // ── Record one delivery ───────────────────────────────────────────────
        /// <summary>
        /// Add one delivery to the running totals. itemId may be -1 if the
        /// caller couldn't identify the item (e.g., the bundle was a
        /// multi-item request); count must always be the actual quantity.
        /// </summary>
        public static void RecordDelivery(
            Vector3 shopPos,
            string  shopName,
            int     modeInt,        // ShopMode cast to int
            int     itemId,         // -1 if unknown
            int     count,
            int     gameYear)
        {
            if (!ManifestDeliveryMod.StatsEnabled.Value) return;
            if (count <= 0) return;

            EnsureLoadedForCurrentSave();

            int key = GetShopKey(shopPos);
            if (!_shops.TryGetValue(key, out var stats))
            {
                stats = new WagonShopStats
                {
                    ShopPosition = shopPos,
                    ShopName     = shopName,
                };
                _shops[key] = stats;
            }
            else
            {
                // Refresh display name in case the user renamed the shop.
                stats.ShopName = shopName;
            }

            stats.EnsureYearCurrent(gameYear);

            // Trip counters
            stats.LifetimeTrips++;
            stats.YTDTrips++;

            // Item count
            stats.LifetimeItems += count;
            stats.YTDItems      += count;

            // Per-mode trips
            stats.LifetimeByMode.TryGetValue(modeInt, out int mLife);
            stats.LifetimeByMode[modeInt] = mLife + 1;
            stats.YTDByMode.TryGetValue(modeInt, out int mYtd);
            stats.YTDByMode[modeInt] = mYtd + 1;

            // Per-item bucket + raw/produced split (only if the caller knew the item)
            if (itemId >= 0)
            {
                stats.LifetimeByItem.TryGetValue(itemId, out int iLife);
                stats.LifetimeByItem[itemId] = iLife + count;
                stats.YTDByItem.TryGetValue(itemId, out int iYtd);
                stats.YTDByItem[itemId] = iYtd + count;

                if (ItemCategoryClassifier.IsRawMaterial(itemId))
                {
                    stats.LifetimeRawItems += count;
                    stats.YTDRawItems      += count;
                }
                else
                {
                    stats.LifetimeProducedItems += count;
                    stats.YTDProducedItems      += count;
                }
            }
        }

        // ── Format report ─────────────────────────────────────────────────────
        public static string FormatReport()
        {
            EnsureLoadedForCurrentSave();
            if (_shops.Count == 0)
                return "[MD][Stats] No deliveries recorded yet.";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("=== Manifest Delivery — Shop Hauling Report ===");

            // Aggregate totals across all shops for the header line
            int totalTripsLife = 0, totalTripsYTD = 0;
            int totalItemsLife = 0, totalItemsYTD = 0;
            int totalRawLife   = 0, totalRawYTD   = 0;
            int totalProdLife  = 0, totalProdYTD  = 0;
            foreach (var s in _shops.Values)
            {
                totalTripsLife += s.LifetimeTrips;       totalTripsYTD += s.YTDTrips;
                totalItemsLife += s.LifetimeItems;       totalItemsYTD += s.YTDItems;
                totalRawLife   += s.LifetimeRawItems;    totalRawYTD   += s.YTDRawItems;
                totalProdLife  += s.LifetimeProducedItems; totalProdYTD += s.YTDProducedItems;
            }

            sb.AppendLine($"All shops combined:  Lifetime {totalTripsLife} trips / {totalItemsLife:N0} items   |   This year {totalTripsYTD} trips / {totalItemsYTD:N0} items");
            sb.AppendLine($"  Raw materials:     Lifetime {totalRawLife:N0}   |   This year {totalRawYTD:N0}");
            sb.AppendLine($"  Produced goods:    Lifetime {totalProdLife:N0}   |   This year {totalProdYTD:N0}");
            sb.AppendLine();

            // Per-shop details, sorted by lifetime trips desc
            var sortedShops = _shops.Values
                .OrderByDescending(s => s.LifetimeTrips)
                .ToArray();

            int shopIndex = 0;
            foreach (var s in sortedShops)
            {
                shopIndex++;
                sb.AppendLine($"--- {shopIndex}. {s.ShopName} @ ({s.ShopPosition.x:F0}, {s.ShopPosition.z:F0}) ---");
                sb.AppendLine($"  Trips:    Lifetime {s.LifetimeTrips}   |   This year {s.YTDTrips}");
                sb.AppendLine($"  Items:    Lifetime {s.LifetimeItems:N0}   |   This year {s.YTDItems:N0}");
                sb.AppendLine($"  Raw:      Lifetime {s.LifetimeRawItems:N0}   |   This year {s.YTDRawItems:N0}");
                sb.AppendLine($"  Produced: Lifetime {s.LifetimeProducedItems:N0}   |   This year {s.YTDProducedItems:N0}");

                if (s.LifetimeByMode.Count > 0)
                {
                    sb.Append("  By mode (lifetime):");
                    foreach (var kv in s.LifetimeByMode.OrderByDescending(p => p.Value))
                        sb.Append($"  {ModeName(kv.Key)} {kv.Value}");
                    sb.AppendLine();
                }

                if (s.LifetimeByItem.Count > 0)
                {
                    sb.AppendLine("  Top items (lifetime):");
                    var top = s.LifetimeByItem.OrderByDescending(p => p.Value).Take(8);
                    foreach (var kv in top)
                        sb.AppendLine($"    {ItemDisplayName(kv.Key),-22} {kv.Value:N0}");
                }
            }
            sb.AppendLine("=== End of report ===");
            return sb.ToString();
        }

        public static void DumpReportToLog()
        {
            ManifestDeliveryMod.Log.Msg(FormatReport());
        }

        private static string ModeName(int modeInt)
        {
            try { return ((ManifestDelivery.Components.ShopMode)modeInt).ToString(); }
            catch { return "Mode" + modeInt; }
        }

        private static string ItemDisplayName(int itemId)
        {
            try { return ((ItemID)itemId).ToString(); }
            catch { return "Item" + itemId; }
        }

        // ── Persistence ───────────────────────────────────────────────────────
        public static void Clear()
        {
            _shops.Clear();
            _loadedForSave = "";
        }

        public static void EnsureLoadedForCurrentSave()
        {
            string save = WagonShopEnhancement_GetActiveSaveNameSafe();
            if (save == _loadedForSave) return;

            // Save changed (or first time) — save current data if there was any,
            // then load the new save's stats.
            if (!string.IsNullOrEmpty(_loadedForSave) && _shops.Count > 0)
            {
                SaveToDisk(_loadedForSave);
            }
            _shops.Clear();
            _loadedForSave = save;
            if (!string.IsNullOrEmpty(save))
                LoadFromDisk(save);
        }

        public static void SaveToDisk() => SaveToDisk(_loadedForSave);

        public static void SaveToDisk(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) return;
            try
            {
                string path = ResolveStatsPath(saveName);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var sw = new StreamWriter(path, false))
                {
                    sw.WriteLine("# ManifestDelivery stats v1");
                    foreach (var kv in _shops)
                    {
                        var s = kv.Value;
                        sw.WriteLine($"SHOP|{s.ShopPosition.x:F2}|{s.ShopPosition.y:F2}|{s.ShopPosition.z:F2}|{Sanitize(s.ShopName)}");
                        sw.WriteLine($"YEAR|{s.YTDYear}");
                        sw.WriteLine($"TRIPS|{s.LifetimeTrips}|{s.YTDTrips}");
                        sw.WriteLine($"ITEMS|{s.LifetimeItems}|{s.YTDItems}");
                        sw.WriteLine($"RAW|{s.LifetimeRawItems}|{s.YTDRawItems}");
                        sw.WriteLine($"PROD|{s.LifetimeProducedItems}|{s.YTDProducedItems}");
                        foreach (var p in s.LifetimeByItem)
                        {
                            int ytd = 0; s.YTDByItem.TryGetValue(p.Key, out ytd);
                            sw.WriteLine($"ITEM|{p.Key}|{p.Value}|{ytd}");
                        }
                        foreach (var p in s.LifetimeByMode)
                        {
                            int ytd = 0; s.YTDByMode.TryGetValue(p.Key, out ytd);
                            sw.WriteLine($"MODE|{p.Key}|{p.Value}|{ytd}");
                        }
                        sw.WriteLine("ENDSHOP");
                    }
                }
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][Stats] Save failed: {ex.Message}");
            }
        }

        public static void LoadFromDisk(string saveName)
        {
            try
            {
                string path = ResolveStatsPath(saveName);
                if (!File.Exists(path)) return;

                _shops.Clear();
                WagonShopStats? cur = null;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrEmpty(raw) || raw.StartsWith("#")) continue;
                    var parts = raw.Split('|');
                    switch (parts[0])
                    {
                        case "SHOP":
                            cur = new WagonShopStats
                            {
                                ShopPosition = new Vector3(
                                    float.Parse(parts[1]),
                                    float.Parse(parts[2]),
                                    float.Parse(parts[3])),
                                ShopName = parts.Length > 4 ? Unsanitize(parts[4]) : "Wagon Shop",
                            };
                            break;
                        case "YEAR" when cur != null:
                            cur.YTDYear = int.Parse(parts[1]);
                            break;
                        case "TRIPS" when cur != null:
                            cur.LifetimeTrips = int.Parse(parts[1]);
                            cur.YTDTrips      = int.Parse(parts[2]);
                            break;
                        case "ITEMS" when cur != null:
                            cur.LifetimeItems = int.Parse(parts[1]);
                            cur.YTDItems      = int.Parse(parts[2]);
                            break;
                        case "RAW" when cur != null:
                            cur.LifetimeRawItems = int.Parse(parts[1]);
                            cur.YTDRawItems      = int.Parse(parts[2]);
                            break;
                        case "PROD" when cur != null:
                            cur.LifetimeProducedItems = int.Parse(parts[1]);
                            cur.YTDProducedItems      = int.Parse(parts[2]);
                            break;
                        case "ITEM" when cur != null:
                            cur.LifetimeByItem[int.Parse(parts[1])] = int.Parse(parts[2]);
                            cur.YTDByItem[int.Parse(parts[1])]      = int.Parse(parts[3]);
                            break;
                        case "MODE" when cur != null:
                            cur.LifetimeByMode[int.Parse(parts[1])] = int.Parse(parts[2]);
                            cur.YTDByMode[int.Parse(parts[1])]      = int.Parse(parts[3]);
                            break;
                        case "ENDSHOP" when cur != null:
                            _shops[GetShopKey(cur.ShopPosition)] = cur;
                            cur = null;
                            break;
                    }
                }
                ManifestDeliveryMod.Log.Msg($"[MD][Stats] Loaded {_shops.Count} shop record(s) for save '{saveName}'.");
            }
            catch (Exception ex)
            {
                ManifestDeliveryMod.Log.Warning($"[MD][Stats] Load failed: {ex.Message}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string ResolveStatsPath(string saveName)
        {
            // Resolve UserDataDirectory via reflection so we work across both
            // pre-0.7 (MelonUtils.UserDataDirectory) and 0.7+ (MelonEnvironment.
            // UserDataDirectory) without compile-time obsolete-API errors.
            string baseDir = ResolveUserDataDir();
            // Sanitize any slashes in saveName (FF uses "{slot}/{savename}" format)
            string fname = saveName.Replace('/', '_').Replace('\\', '_') + ".txt";
            return Path.Combine(baseDir, DirName, fname);
        }

        private static string Sanitize(string s) => s.Replace('|', ' ').Replace('\n', ' ');
        private static string Unsanitize(string s) => s;

        // Cached UserData directory lookup. Tries new MelonEnvironment first,
        // falls back to old MelonUtils, last resort to AppDomain.BaseDirectory.
        private static string? _userDataDirCached;
        private static string ResolveUserDataDir()
        {
            if (_userDataDirCached != null) return _userDataDirCached;
            try
            {
                // 0.7+ : MelonLoader.MelonEnvironment.UserDataDirectory
                var t = Type.GetType("MelonLoader.MelonEnvironment, MelonLoader");
                if (t != null)
                {
                    var p = t.GetProperty("UserDataDirectory",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static);
                    if (p != null)
                    {
                        var v = p.GetValue(null, null) as string;
                        if (!string.IsNullOrEmpty(v)) return _userDataDirCached = v!;
                    }
                }
                // pre-0.7 : MelonLoader.MelonUtils.UserDataDirectory
                var tu = Type.GetType("MelonLoader.MelonUtils, MelonLoader");
                if (tu != null)
                {
                    var p = tu.GetProperty("UserDataDirectory",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static);
                    if (p != null)
                    {
                        var v = p.GetValue(null, null) as string;
                        if (!string.IsNullOrEmpty(v)) return _userDataDirCached = v!;
                    }
                }
            }
            catch { }
            // Final fallback — game directory + UserData
            return _userDataDirCached = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, "UserData");
        }

        private static string WagonShopEnhancement_GetActiveSaveNameSafe()
        {
            // Match WagonShopEnhancement.GetActiveSaveName via reflection so
            // we don't duplicate the SaveManager lookup logic. Static method.
            try
            {
                var mi = typeof(ManifestDelivery.Components.WagonShopEnhancement)
                    .GetMethod("GetActiveSaveName",
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                if (mi == null) return "";
                return (mi.Invoke(null, null) as string) ?? "";
            }
            catch { return ""; }
        }
    }
}
