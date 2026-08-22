# Storage Priorities in Manifest Delivery — design + build plan

**Created:** 2026-08-21 (review session)
**Status:** DESIGN ONLY — no code written. Target: MD **v1.1.0** (new feature; current 1.0.20).
**Origin:** Review of the community mod *Storage Priorities* (3am, v1.3.2) for a possible fold.

---

## READ THIS FIRST — how to build this

The review that produced this doc involved decompiling someone else's working,
maintained mod. **Do not reproduce his implementation.** Build MD's own feature
from MD's own architecture:

- **Fair game:** vanilla FF API facts (verified below from the decompile), MD's
  existing `TaskSearchEntry` / `SingleItemRequest` / per-save persistence
  patterns, and the general genre concept (storage priority exists in RimWorld,
  Factorio, Satisfactory — it is not his invention).
- **Not fair game:** his tier model, his badge/popup UI design, his rebalancer's
  structure, his config surface, or working with his decompiled source open.
  The decompile lives at `ClaudeCodeLocalSessions/StoragePriorities_Decompiled/`
  if a collision check is ever needed — it should not be needed to build this.
- **Consider crediting him** in the release notes ("inspired by Storage
  Priorities by 3am"). His mod is why this is on the roadmap, and the FF modding
  scene is small.
- **Also consider NOT building it.** His mod works, has zero collisions with our
  fleet (verified), and players can simply run both. Only build this if MD is
  meaningfully better as the owner of the whole hauling story.

---

## Verified game API (from decompile — safe to rely on)

- **`GetBaseScore(IQueryContainer container)`** — public, returns the float the
  logistics query uses to rank candidate storages. A postfix that adds to
  `__result` biases routing. That is the entire routing mechanism; no
  transpiler needed. **Verified end-to-end (M0):** it runs on the *main thread*
  while building logistics job data (L124252 sources / L124320 destinations),
  the value is copied into the Burst job struct (L122925), and the scorer ranks
  `baseScore - distance + modifiers` (L123115).

  > **CORRECTION (2026-08-21, during M0 — the original note was wrong):**
  > It is **`Resource.GetBaseScore`** (L165149), *not* `StorageBuilding`'s.
  > `StorageBuilding : Building : Resource` and **no storage type overrides it**.
  > Consequences:
  > 1. The postfix fires for **every `Resource` in the game**, not just
  >    storages — an `is StorageBuilding` guard is load-bearing, and the perf
  >    bar is stricter than first assumed.
  > 2. **Do not** patch `GetBaseScoreModifications()` instead. It looks like the
  >    natural "add your bonus" hook, but it is `protected virtual` and **is**
  >    overridden by Granary / Root Cellar / Treasury (their built-in +100), so
  >    patching the base would silently never fire for exactly those storages.

- **Score calibration (verified):** vanilla base score is `(1 - fullness) * 100`
  (emptier ranks higher) and distance subtracts ~1 point per world unit, so a
  bias of *N* ≈ *N* world-units of extra travel tolerated. Granary, Root Cellar
  and Treasury each add a flat **+100**, so a bias must exceed that to out-rank
  them.

- **Bucket discrimination (verified):** `WorkBucket` implements `IQueryContainer`
  and exposes `bucketIdentifier`. Names are prefixed `CanStore*` (destination
  lists, 202 values) vs `HasItem*` (source lists, 174), plus parallel
  `CanStore*OverCapacity` overflow buckets. This is what makes the
  anti-ping-pong contract below implementable.
- **`StorageBuilding.GetAvailableStorageSpace(ref uint)`** — public; for
  "does the better target actually have room."
- **`SingleItemRequest`** — MD already constructs/inspects these in
  `Tasks/ReturnTripSearchEntry.cs:432`. This is the mechanism for actively
  moving existing stock, if we ever want the rebalancing half.

**Collision check (verified 2026-08-21, 12-mod load, zero errors):**
- Nothing in our fleet patches `GetBaseScore` — not KC, MD, EP, SB, TW, WotW, RR.
- Storage Priorities also postfixes `GetBaseScore`. Two postfixes on one method
  **stack additively** — if a player runs both and sets priorities in both, the
  biases sum. Not a crash, but the routing becomes the sum of two opinions.
  Add a soft-dep check: if `Storage Priorities` is loaded, log a warning and
  default our feature OFF.

---

## Anti-ping-pong contract — NON-NEGOTIABLE

A naive score bias **will** shuffle goods between storages forever. These three
rules make that structurally impossible rather than merely unlikely, and are
implemented in `Patches/StoragePriorityPatches.cs` (M0). **Every later milestone
must preserve all three.**

1. **DESTINATION ONLY.** Bias `CanStore*` buckets; never `HasItem*`.
   A Preferred storage boosted on *both* sides would attract goods *and* be the
   preferred place to drain — A pulls from B, B pulls back from A, forever.
   Biasing only the destination side means goods flow toward Preferred and
   **stop there**: the cycle has no return edge, so it cannot close.
2. **TAPER WITH FULLNESS.** Scale the bias by the fraction of space still free.
   A flat bias overrides vanilla's `(1-fullness)*100` and overfills the target,
   which trips its max-quota shed (`StorageQuotaHandler` emits a TakeOut above
   `max*1.1`) — pushing goods out that the bias then pulls back in. That is a
   real oscillator. Tapered, preference never beats physics.
3. **NEVER BIAS `CanStore*OverCapacity`.** That is vanilla's already-full
   fallback; cramming more in is the same overfill→shed→refill loop as (2).

Net: the bias can only **redirect haul work vanilla already decided to do**,
toward a destination that has room. It never *creates* haul work, and never
makes a storage attractive to empty. Actively relocating already-stored stock is
the M4 rebalancer — a different risk class, deliberately deferred.

---

## Why MD is the right home

MD already owns hauling. `Tasks/CampHaulSearchEntry.cs` explicitly documents:

> *"Storage buildings are skipped — camp wagons pick up from PRODUCTION
> buildings, not shuffle between storages."*

This feature fills that exact gap from the other end:

| Leg | Owner today |
|---|---|
| production → storage | MD wagons (Camp/Hub haul) |
| storage → *better* storage | nobody |
| which storage wins a tie | vanilla distance only |

Priority routing completes MD's story instead of bolting a logistics concern
onto a UI mod. KC is the wrong home — it does not own logistics.

---

## Design (MD's own)

### Data model
- Priority is **per (storage building, item)** — a per-item tier is what makes it
  useful ("grain goes to the granary near the bakery, not the far one").
- Keep the ladder short. Suggest **3 tiers + unset** (Preferred / Normal / Last
  Resort / Unset) rather than a 1–5 numeric scale: fewer decisions, and it maps
  to language instead of numbers — consistent with the Camp/Hub/Standard
  vocabulary MD already puts in the building window.
- Score contribution: `bias = tierWeight * strength`, added in the `GetBaseScore`
  postfix. One tunable `strength` pref (KC slider): high = priority always wins,
  low = priority breaks ties and distance still matters.

### Persistence — per save, keyed stably
**Do not** use a single global file, and **do not** key on world position. Both
are traps: a global file leaks settings between towns, and a position key
silently loses settings when a building is relocated — and MD/TW both relocate.

Use MD's existing pattern verbatim (`Components/WagonShopEnhancement.cs`):
- `GetActiveSaveName()` + `LatchSaveName()` — guards the transient-empty
  `SaveManager.activeSaveFileName` window (see `Patches/SaveNameLatchPatches.cs`)
- `GetSaveFilePath(saveName)` → `UserData/<dir>/<sanitized-save>.txt`
- `EnsureLoadedForCurrentSave()`, including the mid-session save-switch reload

For the building key, prefer the building's **save GUID / instance identity**
(what MD's mode persistence already keys on) over coordinates.

### UI
Reuse MD's proven injection path: `Patches/ModeButtonPatches.cs` postfixes the
building info window's `SetTargetData` and gets `__instance` directly — no
hunting for pooled UI objects. Do the same on the storage window.

Keep it small: one per-item tier control in the storage item list, styled to
match MD's existing mode buttons rather than inventing new chrome.

### Rebalancing (PHASE 2 — defer)
Ship routing first. Actively moving already-stored stock into higher-priority
buildings is a different risk class: it creates haul work the player never asked
for and can starve real tasks if unthrottled.

If built, it must be:
- **Opt-in, default OFF.**
- Hard-capped: max N active move orders town-wide, a scan interval, and a
  per-item batch cap. Background reshuffling must never dominate hauling.
- Self-disabling on first exception, with a single log line.
- Built on MD's `TaskSearchEntry` injection (its established pattern) rather
  than pushing requests into quota handlers directly.

---

## Build plan

- ~~**M0 — Routing spike.**~~ **DONE** (`Patches/StoragePriorityPatches.cs`).
  Postfix verified to reach the Burst scorer; anti-ping-pong contract added.
  Superseded by M1 — the name-substring test pref is gone.
- **M1 — Data + persistence.** **BUILT, NOT YET PLAY-TESTED.**
  `Components/StoragePriorityData.cs` (tier enum + per-building component),
  `Systems/StoragePriorityStore.cs` (per-save file, MD latch pattern).
  Tiers live on a component so relocation is free; the store merges live
  components into the on-disk map at their current position.
  Two persistence traps found and closed during the build:
  1. A rebuild-from-scratch sync would wipe tiers if the player saved during
     the attach/restore window → sync is a **merge**, and un-restored
     components may never write.
  2. `SaveToDisk` before the store had ever read the file would delete the
     player's tiers → it now loads before merging.
  Temporary input until M2: a configurable hotkey (default **K**) cycles the
  selected storage's tier.
- **M2 — UI.** Per-item tier control in the storage window via the
  `SetTargetData` postfix pattern.
- **M3 — Settings + polish.** KC registration (`KeepClarityIntegration.cs`
  pattern), strength slider, feature master-toggle default OFF, Storage
  Priorities soft-dep warning.
- **M4 (optional) — Rebalancer**, under the constraints above.

## Verification
- **Routing:** set a far storehouse to Preferred → watch a hauler walk past a
  nearer one. Set it to Last Resort → watch it get skipped until others fill.
- **PING-PONG (do this every milestone):** with a Preferred storage set, leave a
  town running for 10+ minutes with `HaulDiagnostics` on and confirm no item
  repeatedly moves storage→storage. Specifically watch for a `DELIVER` whose
  *origin* is a storage and whose *destination* is another storage, then the
  reverse move for the same item shortly after. Highest-risk setups to test:
  (a) two storages both with a **min quota** for the same item, (b) a Preferred
  storage with a **max quota** it can exceed, (c) a Preferred storage that is
  nearly **full** (Rule 2's taper should quietly stop favouring it).
- **Persistence:** set tiers → save → main menu → load a *different* save (tiers
  must not leak) → reload the first (tiers must return).
- **Relocation:** move a tiered storehouse via TW/MD relocation; tiers must follow.
- **Perf:** `GetBaseScore` is on the logistics hot path. Profile with FFPerfProbe
  (see `reference_ff_perf_probe`) at 500+ pop. The postfix must be O(1) — a
  dictionary lookup, no scans, no allocations. This is the single biggest perf
  risk in the feature; the 2026-07-28 hunt found a 240ms/call vanilla method on
  a comparable path, so measure rather than assume.
- **Interop:** run with Storage Priorities installed; confirm the warning fires
  and our feature stays off.
