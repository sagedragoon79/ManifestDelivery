using System.Collections.Generic;
using UnityEngine;

namespace ManifestDelivery.Components
{
    /// <summary>
    /// Aggregated delivery statistics for a single Wagon Shop, summed across
    /// all wagons assigned to that shop. Vanilla's wagon-level UI already
    /// shows per-wagon current/last-year delivery counts; this aggregation
    /// adds the things vanilla doesn't:
    ///
    ///   • Per-SHOP totals (sum across that shop's wagons)
    ///   • LIFETIME totals (vanilla resets at year boundary; we keep going)
    ///   • Per-ITEM breakdown (vanilla shows trip count, not what was hauled)
    ///   • RAW vs PRODUCED material split
    ///
    /// Stored per-save under UserData/ManifestDelivery_Stats/&lt;saveName&gt;.txt.
    /// Position-keyed using the same hash as WagonShopEnhancement modes
    /// so re-load matches the right shop.
    /// </summary>
    public class WagonShopStats
    {
        // Position-keyed identity (matches WagonShopEnhancement persistence key)
        public Vector3 ShopPosition;
        public string  ShopName = "Wagon Shop";

        // Year boundary tracking — when FF current year > YTDYear, YTD resets.
        // -1 means "never recorded" so the first delivery initializes it.
        public int YTDYear = -1;

        // Trip counts (one delivery = one trip)
        public int LifetimeTrips;
        public int YTDTrips;

        // Total item count summed over all trips
        public int LifetimeItems;
        public int YTDItems;

        // Raw vs Produced split — see ItemCategoryClassifier for the rules.
        public int LifetimeRawItems;
        public int YTDRawItems;
        public int LifetimeProducedItems;
        public int YTDProducedItems;

        // Per-item breakdown. Keyed by ItemID enum cast to int so the
        // dictionary survives without typing concerns.
        public Dictionary<int, int> LifetimeByItem = new Dictionary<int, int>();
        public Dictionary<int, int> YTDByItem      = new Dictionary<int, int>();

        // Per-mode trip counts. Key = ShopMode int (0=Standard, 1=Camp, 2=Hub).
        public Dictionary<int, int> LifetimeByMode = new Dictionary<int, int>();
        public Dictionary<int, int> YTDByMode      = new Dictionary<int, int>();

        /// <summary>
        /// Roll YTD counters into a fresh year if the current game year is
        /// past YTDYear. Lifetime totals are NOT touched (they accumulate
        /// continuously). Called inside RecordDelivery before the new
        /// trip is added so the trip lands in the correct bucket.
        /// </summary>
        public void EnsureYearCurrent(int currentYear)
        {
            if (YTDYear == currentYear) return;
            // First-ever record OR year rolled over → reset YTD buckets.
            YTDYear              = currentYear;
            YTDTrips             = 0;
            YTDItems             = 0;
            YTDRawItems          = 0;
            YTDProducedItems     = 0;
            YTDByItem.Clear();
            YTDByMode.Clear();
        }
    }
}
