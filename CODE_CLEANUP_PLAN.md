# Glamour Tracker+ — code cleanup plan

**Branch:** `code-cleanup` (never work on `main` for this)
**Baseline:** 0.1.118, everything working as of 2026-08-05
**Goal:** same behaviour, better code — efficient, readable, one rule per concept, no leftovers from trial and error.

## How to resume this work in a new session

1. `git checkout code-cleanup`
2. Read this file top to bottom, find the first phase not marked done.
3. Read the **Progress log** at the bottom for what actually happened.
4. Work one phase at a time. Build, load in game, tick the checklist, append to the log.

## Ground rules

- **One phase per commit.** Never mix phases; a bad phase must be revertable alone.
- **Build after every phase:** `dotnet build GlamourTrackerNative/GlamourTrackerNative.csproj -c Release` (needs unsandboxed shell for the Dalamud SDK) and also `-c Debug` because Debug compiles the dev-only files that Release excludes.
- **Behaviour is frozen.** If a phase would change what the user sees, stop and ask first.
- **Version bump** one patch per shipped phase, and note it in `PROJECT_NOTES_LOG.txt`.
- **Blast radius:** before changing any shared rule (ownership, storage, completion, week reset), list every consumer and fix them together. The Overview vs Outfit Sets bug came from skipping this.
- **Don't delete dev tooling.** `TrackerWindow.cs`, `FashionReportPanel.cs`, `StorageMarkerDrawer.cs` are `GLAMOUR_DEV`/Debug-only on purpose.

## Do not break (regression watchlist)

Verify these in game after every phase:

- Overview: "Completed in dresser" ≈ 74/264, "Completed in armoire" ≈ 31/84, dresser slots, unique item counts.
- Overview counts survive a logout/login **without** opening the dresser.
- Clear saved data resets to zeros and does **not** instantly refill.
- Outfit sets tab: dresser pieces show, "In dresser" filter returns sets, detail piece rows show correct storage.
- Fashion Report: current week score, no stale "Complete · Score 88" after reset, Masked Rose sync.
- Item tooltips tint dresser/armoire icons; GC expert delivery shows markers.
- Plate editor overlay + randomize still work.

---

# Phase 0 — Safety net and baseline

**Status:** done

- [x] Confirm branch `code-cleanup` is checked out and `main` holds the last known-good build.
- [ ] Record baseline numbers from the watchlist above into the Progress log (needs the user in game).
- [x] Copy the current Release DLL somewhere outside the repo as a rollback artifact — `.cursor/backups/2026-08-05_233257_pre-phase1/baseline-0.1.118.dll`.
- [x] Confirm `dotnet build` succeeds in both Debug and Release before touching anything.

---

# Phase 1 — Correctness bugs (do these before any restructuring)

**Status:** code done in 0.1.119, awaiting in-game verification
**Why first:** restructuring on top of broken rules bakes the bugs in.

### 1.1 Ownership save/read guards
- [x] `GlamourOwnershipIndex.cs` `SavePersistedForCharacter` guards empty dresser and empty complete-set lists, but **not** empty `DresserSetRowIds` — a partial read can wipe set presence on disk. Add the same guard.
- [x] `Refresh()` bails entirely when neither dresser nor armoire read succeeds, so set/complete rebuilds never run off persisted data. Let the cache-only rebuild still happen.
- [x] `ReadArmoire` returns `false` when the cabinet is loaded but empty, so an emptied armoire stays stale forever. Return `true` when `IsCabinetLoaded()` and let the empty set replace.
- [x] Mirage complete scan **replaces** the whole complete list when it finds anything; a partial pass can drop valid completes. Only sets the scan actually saw in the Prism Box may lose completeness; unscanned sets keep their saved result.

### 1.2 Fashion Report week boundaries
- [x] All `FashionReportWeek` helpers now take `utcNow`; no hidden clock reads.
- [x] `NextJudgingResetUtc` renamed to `ScoreExpiryUtc` with a comment explaining why it is not simply "the next Friday". **Correction to the original plan:** the Friday-based expiry is correct — a score has to survive the closed Tuesday–Friday gap — so the semantics were kept, only the name and docs changed.
- [x] Judging window boundary is now inclusive at exactly Friday 08:00 UTC.
- [x] The 10-minute snapshot cache also expires at the weekly reset, so Tuesday rollover can't show last week's hints.
- [x] Anchor initialisation now persists (it only lived in memory before).
- [x] `Math.Max` on the score: verified safe — `EnsureWeekReset()` already runs before the NPC scenes are applied. No change.

### 1.3 Threading and lifetime
- [x] `FashionReportProgressTracker` calls `Save()` from a native hook thread. Config saves now go through the framework thread.
- [x] `TrackerNativeAddon` async acquire loads use `CancellationToken.None`. Added a window-scoped token, cancelled in `OnFinalize` and threaded through the acquire loads, the category scan, and `ResolveNamedItemAsync`.
- [x] A failed acquire load is marked "loaded", so it never retries. Failures now record a 5-minute cooldown, show "Couldn't load sources", and retry after that.

### 1.4 ATK safety
- [x] GC markers are no longer built for a hidden supply addon (`ShouldDrawAnyMarkers` now checks `IsVisible`).
- [x] GC marker rebuild key ignores ownership changes. Added `GlamourOwnershipIndex.Revision` and included it in the key.
- [x] `ItemDetailEnhancer` early-exits without restoring the tooltip. Restores on every exit path, and `RestoreTooltip` is null-safe.
- [x] `ItemDetailEnhancer` has no `PreFinalize` listener; tints survive teardown. Registered one.
- [x] `isEnhancing`: verified this is re-entrancy protection (tinting re-triggers the update), not a dropped-event bug. Kept, comment added.

### 1.5 Stale-data UI rules
- [x] Added `OutfitSetInfo.InDresser` as the single dresser-presence rule; Overview counting and the "In dresser" filter both read it instead of computing their own answer.
- [x] Category filter hid unscanned sets with a "no matches" message. It now says sources are still being checked while the scan runs.
- [x] `FashionVendorLocator` compares place names on letters and digits only, so `Ul'dah` vs `Ul’dah` matches.

**Verify:** full watchlist, plus deliberately empty the armoire and confirm the count drops after opening it.

---

# Phase 2 — One ownership model

**Status:** not started
**Why:** four parallel caches with different merge rules is the root cause of most past bugs.

Target shape:

- `OwnershipSnapshot` — one per character: item locations, set presence, set completion, slots used, `Version`, last source.
- `OwnershipRefresher` — ordered adapters (ItemFinder, MirageManager, PrismBox agent, Cabinet), each declaring its authority and whether it merges or replaces.
- `SetCompletionRules` — the single definition of "complete": all glam slots unlocked via Mirage, or all glam base ids in the dresser.
- `ItemStorageResolver.GetStorage(itemId, setContext?)` — the single answer for tooltips, plates, outfit pieces, GC markers.

Work:

- [ ] Introduce `OwnershipSnapshot` and move the four `cached*` sets behind it.
- [ ] Extract the adapters out of `Refresh()` so the pipeline reads as a list of steps with declared semantics.
- [ ] Extract `SetCompletionRules` and delete the duplicate completion logic in `GlamourOwnershipIndex.RebuildCompleteSetsFromOwnedPieces`, `OutfitSetCatalog.IsDresserSetComplete`, and `IsDresserSetCompleteForOverview`.
- [ ] Extract `ItemStorageResolver` and route `OutfitSetCatalog.ResolvePieceStorage`, plate store, tooltips and GC markers through it (this is what 0.1.118 patched in one place only).
- [ ] Replace `Invalidate()`-everything with a snapshot `Version` the UI can compare.
- [ ] Add the empty-overwrite guard once, for every persisted list.
- [ ] Fix the contradictory doc comments on `DresserSetRowIds` (says "Mirage slot unlocked", actually "set row present in dresser item list") — rename to `DresserSetPresenceRowIds`.

**Blast radius to check:** Overview stats, Outfit sets list + filters + detail, item tooltips, GC delivery markers, plate randomizer, Fashion Report owned flags.

---

# Phase 3 — Outfit set catalog and shared domain helpers

**Status:** not started

- [ ] Split `OutfitSetCatalog` into static metadata (built once: name, pieces, eligibility) and dynamic flags read from the ownership snapshot. Today the full `MirageStoreSetItem` sheet is rescanned and re-sorted on every invalidate.
- [ ] Introduce a single `SetDresserState` (None / Partial / Complete) and use it for both Overview counting and the Outfit sets tab, so they can never disagree again.
- [ ] Memoize "is this a glamour piece" by item id and hold the `Item` sheet once — `IsGlamourPiece` currently calls `GetExcelSheet<Item>()` per piece, per set, per rebuild.
- [ ] Precompute overview stats during refresh instead of scanning all sets per frame.
- [ ] Merge the three slot maps (`GlamourOwnershipIndex` set slots, `OutfitSetCatalog` slot readers, `GlamourPlateSlotMap`) into one file, documenting why sets have 11 slots and plates have 12.
- [ ] Merge the duplicate unlock-bit readers (`IsFinderSetUnlockBitSet` and `OutfitSetCatalog.IsOutfitSetUnlocked`).
- [ ] Collapse `ItemIdHelper.GlamourBaseId` and `Normalize` (identical) into one name.
- [ ] Delete `GlamourPlateCatalog.cs` (unused; `GlamourPlateStore` does the job) or make the store delegate to it — not both.
- [ ] Index set row id → Prism Box index once per Mirage load instead of scanning `PrismBoxItemIds` per lookup.

---

# Phase 4 — Split the two giant UI files

**Status:** not started

### 4.1 `TrackerNativeAddon.cs` (1470 lines) → partials
- [ ] `TrackerNativeAddon.cs` — shell only: fields, `OnSetup`/`OnShow`/`OnUpdate`/`OnFinalize`, tab routing, layout, rebuild scheduling.
- [ ] `TrackerNativeAddon.Overview.cs`
- [ ] `TrackerNativeAddon.Settings.cs`
- [ ] `TrackerNativeAddon.OutfitBrowser.cs` — toolbar, filters, row building, selection.
- [ ] `TrackerNativeAddon.OutfitDetail.cs` — detail pane, piece rows, acquire sections, try-on.
- [ ] `TrackerNativeAddon.AcquireLoading.cs` — caches, async loads, background category scan.
- [ ] `TrackerNativeNodeFactory.cs` — static node builders (`MakeText`, `MakeMuted`, `MakeSection`, `MakeCheckbox`, indenting, truncation).
- [ ] Delete unused `MakeStatLine`.

### 4.2 `FashionReportNativeAddon.cs` (1055 lines)
- [ ] Split along the same lines: shell, chrome/progress header, hint list, item detail, node factory.

### 4.3 `GcExpertDeliveryEnhancer.cs` (818 lines)
- [ ] `GcSupplyAddonLifecycle` — listeners, marker disposal, cache reset.
- [ ] `GcExpertDeliveryMarkerSync` — marker build/attach and rebuild caching.
- [ ] `GcExpertDeliveryRowMatcher` — row lookup, label/icon resolution.
- [ ] `GcExpertDeliveryAgentAccess` — agent reads, expert tab detection.
- [ ] `GcExpertDeliveryDevTools` — everything under `GLAMOUR_DEV`.
- [ ] Drop the unused `cabinetCatalog` constructor dependency.

### 4.4 `FashionReportService.cs` (739 lines)
- [ ] `FashionReportCoordinator` — public API, snapshot/error state.
- [ ] `FashionReportRefreshPipeline` — refresh, cancellation, HTTP fan-out.
- [ ] `FashionReportItemResolver` — resolve, rebind, rank, craft enrichment.
- [ ] `FashionReportItemNameIndex` — name/icon/dye lookup index.
- [ ] `FashionReportOwnershipSync` — inventory events, debounce, rebuild.
- [ ] `FashionReportSnapshotFactory` — dye views, easy outfit, hint assembly.

Splits are mechanical: move code, do not rewrite behaviour in the same commit.

---

# Phase 5 — Stop doing work every frame

**Status:** not started

- [ ] `TrackerNativeAddon.OnUpdate` builds a signature string every frame; on Overview that calls `GetOverviewStats()` (full set scan + LINQ allocations), and on Outfit sets it runs the whole filter/sort pipeline plus a `string.Join` over all rows. Gate both behind a cheap dirty check (ownership version + filter values + counts).
- [ ] Cache the filtered/sorted outfit row list until inputs change; build it in a single pass instead of layered LINQ.
- [ ] `SplitPiecesForFilter` allocates two lists per call and is called twice per set. Reuse scratch buffers or return a struct enumerator.
- [ ] `FashionReportNativeAddon.OnUpdate` calls `GetProgress()` + MGP buff view every frame (inventory and status scans). Throttle to ~1s or drive from events.
- [ ] `GcExpertDeliveryEnhancer` does marker sync from the draw path and computes atlas paths/slices before checking whether a rebuild is needed. Move to framework update + dirty flag, compute the signature first.
- [ ] `PlateEditorOverlay` opens up to 12 ImGui windows per frame and calls `GetAddonByName` three times per frame for visibility. Use one overlay window and cache visibility off lifecycle events.
- [ ] Replace full-sheet scans with prebuilt indexes: dye names (`FashionReportNativeAddon.ResolveDyeIcon`), territories (`FashionVendorTravel`), duties (`OutfitDutyTravel`), item icons (Fashion Report refresh).
- [ ] `FashionInventoryIndex.Scan()` runs fully on every refresh and every debounced rebind — make it incremental or TTL-cached.
- [ ] `PlateSlotNodeLocator` does an O(n) `Contains` inside a 12-slot loop; use a lookup table.
- [ ] `GlamourPlateStore` saves unconditionally; compare before writing.
- [ ] `Configuration.Save()` has no coalescing — add a dirty flag with a short debounce.
- [ ] Drop the duplicate `RebindOwnership()` call in `Plugin.RefreshAll`.

---

# Phase 6 — Config and persistence cleanup (schema v13)

**Status:** not started

- [ ] Move the migration chain out of `Plugin.cs` into `Configuration.Migrate()`; rename `MigrateIconSliceConfig` (it migrates far more than icon slices).
- [ ] Gate the v11 icon-path repair on `Version < 11` so it can't force a save on every startup.
- [ ] Document or no-op the skipped version 7.
- [ ] Drop dev-only tuning fields from the Release schema: icon UV/offset/display-scale fields, slot reroll placement floats. Keep behaviour by moving the values to constants.
- [ ] Remove `FashionReportFromDailyDuty` (written, never read) and the migration that clears it.
- [ ] Rename `CharacterGlamourCache` — it holds Fashion Report state and outfit sets too, not just glamour. Consider splitting per concern.
- [ ] Persist id lists as sorted arrays/sets and validate on load.
- [ ] Add pruning or a "forget this character" action for `CharacterCaches`, which currently grows forever per alt.
- [ ] Clarify `UseLocalUiStyle`/`LocalUiTheme` — they only affect the ImGui plate overlay, not the native UI. Rename or scope them.
- [ ] Align `GlamourStorageLocation` (`[Flags]`) and `OutfitSetStorageLocation` (plain enum with `Both = 3`), or document why they differ.

---

# Phase 7 — Dead code, duplication, obsolete APIs

**Status:** not started

- [ ] Exclude `EmptyGearSlotAtlas.cs` from Release — ~150 dictionary entries ship with zero Release call sites.
- [ ] Delete `AtkUiHelper.GetItemIconDrawNode` (private, never called) and `StorageMarkerDrawer.StoredGreen`.
- [ ] Delete `GetLongestRowText`, which duplicates `AtkUiHelper.FindPrimaryRowLabelTextNode`.
- [ ] `PlateSlotNodeLocator.ClearCache()` / `InvalidateLock()` are no-ops with live call sites — either restore real caching or remove both, plus the unused `IPluginLog` parameter.
- [ ] Replace the `GcMarkerCalibrateX = 550f` magic constant with a measured offset.
- [ ] Migrate the two obsolete `RowTemplateNodeCount` uses (CS0618) to the current ClientStructs API — the only warnings in the build.
- [ ] Unify HR/SD texture path resolution (duplicated in `StorageIconAtlasDefaults` and `EmptyGearSlotAtlas`) into one resolver.
- [ ] Name the atlas UV magic numbers and document that they are SD-space.
- [ ] Move the dev-only tooltip capture path out of `StorageUiIconCache` into a dev partial.
- [ ] Remove the never-supplied `onTooltipIconsApplied` callback from `ItemDetailEnhancer`.
- [ ] Delete the empty `FashionReportPanel.RequestOpen()` stub.
- [ ] Deduplicate formatting helpers shared by native and ImGui UIs (`FormatStorage`, progress/tag colors) into the existing `*Helpers` classes.
- [ ] Derive the Fashion Report client User-Agent from the assembly version instead of the hardcoded `0.6`.
- [ ] Pick one of `GetLocalContentId()` / `GetLocalContentIdStatic()`.

---

# Phase 8 — Naming and readability pass

**Status:** not started

- [ ] Rename members whose names no longer match behaviour (see Phase 6/7 items, plus `OpenFashionReportTab` which opens a window, not a tab).
- [ ] Comment policy sweep: keep comments that explain a constraint or a hard-won gotcha (Prism Box index vs set RowId, add-only vs replace, SD-space UVs). Delete comments that narrate the code.
- [ ] Plain-English UI text: "Judging" → "This week's score"; fix "Try on previews it."; "Open: {duty}" → "Travel to {duty}"; drop the redundant "Sort:" prefix inside the sort dropdown; "No pieces for this filter" → "No pieces match this storage filter."
- [ ] Consistent terminology everywhere: dresser / armoire / outfit set / piece / stored / complete.
- [ ] Consider renaming the assembly and folder from `GlamourTrackerNative` to match "Glamour Tracker+" — **breaking**: changes the Dalamud InternalName and config file name, needs a migration and a new dev-plugin path. Decide explicitly; skip if not worth it.

---

# Phase 9 — Wrap up

**Status:** not started

- [ ] Full watchlist verification on a fresh login and on a character switch.
- [ ] Debug and Release builds clean, zero warnings.
- [ ] `PROJECT_NOTES_LOG.txt` updated with the outcome of each phase.
- [ ] README updated if any user-visible wording changed.
- [ ] Merge `code-cleanup` into `main` only after a full in-game session with no regressions.

---

# Complete file checklist

Every source file must be reviewed, even if the outcome is "no change needed". Mark each one.

## Root (6 files + project files)

| File | Lines | Phase | Status |
|---|---|---|---|
| `Plugin.cs` | 486 | 1, 5, 6 | ☐ |
| `Configuration.cs` | 146 | 6 | ☐ |
| `CharacterGlamourCache.cs` | 34 | 2, 6 | ☐ |
| `StoredGlamourPlate.cs` | 15 | 6 | ☐ |
| `GlamourStorageLocation.cs` | 11 | 6 | ☐ |
| `OutfitSetStorageLocation.cs` | 9 | 3, 6 | ☐ |
| `GlamourTrackerNative.csproj` | — | 7 | ☐ |
| `GlamourTrackerNative.json` | — | 8 | ☐ |

## Services (28 files)

| File | Lines | Phase | Status |
|---|---|---|---|
| `GcExpertDeliveryEnhancer.cs` | 818 | 1, 4, 5, 7 | ☐ |
| `GlamourOwnershipIndex.cs` | 759 | 1, 2 | ☐ |
| `PluginLocalUiTheme.cs` | 609 | 6, 8 | ☐ |
| `PlateEditorOverlay.cs` | 546 | 5 | ☐ |
| `GlamourPlateRandomizer.cs` | 431 | 3, 5 | ☐ |
| `AtkUiHelper.cs` | 380 | 7 | ☐ |
| `StorageUiIconCache.cs` | 362 | 5, 7 | ☐ |
| `OutfitSetCatalog.cs` | 303 | 2, 3 | ☐ |
| `EmptyGearSlotAtlas.cs` | 260 | 7 | ☐ |
| `GlamourCandidatePool.cs` | 222 | 3 | ☐ |
| `GcMarkerOverlayGuard.cs` | 196 | 5 | ☐ |
| `ItemDetailEnhancer.cs` | 163 | 1, 7 | ☐ |
| `PlateSlotNodeLocator.cs` | 153 | 5, 7 | ☐ |
| `GlamourPlateStore.cs` | 149 | 3, 5 | ☐ |
| `ItemEquipFilter.cs` | 139 | 3 | ☐ |
| `PluginCommands.cs` | 94 | 8 | ☐ |
| `ClassJobFilterList.cs` | 93 | 3 | ☐ |
| `StorageIconAtlasDefaults.cs` | 90 | 7 | ☐ |
| `GlamourPlateSlotMap.cs` | 88 | 3 | ☐ |
| `ExpertDeliveryMatchIndex.cs` | 79 | 4 | ☐ |
| `StorageMarkerDrawer.cs` | 67 | 7 | ☐ |
| `OutfitDutyTravel.cs` | 63 | 5 | ☐ |
| `PluginFileLog.cs` | 59 | 1 | ☐ |
| `GlamourPlateCatalog.cs` | 40 | 3 (delete) | ☐ |
| `CabinetCatalog.cs` | 38 | 2, 3 | ☐ |
| `ItemIdHelper.cs` | 28 | 3 | ☐ |
| `StorageUiIconSlice.cs` | 21 | 7 | ☐ |
| `GlamourCandidate.cs` | 11 | 3 | ☐ |

## Services/FashionReport (12 files)

| File | Lines | Phase | Status |
|---|---|---|---|
| `FashionReportService.cs` | 739 | 1, 4, 5 | ☐ |
| `FashionVendorTravel.cs` | 453 | 5 | ☐ |
| `FashionAcquisitionParser.cs` | 383 | 1, 8 | ☐ |
| `FashionMgpBuffService.cs` | 261 | 5 | ☐ |
| `FashionReportModels.cs` | 241 | 6, 8 | ☐ |
| `FashionVendorLocator.cs` | 240 | 1 | ☐ |
| `FashionReportProgressTracker.cs` | 212 | 1 | ☐ |
| `FashionInventoryIndex.cs` | 172 | 5 | ☐ |
| `ArtisanIpcClient.cs` | 124 | 7 | ☐ |
| `FashionReportClient.cs` | 92 | 7 | ☐ |
| `FashionRecipeLookup.cs` | 49 | 5 | ☐ |
| `FashionReportWeek.cs` | 30 | 1 | ☐ |

## Windows (5 files)

| File | Lines | Phase | Status |
|---|---|---|---|
| `TrackerNativeAddon.cs` | 1470 | 1, 4, 5, 8 | ☐ |
| `FashionReportNativeAddon.cs` | 1055 | 4, 5 | ☐ |
| `TrackerWindow.cs` (dev only) | 588 | 7, 8 | ☐ |
| `FashionReportPanel.cs` (dev only) | 560 | 7 | ☐ |
| `RandomizeFilterUi.cs` | 152 | 8 | ☐ |

## Windows/Native (6 files)

| File | Lines | Phase | Status |
|---|---|---|---|
| `TrackerNativeHelpers.cs` | 263 | 3, 5, 8 | ☐ |
| `FashionReportNativeHelpers.cs` | 129 | 7 | ☐ |
| `FashionReportNativeItemNode.cs` | 88 | 4 | ☐ |
| `TrackerNativeListItemNode.cs` | 84 | 4 | ☐ |
| `FashionReportNativeRow.cs` | 24 | 4 | ☐ |
| `TrackerNativeListRow.cs` | 16 | 4 | ☐ |

**Total: 57 C# files, ~14,400 lines.**

---

# Progress log

Append one entry per phase. Keep it short and factual.

```text
[YYYY-MM-DD] Phase N — <name>
- Done:
- Skipped (and why):
- In-game result:
- Version:
```

[2026-08-05] Plan created on branch `code-cleanup` from 0.1.118. No code changed yet.

[2026-08-05] Phase 1 — correctness bugs
- Done: all of 1.1–1.5. Ownership guards (set-presence save guard, cache-only rebuild, authoritative
  empty armoire, scan-scoped completeness), Fashion Report week helpers take the clock and the
  snapshot cache expires at reset, config saves off the hook thread, window-scoped cancellation plus
  failed-source retry, ATK restore/visibility guards, ownership revision on GC markers, and one
  shared `InDresser` rule for Overview and the Outfit sets filter.
- Skipped: two items turned out not to be bugs — the Friday-based score expiry (correct as written,
  renamed only) and the `isEnhancing` re-entrancy guard. Both documented above.
- In-game result: verified, except GC delivery showed armoire markers but no dresser markers until the
  dresser was opened. Fixed in 0.1.120 (below); everything else on the watchlist passed.
- Version: 0.1.119

[2026-08-06] Phase 1 follow-up — dresser ownership survives a restart
- Cause: the dresser cache mixed two sources with different meanings. ItemFinder lists ~1493 ids
  (every piece inside a stored outfit); the Prism Box lists 563 physical slots, where a whole outfit
  is one set row. A Prism Box read alone counted as authoritative and pruned the 930 piece ids, and
  that thinned list is what got saved. Predates Phase 1 — the same 1493 → 563 drop is in the log
  under 0.1.117.
- Done: pruning now requires both sources to have been read in the same pass, and "in the dresser"
  additionally answers true for pieces of a set already known to be complete, so tooltips, GC
  markers, plates and the sets tab share one rule. Completeness math kept on the raw item list via
  `IsInDresserItemList` so Overview counts cannot inflate.
- In-game result: pending.
- Version: 0.1.120
