# Owner review

Open owner decisions, decisions already made, and queued work. History (what merged, gates, measurements, audits) is in [poc-log.md](poc-log.md); open bugs are in [known-issues.md](known-issues.md), perf backlog in [perf-opportunities.md](perf-opportunities.md), accepted costs in [accepted-costs.md](accepted-costs.md).

## 1. Needs you

- **Pacing-gap milestone (M1-M5, 2026-11-13; details in poc-log):** M1, M3, M4, M5 merged; commit points in cuo-poc-state/measure/quiet-queue-pacing-gap.md. Open: **M2 `prep_reuse` parked** (bg/prep-reuse, 3 rounds): busy scenes never reuse because the ChunkRetry clock needs a full fill every draw; the quiet spot reuses ~98 %. *Default:* leave parked until a retry clock that runs outside the fill exists.
- **FPS test:** the coordinator now runs it unattended as a bench job (`CUO_FPS_TEST_PASSWORD` from the runner's environment) with no agent builds or jobs running, after the T9 work and before the quiet timing pass. Nothing to do unless you want to run it yourself.
- **Bodies, `sequences` = target (bg/bodies-lbr, not merged):** in-game check bench-000621 (contact sheet `cuo-poc-state/measure/bodies-lbr-check/out-621/bodies_gpu.png`, current pack left, test pack right per action). Every action draws a valid frame on both renderers, but with the target sequences daemon (9), ice fiend (43) and wraith (26) show their standing frame for server actions 17, 18 and 20, which the current pack animates; giant spider (28) gains action 13. Is that loss acceptable (it is what the target groups contain), or should `sequences` stay `none` for bodies whose replaced groups have fewer actions? The merge and pack classic-1.5 wait for your answer.
- **Rest of T9: the hot caches** (survey `cuo-poc-state/surveys/t9-remaining-2026-11-11.md`). Proceeding with the defaults below unless you say otherwise:
  1. X/Y/Z (about 940 read sites each): *default:* keep one position cache written only by the funnel, as the last hot field. Moving every reader (render walks included) to the record or to snapshots costs a large slice and risks Legacy `world_prepare`. The alternative is a full move, measured.
  2. Packs without animation tables: *default:* a pack made with the converter's `--no-anims` (a dev/test option) is not supported for play: mobiles draw nothing there, and `MobileView.Draw` goes with the other view fallbacks. The alternative keeps the view path for that case only.
  3. Perf switches (`gpu_prepare_*`, `ui_render_cache`, `text_skip_expired`) and their checkers: *default:* keep them as toggles until the final quiet pass, then delete the ones that are clearly better on.

- **ModernUO closes the connection after a large-scene arrival (2026-11-11):** twice (bench6-000004, bench-000713) the server closed the socket ~2 s after a client's `[go 5156 2150`, with several lanes running. The client read EOF ("Disconnected: Success" -> Connection Reset gump); `Distribution/network-disconnects.log` has no entry (last 11/08), so it is a close without a `Disconnect(reason)`. On arrival the client asks for ~21k property lists (0xD6) on top of ~1 MB of world packets. Client side is not at fault (packet framing proven by tests, `bg/net-framing`). Questions: can you check the server console at such an arrival? *Default meanwhile:* a client slice that spreads the 0xD6 requests over frames (on-screen objects first) behind a switch.
  *Update 2026-11-12:* the burst theory is not confirmed. The old request path already sends at most 15 serials per frame, and the later arrival runs (bench2-000014, bench4-000007, old path and spread budgets 600-2000/s) had no disconnect. Note also that lane locks never held before 2026-11-12 (a script bug), so several lanes could arrive in the large scene at once; that may be what the server reacted to.
- **ModernUO sends an unchanged OPL revision on every mobile delta (found 2026-11-12, `bg/opl-debounce`):** `Mobile.ProcessDelta` sends 0xDC to every other viewer at the end of any delta that sends something (step, hits, stats, healthbar, hair) without checking `sendOPLUpdate` (`Server/Mobiles/Mobile.cs:3058`; the mobile's own client is checked at line 2919). In a large-scene arrival that is ~7-8k same-revision 0xDC per 80 s, all mobiles (up to 18 per serial); items have none. The client now ignores them (no extra 0xD6). Suggested server fix: `if (sendOPLUpdate)` at line 3058. Not changed (ModernUO is read-only for us).
- **Unfocused 10 fps for existing profiles:** the new `ReduceFPSWhenInactive` default (on, 100 ms) only reaches new profiles; existing `profile.json` files store `false`. *Default:* no migration (a one-time profile-version migration would override players who turned it off). Say if you want the migration.

- **Anim atlas LRU, reload of an evicted direction (`bg/cache-lru`):** a visible mobile turning to a direction whose page was evicted reloads it async (it blinks in for a few frames) or sync (no blink, a 3-12 ms main-thread stall per reload). *Default:* async, as for any first sight. Evictions only happen above the budget (256 MiB start, today's sessions use 1-2 pages = 64-128 MiB), so normal play should not see it.
- **Clipping milestone design (`surveys/clip-design-2026-11-12.md`):** root cause of the 1453 1668 feet-through-wall case: a mobile's lower strips are drawn at depth +3 so feet beat the floors ahead, which also beats walls up to two diagonals ahead (one depth per sprite cannot separate a floor and a wall on the same diagonal; inherited from upstream). Recommendation: prototype per-pixel analytic depth by shape class (flat, NE wall, NW wall, corner, box, mobile composite with ground contact); fallback pairwise 3D box sort (breaks Gpu baking). *Default:* build the prototype against the acceptance scenes on both renderers; say if you prefer another candidate.

## 2. Decisions made

- **Pacing-gap decisions (owner, 2026-11-13):**
  - `present_at_display_rate` is default on (merged 4f69c8572; `settings_version` 1 migrates a stored false once; later user choices are kept). Ping becomes the real round trip; the FPS counter shows presented frames, loop rate separately.
  - M5 GPU draws/s drop with both UI switches on: investigated now (bg/m5-gpu-perf, bench-000782, 3 interleaved rounds): did not reproduce (off 2429, both on 3067 draws/s, wide spread on the busy machine; UI recording 47-50 -> 40-41 us median, UI cache hits > 99.6 %). The earlier 1707 -> 1159 was load noise. Quiet pass confirms; switches stay default off.
  - Test shard autosave stays off (`modernuo.json` `autosave.enabled` False).
Architecture and scope
- 2026-10-24: Legacy is a developer-only comparison mode (no automatic fallback). 2026-10-25: keep Legacy until the supported hardware range is decided; Legacy on the ECS is a later project (a6110f967).
- 2026-10-25: renderer chosen at startup by capability (`world_renderer` auto/gpu/legacy), runtime switching only under `CUO_DEVTOOLS` (bbff17751).
- 2026-10-31: T7b approved as the point where Legacy draws dynamics from the shared extract. ECS transition answers 4a-4i followed as recommended: dynamic objects only (statics stay in chunk data), plugins keep the C interface, player stats/skills/buffs/tooltips stay a plain store, switch default-on when checkers are 0 then delete the off path one phase later, Legacy quirks reproduced exactly, `Item`/`Mobile` stay thin handles, effects last.
- 2026-11-01: T5d keeps the tag mirroring the list until T9; effect motion after source/target ownership; mount inputs into the ECS after the default flips.
- 2026-11-02: write our own Friflo read helper (`EcsRead`, docs/dev/ecs-friflo.md); PixelPicker first-pixel bug fixed; contents of an item and mobile sharing one serial accepted; assume no hostile server (comment edge cases, 26b5fabee).
- 2026-11-08: OPL stays a serial-keyed store, not a component (plugin read API possible later). Shared serial in the entity index accepted; `World.Contains`/`Entity.Exists` are "live" (destroyed-unswept does not exist).
- 2026-11-09: accepted: boat `ItemOffset` reads zero after a shared-serial re-creation; double death ends the displaced mobile's effects; Friflo 16-bit revision wrap is a documented bound; gump-handles behaviour changes; player-state items (logout empties the store, opened-corpse sets move to `PlayerState`).
- 2026-11-10: `ecs_item_draw_row` (~0.35 MB at 25k items) goes default on, then `legacy_ecs_sprites`; `legacy_item_draw_cache` dropped (no gain, ~2.5 MB, 2 Codex P2s). Old view Draw bodies deleted after a phase is default on (T9).
- 2026-11-10: .NET 11 is installed (needs restart); upgrade only after T9 and the final quiet pass show no regressions, as its own branch with paired timing.

Pack and assets
- 2026-10-31: data pipeline MUL/UOP (import only) to source format to shipped pack; MUL/UOP never written back.
- 2026-10-31: one source of truth per pack file (`hues.bin` removed, built from `hues_raw.bin`); one tiledata file with ClassicUO's hard-coded draw rules stored as per-tile flags; art and animations ship as pre-packed pages with stable packing + CDC patching (per-graphic records rejected, records-eval.md).
- 2026-10-31: a pack version per format change (classic-1.1 tiledata, 1.2 audio, 1.4 layerrules); versions immutable; `CURRENT` flips once queued jobs with older binaries ran.
- 2026-10-31: cliloc ships uncompressed, block-compressed layout dropped. Client language follows the OS (SDL3 `SDL_GetPreferredLocales`, matched by ISO-639 against the cliloc languages present; profile language wins; none matches gives `ENU`).
- 2026-10-31: map x files not supported (rename before import); legacy-pack tiledata padded to the standard 0x4000/0x10000 entries by the converter (option A).
- 2026-10-31: body set chosen once at import (design A), one pack per shard, admin picks overrides with era presets (AOS and later = all, before AOS = none); `forceOldAnimations` is not OSI-only.
- 2026-10-31: loose `Gumps/*.gump` files need a converter rerun (`--only gumps`) to take effect.
- 2026-11-01: T3 parked items: fold 0x78 equipment into `ReplaceWornItems`, accept +8 B `Mobile.EquipCache`; layering rules cached per mobile; layerrules.bin in the pack with optional JSON input/export.

Process and platform
- 2026-10-30: several clients at once is a requirement (3-4 instances, no shared-file corruption). Work pushed to github.com/modernuo/ModernUO-Client (remote `modernuo`), never forced; `legacy-main` L2-L5 dropped (the pack replaces them), L1 and L7 kept (L1 done, L7 measured negligible).
- 2026-10-25: SDL fork pushes allowed without asking (no upstream contributions); fork branch rule `modernuo/<release>-patched` (SDL 3.4.18, FNA/FNA3D 26.10); libusb waits for a specific controller.
- 2026-10-31: upstream PR #1889 (gump render cache) parked: ~9 us gain, not worth the invalidation code.
- 2026-11-01: `poc/modern-client-clean-2` renamed to `poc/modern-client-clean` and force-pushed over the old one (old tip tagged `archive/poc-modern-client-clean-old`).
- 2026-11-02: every accepted time/memory cost, even ~1 %, goes in accepted-costs.md for the final audit.
- 2026-11-07: T9 deletion of the flipped switches' old paths was parked until quiet timing; since then done in waves.
- 2026-11-08: main thread priority raised only around the frame limiter's sleep and Present, and lowered while minimized (process BELOW_NORMAL, restored on restore; Linux thread-level or skipped; macOS QoS). Agent worktrees get submodules through `tools/worktree-externals.sh`. Gradual ECS dual-path cost accepted as "re-measure after T9"; T9 and known fixes run concurrently with one full timing pass at the end.
- 2026-11-08: allocation checks run on every ECS/perf branch now; CPU/FPS timing stays parked until the owner says the machine is quiet.

Answered 2026-11-10
- Renderer: auto-detection stays; the override (Automatic / GPU / Compatibility) is in Options and settings.json; the renderer tag shows in the title only in dev builds; the user-facing name "Legacy" becomes "Compatibility" (the renderer stays).
- Bodies: `sequences` defaults to `target`; `forceOldAnimations` reverts only the LBR (Bodyconv column 1) remodels: 9, 10, 26, 28, 76; additions are not reverted. Body 95 (turkey) stays as it is: an addition with its own model (anim3), not affected by `forceOldAnimations`.
- `Clilocs.txt`: folded into the pack (already since text-in-pack), a loose file still overrides; `--cliloc-overrides <file>` for a file outside the install.
- Accepted: 4K pool caps unchanged until a 4K measurement; warm-up collection once per process; U+FFE0 draws no glyph.
- Empty tiledata entries: skip components without art in the roof and light decisions (parity-checked).
- ECS wave 1E (HouseManager component): skipped. Multis (houses, boats) get their own later project: performance, fixes, smooth movement.
- SDL3 HIDAPI: deferred to a later project.
- Clean branch: you review it after all milestones.
- Plugins: their own later project; tentatively drop the existing CUO plugin API for the plugin v2 work (ClassicUO PR #1895, `kamronbatman:feat/cuo_plugin_v2`) or something better. The connect-time send question folds into it.
- Next work approved: the P3 bug bundle, the text/UI allocation batch, the multi-instance audit, Item pooling on range entry (after T9 wave B).

## 3. Queued work (not started)

- Item pooling: pool `Item` objects entering range (~4.3 MB per 15 s of ls_walk), after T9 wave B.
- Multis project: houses and boats handling, performance, smooth movement.
- Plugins project: plugin v2 (see Decisions).
- GitHub Actions for Linux/macOS: build, test, native libraries (cuo_mp3, SDL/FNA forks), release artifacts (`cuo-poc-state/handoffs/ci-release-artifacts.md`).
- Minimized behaviour: process priority lowered while minimized, FAudio mixing paused (needs timing under load).
- Server update range vs large viewports: edge-ghosting repro, ModernUO create/remove audit, client cleanup, range-based streaming (perf-opportunities.md); gump tooltips for objects the client never saw (repro first).
- T9 remainder: `TNext`/`TPrevious` off dynamics slice (16 B per dynamic, ~0.5 MB at 30k, after wave B), gpu_ecs_sprites parity fixes (Codex audit: 2 P2) then capture-path deletion, world index slice 5 (delete off paths, checker, `ItemTable.Values` walk, bookkeeping dictionaries), regenerate ecs-state-census.md section 2.5.
- Final timing pass after T9 with settled switches (parked scripts in `cuo-poc-state/measure/*/job-*-PARKED.sh`; several need their AOT builds rebuilt); then .NET 11 on its own branch.
- Friflo: clear vacated component tails that hold references (`EntityName` strings linger after delete); fork or upstream, not a per-component workaround.
- Serial scheme flexibility (survey in `cuo-poc-state/surveys/`): shape the code so a shard could change its serial ranges mechanically.
- ModernUO-style double-mapped network pipe (high risk, own project; perf-opportunities.md).
- Minor: `[TestScene` leftovers in `tools/fps-test/run-fps-test.ps1` and `quiet-1108/bisect/series.sh` (server lacks the command; use `[go 5156 2150` / `[go 1495 1629`); deploy ModernUO 7d52e9b33 (cosmetic `[TestScene create` timer) with the next server restart.

## 4. History

What merged and when, with verification, is in [poc-log.md](poc-log.md): milestone 2026-10-31 (packs classic-1.1/1.2, audio, memory rounds, roof fix), the ECS phases T0-T9, the quiet-1108 timing and bisect, and the T9 waves. The history-cleanup plan and result are in `cuo-poc-state/handoffs/history-cleanup.md` and `history-cleanup-result.md`. Re-measure items from noisy-machine runs are folded into the final timing pass above.
