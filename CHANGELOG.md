# Manifest Delivery — Changelog

All notable changes to this mod, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely.

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
