# Manifest Delivery — Changelog

All notable changes to this mod, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely.

---

## [1.0.13] — 2026-05-15

### Fixed
- **YTD rollover restored** on the post-DLC build. The 1.0.12 type-based
  scan for a `TimeManager` int year member came up empty because FF's
  year doesn't live as a plain int on TimeManager — it's nested inside
  the `currentDate` property, which is a `CEDateTime` struct
  (`TimeManager.currentDate.year`). This is the same source vanilla's
  "shipped last year" and "produced last year" stats read from.
- **Fix:** the year resolver now binds directly to
  `TimeManager.currentDate.year` via reflection, with fallbacks for
  `startDate.year`, any other `CEDateTime`-typed member on TimeManager,
  and finally the old int-on-TimeManager scan for unusual builds. The
  resolved getter walks property → CEDateTime struct → `year` getter
  and caches the chain, so each delivery only pays one reflection-invoke
  pair.

### Internal
- Version bump `1.0.12.0` → `1.0.13.0`.

---

## [1.0.12] — 2026-05-12

### Fixed
- **DLC-compatibility for both the logistics scanner and YTD year
  rollover.** After the DLC dropped, two of our reflection resolvers
  stopped finding their targets:
  - `GameManager.logisticsAggregator` was reshuffled again — the
    1.0.11 name-based fallback couldn't find either spelling.
    Backhaul/CampHaul logged `could not resolve LogisticsAggregator`
    and dropped to empty scans.
  - `TimeManager.Instance` returned null in the new build, leaving
    stats stuck in the wrong year bucket (`YTD rollover disabled`).
- **Fix:** both lookups now do a *type-based* scan as their last
  resort. The LogisticsAggregator resolver walks every field and
  property on `GameManager` and returns the first member whose value
  is a `LogisticsAggregator`, then falls back to
  `FindObjectOfType<LogisticsAggregator>()`. The year resolver tries
  the standard `Instance` property/field, then
  `UnitySingleton<TimeManager>.Instance`, then
  `FindObjectOfType<TimeManager>`, and then scans `TimeManager` for
  any int field/property containing "year" in its name. Both
  resolvers cache the resolved member so they only walk metadata once.

### Internal
- Version bump `1.0.11.0` → `1.0.12.0`.

---

## [1.0.11] — 2026-05-12

### Fixed
- **Compat with the current Farthest Frontier build.** FF moved
  `GameManager.logisiticsAggregator` to non-public access (and may have
  fixed the typo to `logisticsAggregator`), which broke the direct
  field access we used for the logistics scanner. Both
  `ReturnTripSearchEntry` and `CampHaulSearchEntry` now resolve the
  aggregator via reflection, trying both the original typo'd name and
  the corrected one — works across game-build versions either way.

### Internal
- Version bump `1.0.10.0` → `1.0.11.0`.

---

## [1.0.10] — 2026-04-26

### Added
- **Per-shop hauling stats.** New aggregator records every wagon delivery
  into a position-keyed shop record. Tracks **lifetime** and **year-to-date**
  totals separately — vanilla resets at year boundary, our totals keep
  accumulating. The Steam Workshop description now mentions this as the
  upgrade-feature for the v1.0.10 release.
- **Item breakdown** per shop: top-N items hauled all-time and this year.
  Vanilla shows the trip count but not what was actually carried.
- **Raw vs Produced split** — see `ItemCategoryClassifier.cs` for the rule
  list. Stone, Wheat, Berries, RawMeat, Hide, Honey, Wool etc. count as
  Raw; Bread, Lumber, Cloth, Cheese, Beer, Charcoal, Firewood etc. count
  as Produced. Per-game-version mismatches are silently ignored.
- **Per-mode trip counts** per shop (Standard / Camp / Hub) — useful when
  comparing how much work each mode actually does for you.
- **Report keybind** (default `Ctrl+Shift+M`, configurable via
  `StatsReportKey`). Press in-game to dump a formatted breakdown to the
  MelonLoader log.
- **`StatsEnabled` toggle** (default `true`). Set false to skip recording
  entirely; existing files are left untouched on disk.

### Persistence
- Stats stored per-save under `UserData/ManifestDelivery_Stats/<saveName>.txt`.
- Same per-save isolation as the modes file — no cross-save leaks.
- Flushed on `SaveManager.Save`, `OnApplicationQuit`, and scene transitions.
- Custom binary-ish text format (one line per record); easy to inspect or
  hand-edit if you need to reset a counter.

### Internal
- New `Components/WagonShopStats.cs`, `Systems/StatsTracker.cs`,
  `Systems/ItemCategoryClassifier.cs`, `Patches/StatsSavePatch.cs`,
  delivery hook integration in `Patches/DeliveryLogPatches.cs`.
- Reflection-based resolver for `UserDataDirectory` so the mod compiles
  cleanly across MelonLoader pre-0.7 (`MelonUtils`) and 0.7+
  (`MelonEnvironment`) without obsolete-API errors.
- Reflection-based resolver for `TimeManager.year` (or `Year`/`currentYear`/
  etc.) so YTD rollover works regardless of FF's exact field name.

### Note
- Vanilla wagon UI still shows per-wagon current/last-year delivery
  counts. This feature is the per-SHOP layer that aggregates across all
  the wagons of that shop, plus item breakdown that vanilla doesn't show.

---

## [1.0.9] — 2026-04-26

### Performance
- **Storage classification cache** in `ReturnTripSearchEntry`. Previously
  every backhaul scan walked the parent transform of every candidate
  requester and called `GetComponents<MonoBehaviour>` to test for storage
  type names — hundreds of allocations per second on a busy map. Now
  cached per-`LogisticsRequester` for the lifetime of the scene.
- Cache is cleared on `OnSceneWasLoaded` so destroyed-then-replaced
  requesters from the previous map don't leak as Unity-null dictionary
  keys (which still hash but won't equate).

### Changed
- **CampHaul EMPTY log** rate-limited to state transitions only
  (was-finding → just-emptied), instead of one line per scan cooldown.
  Idle camps with no production used to spam hundreds of EMPTY lines
  per minute into the Melon log; now they emit once when work runs out
  and stay quiet until production restarts. New `LastCampHaulScanWasEmpty`
  flag on `WagonEnhancementData` tracks the prior-scan state.

### Internal
- Version bump `1.0.8.0` → `1.0.9.0`.

---

## [1.0.8] — 2026-04-26

### Added
- **Keep Clarity settings-panel integration.** When KeepClarity.dll is
  installed, Manifest Delivery's preferences appear in the in-game
  settings panel with proper labels, tooltips, sliders, sub-categories
  (Master, Wagon Caps, Backhaul AI, Camp & Hub, Storage Cart, Hotkeys),
  and `VisibleWhen` gating (e.g. radius is hidden when backhaul is
  disabled). All access is reflective — KeepClarity is a soft dependency,
  not a build dep.
- New `KeepClarityIntegration.cs` follows the canonical WotW template.
  Registers a wagon-tan accent colour and order=20 in the panel.

### Internal
- Version bump `1.0.7.0` → `1.0.8.0`.

---

## [1.0.7] — 2026-04-26

### Fixed
- **Wagon shop modes were leaking between save files.** Setting modes in Save A
  and then loading Save B in the same session caused B's shops to inherit A's
  modes. Modes are now persisted per-save under
  `UserData/ManifestDelivery_Modes/<saveName>.txt`, lazy-loaded via
  `EnsureLoadedForCurrentSave()`. Also handles mid-session save-switching.
- **Position-hash collision** on close-but-distinct shops. Previous formula
  (`x*1000+z`) collapsed axes and shared keys when shop coords were similar.
  Switched to `(ix*397)^iz` on rounded ints — no axis collapse.

### Added
- **`PreferWorkshopInput` config toggle** (default `false`). When enabled, after
  a wagon drop-off the next backhaul prefers production buildings (Bakery,
  Smithy, Tannery, Carpenter, Brewery, Cobbler, etc.) over storage shuffling
  (Storehouse, Storage Depot, Root Cellar, Granary, Marketplace, Treasury,
  Stockyard). Workshops always win when in range, regardless of distance.
  Falls back to closest-storage when no workshops have active requests.
  Default `false` preserves vanilla closest-wins behaviour.
- **Backhaul diagnostic logs.** `ReturnTrip CLAIM` now includes mode name and
  distance from both shop and wagon. New `ReturnTrip EMPTY` line fires when
  a scan ran but found no candidates, with mode + center + radius for
  diagnosis. Workshop-priority decisions log which candidate was chosen vs
  what closest-overall would have picked.

### Changed
- **TransportWagon Hub-mode +20% capacity boost** rewritten without reflection.
  `TransportWagon.temporaryInventory` and `ItemStorage.carryCapacity` are both
  public; no need for the reflection workaround. Identical behaviour.
- **Storage Cart speed pref tooltip clarified.** `StorageCartSpeedMult` only
  affects the building-relocation animation when a Storage Cart is rallied
  to a point — *not* Transport Wagon haul speed.
- **`ReturnTripSearchEntry`** zeroed vanilla's `_delayBetweenNewTaskSearch`
  and `_additionalDelayIfLastTaskSearchFailed`. Trip is `JustDelivered`-gated,
  not cooldown-gated; vanilla's 2-second fail-delay was just delaying the
  next legitimate check.

### Internal
- Version bumped `1.0.0.0` → `1.0.7.0` in csproj.
- Plugin.cs `MelonInfo` and startup log message updated to `1.0.7`.

---

## [1.0.6]

### Added
- Per-delivery logging so you can audit wagon trips in the Melon log.
- Shop-link back-fill: data component now resolves its `WagonShopEnhancement`
  even when the assignment fired before our component spawned.
- Button polish on the in-shop mode UI.

---

## [1.0.5]

### Added
- **Permanent mode buttons in the wagon shop info window.** Mode is no longer
  hidden behind a tooltip — Camp / Hub / Standard are visible and clickable
  any time the shop is selected.

---

## [1.0.4]

### Added
- **Placement preview circles** during shop placement. The work-radius circle
  is drawn while you're positioning the building, so you can see Camp/Hub
  coverage *before* committing.
- Existing coverage display: existing shops' radii also render during
  placement so you can avoid overlap.

---

## [1.0.3]

### Added
- **Shop mode persistence across save/load and game restart.** Modes are
  written to disk per-shop using position-based keys, so Camp/Hub
  designations survive both reload and game-quit.

### Note
- *(This v1.0.3 persistence used a single global modes file. v1.0.7 fixes the
  cross-save leak this introduced.)*

---

## [1.0.2]

### Fixed
- Visual radius circle now matches the actual logistics radius.
- Selection visibility — the wagon shop's selection ring no longer hides
  behind other UI.
- CampHaul wagon speed now respects the configured multiplier.

### Added
- **`ModEnabled` master switch** in config. Disable the entire mod in one
  flag if you need to rule it out for a compat issue.
- File version metadata in csproj (1.0.2).
- Default `HubWorkRadius` raised from 100u to 200u — Hubs now reach farther,
  matching the mode's "town-wide service" intent.

---

## [1.0.1]

### Added
- **Rebrand:** `WagonShopsEnhanced` → `Manifest Delivery`.
- Camp backhaul for firewood, processed food, and beer (the items camp
  residences cannot produce themselves).

### Fixed
- Worker slot resizing on Hub mode (slots stretched correctly to match the
  mode's higher worker cap).

### Internal
- `.gitignore` added.

---

## [1.0.0] — First public release

Initial release. Wagon Shop overhaul featuring:

- **Three operating modes** per shop:
  - **Standard** — vanilla behaviour with quality-of-life polish
  - **Camp** — anchored to a remote production zone, hauls back to hub storage
  - **Hub** — town-center logistics shop with extended radius and wagon cap
- **Backhaul** (`ReturnTripSearchEntry`) — wagons opportunistically pick up
  one more trip on the way back instead of returning empty.
- **Camp haul** (`CampHaulSearchEntry`) — Camp-mode wagons proactively pull
  output from nearby production buildings to hub storage.
- **Storage Cart** support: per-shop capacity and relocation-speed multipliers,
  Hub-mode +20% capacity boost.
- **Mode-cycling keybind** (default `M`) and per-mode wagon caps.
- Full `MelonPreferences` configuration for every multiplier and radius.
