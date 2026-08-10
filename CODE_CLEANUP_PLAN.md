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

- Overview: "Completed in dresser" ≈ 71/264, "Completed in armoire" ≈ 31/84, dresser slots ≈ 564/800, unique dresser ≈ 1230.
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

**Status:** done (0.1.121)
**Why:** four parallel caches with different merge rules is the root cause of most past bugs.

Target shape:

- `OwnershipSnapshot` — one per character: item locations, set presence, set completion, slots used, `Version`, last source.
- `OwnershipRefresher` — ordered adapters (ItemFinder, MirageManager, PrismBox agent, Cabinet), each declaring its authority and whether it merges or replaces.
- `SetCompletionRules` — the single definition of "complete": all glam slots unlocked via Mirage, or all glam base ids in the dresser.
- `ItemStorageResolver.GetStorage(itemId, setContext?)` — the single answer for tooltips, plates, outfit pieces, GC markers.

Work:

- [x] Introduce `OwnershipSnapshot` and move the four `cached*` sets behind it. It owns `Version` and every mutation returns whether anything actually changed.
- [x] Extract the adapters out of `Refresh()` so the pipeline reads as a list of steps with declared semantics — `OwnershipGameReader` holds every unsafe read and returns a `DresserRead` describing how much it saw.
- [x] Extract `SetCompletionRules` and delete the duplicate completion logic in `GlamourOwnershipIndex.RebuildCompleteSetsFromOwnedPieces`, `OutfitSetCatalog.IsDresserSetComplete`, and `IsDresserSetCompleteForOverview`. All three are now `SetCompletionRules.IsComplete`, which takes one flag for whether live dresser slot flags may be consulted.
- [x] Route piece storage, plate store, tooltips and GC markers through one resolver. Done without a separate `ItemStorageResolver` class: the index owns the snapshot, so `GetStorage(itemId)` plus the new `GetStorage(itemId, setRowId, slotIndex)` overload is that resolver and `OutfitSetCatalog.ResolvePieceStorage` is gone.
- [x] Replace `Invalidate()`-everything with a snapshot `Version` the UI can compare — `Revision` now comes straight from `OwnershipSnapshot.Version`, which only moves on a real change.
- [x] Add the empty-overwrite guard once, for every persisted list — `KeepSavedWhenEmpty`.
- [x] Fix the contradictory doc comments on `DresserSetRowIds` — the property is now `DresserSetPresenceRowIds`. The on-disk key stays `DresserSetRowIds` via `[JsonProperty]` so existing configs load untouched; renaming the key itself belongs with the Phase 6 schema bump.

**Pulled forward from Phase 3** because Phase 2 needed them: the shared `OutfitSetSlots` map (was duplicated in the index and the catalog), memoized `IsGlamourPiece`, and the merged unlock-bit reader (`IsFinderSetUnlockBitSet` and `OutfitSetCatalog.IsOutfitSetUnlocked` were the same code twice).

**Also removed:** unused `IsMiragePrismReady` and `OutfitSetsCompleteInDresser`.

**One deliberate behaviour change:** a saved slot count is now restored whenever the runtime count is zero, not only when the empty-dresser save guard happens to fire.

**Blast radius to check:** Overview stats, Outfit sets list + filters + detail, item tooltips, GC delivery markers, plate randomizer, Fashion Report owned flags.

---

# Phase 3 — Outfit set catalog and shared domain helpers

**Status:** done and verified (through 0.1.137 follow-ups)

- [x] Split `OutfitSetCatalog` into static metadata (built once: name, pieces, eligibility) and dynamic flags read from the ownership snapshot. `OutfitSetTemplate` holds the sheet-derived part, so an invalidate no longer rescans and re-sorts `MirageStoreSetItem`.
- [x] Introduce a single `SetDresserState` (None / Partial / Complete) and use it for both Overview counting and the Outfit sets tab. `InDresser` is now derived from it rather than being a second answer to the same question.
- [x] Memoize "is this a glamour piece" by item id and hold the `Item` sheet once. Memoizing came forward into Phase 2; the sheet is now held too, and the catalog asks per template instead of per rebuild.
- [x] Precompute overview stats during refresh instead of scanning all sets per frame. Tallied while the sets are built and returned as a cached struct, which matters because the Overview signature asks for them every frame.
- [x] Merge the three slot maps into one file — `GearSlotMaps.cs`, documenting why sets have 11 slots and plates have 12.
- [x] Merge the duplicate unlock-bit readers (done in Phase 2).
- [x] Collapse `ItemIdHelper.GlamourBaseId` and `Normalize` (identical) into one name.
- [x] Delete `GlamourPlateCatalog.cs`. The two record types it declared were the ones `GlamourPlateStore` returns, so they moved there with it.
- [x] Index set row id → Prism Box index once per Mirage load instead of scanning `PrismBoxItemIds` per lookup. Dropped on every refresh and rebuilt lazily; published by one reference assignment so a UI-thread reader never sees a half-built map.

**Deliberately not done:** gating `OutfitSetCatalog.Invalidate()` on the ownership revision. The rebuild also picks up live Mirage slot flags and finder unlock bits, which can move without the snapshot version moving, and with the slot map cached the rebuild is cheap enough that the staleness risk is not worth it.

**Blast radius to check:** Overview set counts, Outfit sets list + storage filter + detail, the old ImGui `TrackerWindow` (also reads `SetStorage` / `MissingPieces`).

---

# Phase 4 — Split the two giant UI files

**Status:** done (0.1.138), mechanical splits only — smoke-test in game recommended

### 4.1 `TrackerNativeAddon.cs` → partials
- [x] `TrackerNativeAddon.cs` — shell only: fields, lifecycle, tab routing, layout, rebuild scheduling.
- [x] `TrackerNativeAddon.Overview.cs`
- [x] `TrackerNativeAddon.Settings.cs`
- [x] `TrackerNativeAddon.OutfitBrowser.cs`
- [x] `TrackerNativeAddon.OutfitDetail.cs`
- [x] `TrackerNativeAddon.AcquireLoading.cs`
- [x] `TrackerNativeNodeFactory.cs`
- [x] Delete unused `MakeStatLine`.

### 4.2 `FashionReportNativeAddon.cs`
- [x] Split: shell, Chrome, HintList, ItemDetail, `FashionReportNativeNodeFactory`.

### 4.3 `GcExpertDeliveryEnhancer.cs`
- [x] Partial class split: lifecycle shell, MarkerSync, RowMatcher, AgentAccess, DevTools (kept public type name for Plugin).
- [x] Drop the unused `cabinetCatalog` constructor dependency.

### 4.4 `FashionReportService.cs`
- [x] Partial class split: coordinator, Refresh, ItemResolver, NameIndex, OwnershipSync, SnapshotFactory (names kept as `FashionReportService.*` for stable call sites).

Splits are mechanical: move code, do not rewrite behaviour in the same commit.

---

# Phase 5 — Stop doing work every frame

**Status:** done (0.1.139) — smoke-test in game recommended

- [x] `TrackerNativeAddon.OnUpdate` builds a signature string every frame; on Overview that calls `GetOverviewStats()` (full set scan + LINQ allocations), and on Outfit sets it runs the whole filter/sort pipeline plus a `string.Join` over all rows. Gate both behind a cheap dirty check (ownership version + filter values + counts).
- [x] Cache the filtered/sorted outfit row list until inputs change; build it in a single pass instead of layered LINQ.
- [x] `SplitPiecesForFilter` allocates two lists per call and is called twice per set. Reuse scratch buffers or return a struct enumerator.
- [x] `FashionReportNativeAddon.OnUpdate` calls `GetProgress()` + MGP buff view every frame (inventory and status scans). Throttle to ~1s or drive from events.
- [x] `GcExpertDeliveryEnhancer` does marker sync from the draw path and computes atlas paths/slices before checking whether a rebuild is needed. Move to framework update + dirty flag, compute the signature first.
- [x] `PlateEditorOverlay` opens up to 12 ImGui windows per frame and calls `GetAddonByName` three times per frame for visibility. Use one overlay window and cache visibility off lifecycle events.
- [x] Replace full-sheet scans with prebuilt indexes: dye names (`FashionReportNativeAddon.ResolveDyeIcon`), territories (`FashionVendorTravel`), duties (`OutfitDutyTravel`), item icons (Fashion Report refresh).
- [x] `FashionInventoryIndex.Scan()` runs fully on every refresh and every debounced rebind — make it incremental or TTL-cached.
- [x] `PlateSlotNodeLocator` does an O(n) `Contains` inside a 12-slot loop; use a lookup table.
- [x] `GlamourPlateStore` saves unconditionally; compare before writing.
- [x] `Configuration.Save()` has no coalescing — add a dirty flag with a short debounce.
- [x] Drop the duplicate `RebindOwnership()` call in `Plugin.RefreshAll`.

**Notes:** Fashion Report item icons already used the one-time name index from Phase 3/4; dye/territory/duty indexes were the remaining full-sheet scans. Plate overlay visibility is cached per ImGui draw (not AddonLifecycle). Inventory scan uses a 2s TTL (force on week refresh). The “one ImGui window for all slot rerolls” item was reverted in 0.1.140 — a single bounding-box window blocked clicks over the character preview and discard dialogs; keep one small window per slot.

---

# Phase 6 — Config and persistence cleanup (schema v13)

**Status:** done (0.1.141) — smoke-test in game recommended (schema migrates to v13 on load)

- [x] Move the migration chain out of `Plugin.cs` into `Configuration.Migrate()`; rename `MigrateIconSliceConfig` (it migrates far more than icon slices).
- [x] Gate the v11 icon-path repair on `Version < 11` so it can't force a save on every startup.
- [x] Document or no-op the skipped version 7.
- [x] Drop dev-only tuning fields from the Release schema: icon UV/offset/display-scale fields, slot reroll placement floats. Keep behaviour by moving the values to constants.
- [x] Remove `FashionReportFromDailyDuty` (written, never read) and the migration that clears it.
- [x] Rename `CharacterGlamourCache` — it holds Fashion Report state and outfit sets too, not just glamour. Consider splitting per concern.
- [x] Persist id lists as sorted arrays/sets and validate on load.
- [x] Add pruning or a "forget this character" action for `CharacterCaches`, which currently grows forever per alt.
- [x] Clarify `UseLocalUiStyle`/`LocalUiTheme` — they only affect the ImGui plate overlay, not the native UI. Rename or scope them.
- [x] Align `GlamourStorageLocation` (`[Flags]`) and `OutfitSetStorageLocation` (plain enum with `Both = 3`), or document why they differ.

**Notes:** Renamed cache type to `CharacterTrackerCache` (JSON shape unchanged). Theme flags keep old JSON names via `[JsonProperty]`. Release uses `SlotRerollDefaults` / `StorageIconAtlasDefaults` for layout and atlas UV; Debug still persists tuning fields. v13 also prunes empty alt caches and normalizes id lists. Split of Fashion Report fields into a separate type deferred — same object, clearer name.

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

# Phase 9 — Masked Rose MGP buff reminder

**Status:** not started

When the player talks to the Masked Rose to turn in Fashion Report, prompt if no MGP bonus is active yet, so they do not burn an allowance without VIP Card / Jackpot III.

- [ ] Hook the Masked Rose Fashion Report turn-in path (same scene / event stream `FashionReportProgressTracker` already watches) early enough to intercept before the turn-in consumes an allowance.
- [ ] If `FashionMgpBuffService` reports neither VIP Card nor Jackpot III active, show a Yes/No dialogue: remind that no MGP buff is applied, ask whether to continue with Fashion Report anyway.
- [ ] Yes → allow the normal turn-in to proceed. No → cancel / close without spending the allowance.
- [ ] Skip the prompt when a buff is already active (or when judging is closed / no allowances — no false alarms).
- [ ] Setting to disable the reminder (default on), stored in config.
- [ ] Plain UI copy (see Phase 8 wording rules): short, player-facing, no jargon like "status ID".
- [ ] Log the decision at INFO (`fashion.mgp` or `fashion.progress`) without spamming every talk.
- [ ] Blast radius: progress tracker hooks, MGP buff view, Fashion Report native UI, any existing Masked Rose chat tips.

**Behaviour change** (user-requested): this phase intentionally adds a confirmation, unlike earlier cleanup phases.

---

# Phase 10 — Wrap up

**Status:** not started

- [ ] Full watchlist verification on a fresh login and on a character switch.
- [ ] Debug and Release builds clean, zero warnings.
- [ ] `PROJECT_NOTES_LOG.txt` updated with the outcome of each phase.
- [ ] README updated if any user-visible wording changed.
- [ ] Merge `code-cleanup` into `main` only after a full in-game session with no regressions.
- [ ] Confirm Phase 9 Masked Rose prompt in game (buff on / buff off / dismiss / continue).

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
- In-game result: confirmed by the user — GC delivery loads both dresser and armoire markers after a
  restart now.
- Version: 0.1.120

[2026-08-06] Phase 2 — one ownership model
- Done: every checklist item. `OwnershipSnapshot` holds the state and the version, `OwnershipGameReader`
  holds every unsafe read and says how much it saw, `SetCompletionRules` is the only definition of a
  finished outfit, and `GlamourOwnershipIndex` is now orchestration plus queries. Three copies of the
  completion rule, two copies of the slot map, and two copies of the unlock-bit reader are gone.
- Skipped: a separate `ItemStorageResolver` class. The index owns the snapshot, so splitting the
  resolver out would only add a hop; the duplicated callers were removed instead.
- In-game result: found three bugs, all pre-existing and all the same mistake — trusting a narrow read
  as the whole truth. Fixed in 0.1.122 (below).
- Version: 0.1.121

[2026-08-06] Phase 2 follow-up — pieces inside stored outfits
- Cause: an outfit can be stored partially, and the dresser item list keeps one row for the whole
  outfit, so its pieces appear nowhere. Tooltips and delivery markers have no set context, so they
  could only infer ownership from complete outfits and silently missed every piece of a partial one.
  Two related faults from the same log: a single partial read pruned the dresser from 1493 ids to 563
  and the armoire from 470 to 124, and the Mirage scan and the all-pieces fallback overruled each
  other every 30 seconds, rewriting the config each time.
- Done: the snapshot now tracks pieces held inside stored outfits, read from per-slot unlock flags and
  persisted, so every consumer sees them without needing set context. Removal requires two consecutive
  reads to agree, and the first read of a session may not remove anything. The completeness fallback
  leaves outfits the scan already ruled on alone.
- In-game result: pending at the time; later verified across 0.1.122–0.1.137.
- Version: 0.1.122

[2026-08-08 … 2026-08-10] Phase 3 + ownership follow-ups (0.1.127–0.1.137)
- Phase 3 catalog split landed in 0.1.127; follow-ups fixed prune gating, unique counts, Clear
  behaviour, 71/276 → 71/264 Overview tally, Overview flicker, and removed "Refresh now".
- Watchlist (2026-08-10): logs clean for the session — no ERROR/WARN since 2026-08-08. Ownership
  holds dresser=1494 / sets=264 / completeSets=71 / armoire=470. Masked Rose progress sync OK.
- Plan change: inserted Phase 9 (Masked Rose MGP buff reminder); former Wrap up is now Phase 10.

[2026-08-10] Phase 4 — split giant UI / service files
- Done: TrackerNativeAddon → 6 partials + node factory; FashionReportNativeAddon → 4 partials +
  node factory; GcExpertDeliveryEnhancer → 5 partials (cabinetCatalog ctor arg removed);
  FashionReportService → 6 partials. Behaviour unchanged by design.
- Version: 0.1.138
- In-game: smoke-test Overview / Outfit sets / Fashion Report / GC delivery after reload.

[2026-08-10] Phase 5 — stop doing work every frame
- Done: Overview/browser dirty gates + cached outfit rows (single-pass build, scratch SplitPieces);
  Fashion Report chrome throttled ~1s; GC markers on Framework.Update with cheap dirty before atlas;
  one plate reroll ImGui window + per-draw addon visibility cache; dye/territory/duty indexes;
  inventory Scan TTL; plate store compare-before-save; config Save debounce; drop duplicate
  RebindOwnership in RefreshAll.
- Version: 0.1.139
- In-game: OK except plate overlay click-through (fixed in 0.1.140); then confirmed good.

[2026-08-10] Phase 6 — config / persistence (schema v13)
- Done: `Configuration.Migrate()`; v11 path repair gated; v7 documented; Release drops UV/slot
  tuning fields (constants); remove DailyDuty field; `CharacterTrackerCache`; sorted id lists;
  prune empty caches + Settings “Forget this character”; theme rename with JSON aliases; storage
  enum docs.
- Version: 0.1.141
- In-game: reload once (v12→v13 migrate), Overview counts, GC markers, plate overlay theme,
  Forget this character vs Clear saved data.
