# Owner review

Open owner decisions, decisions already made, and queued work. History (what merged, gates, measurements, audits) is in [poc-log.md](poc-log.md); open bugs are in [known-issues.md](known-issues.md), perf backlog in [perf-opportunities.md](perf-opportunities.md), accepted costs in [accepted-costs.md](accepted-costs.md).

## 1. Needs you

Each line: the question, then the recommendation on record.

- **FPS test:** run `tools/fps-test/run-fps-test.ps1 -Username kbatman` with no agent clients running and send the `results-<timestamp>.md` path. The published build is cd670ceb9 (2026-10-26); it predates all of T4 to T9, so a new build is needed first. Nothing else is waiting on it.
- **Renderer wording:** the renderer-selection error box ("The GPU world renderer could not start (<reason>). ClassicUO will close.") and the title `ClassicUO [Legacy renderer] - 1.1.0.0`. Agent suggestion: say how to recover ("Set world_renderer to \"auto\"..."), put the reason on its own line, show "[Legacy renderer]" only in dev builds or rename it ("Compatibility renderer"), and avoid "GPU world renderer" (Legacy also uses the GPU). Nothing changed yet.
- **Body-profile review** (`poc/body-profile`, bodies.md, poc-log "Body import choices"): (1) body 95 (turkey) counts as a remodel, its anim.mul slot holds an older grey creature, so `t2a`/`lbr` turkeys draw that creature; keep or exclude 95. (2) Which AnimationSequence list the OSI client uses for converted bodies: `sequences` none (default) or target (changes 123 more bodies beyond 9/26/28 and hides own MUL frames in 614 groups; confirm in game with daemon 9 `[Animate 12 7 3 true false 0`, or choose "fill only empty groups"). (3) `forceOldAnimations` scope (every remodel today). Nuance defaults proposed: additions always use newest art unless "stand-ins" is chosen; "none" turns off all Bodyconv columns; LBR preset = column-1 remodels only; UOP bodies (mobtypes 0x10000) always on.
- **ECS wave 1E (HouseManager as a `House` component):** optional, low value. Recommendation: skip.
- **Clean branch review:** `poc/modern-client-clean` (code-only history, built and tree-checked at each wave; last sync 4975a8c74 = 69e56394e). Review it, then decide whether it replaces `poc/modern-client` and where it is pushed. Nothing is force-pushed.
- **Fold `Clilocs.txt` into the pack?** Today a loose text file read on top of the pack. Recommendation: yes; the converter folds it in and a loose file still overrides, so shards can ship it without rebuilding the pack.
- **4K / zoomed-out pool caps:** the chunk pool is capped at 300 chunks (with matching land and static pools); bigger views load more. Raise the caps with view size? Unchanged until a 4K measurement shows it matters.
- **Warm-up collection on relog:** the login warm-up's two collections run once per process. Recommendation: keep once per process; the pools are topped up on relog (a no-op when full).
- **U+FFE0 in unifont3-12** crashed the old client (glyph points past the end of the file); it now draws no glyph. Recommendation: accept (a crash fix; the legacy-exact policy covers behaviour, not crashes).
- **Empty tiledata entries occlude roofs and lights** (known-issues.md): proposed fix is to skip components without drawable art in both decisions, after a parity check on Legacy and Gpu with modern and padded legacy packs. Say go or defer.
- **Optional, SDL3 HIDAPI:** the fork-built SDL3.dll has no libusb HIDAPI support (the old upstream DLL had it); matters only for unusual controllers. Say if you want it built in.
- **Plugins at connect time:** sends from plugin threads during the login/game-server handshake are now held and sent just after it. If you run ClassicAssist or Razor scripts that act at the moment of connecting, one run with them would confirm nothing changed.

## 2. Decisions made

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

## 3. Queued work (not started)

- GitHub Actions for Linux/macOS: build, test, native libraries (cuo_mp3, SDL/FNA forks), release artifacts (`cuo-poc-state/handoffs/ci-release-artifacts.md`).
- Multi-instance audit: profiles/settings races, logs, screenshot names, caches, pack opened read-only with shared access, memory/GPU at 4 clients.
- Minimized behaviour: process priority lowered while minimized, FAudio mixing paused (needs timing under load).
- Server update range vs large viewports: edge-ghosting repro, ModernUO create/remove audit, client cleanup, range-based streaming (perf-opportunities.md); gump tooltips for objects the client never saw (repro first).
- T9 remainder: `TNext`/`TPrevious` off dynamics slice (16 B per dynamic, ~0.5 MB at 30k, after wave B), gpu_ecs_sprites parity fixes (Codex audit: 2 P2) then capture-path deletion, world index slice 5 (delete off paths, checker, `ItemTable.Values` walk, bookkeeping dictionaries), regenerate ecs-state-census.md section 2.5.
- Flip `ecs_item_draw_row` then `legacy_ecs_sprites` (decided 2026-11-10); branches `bg/legacy-sprites-fix` (b7ed4f80d) and `-2` (9016e016c) unmerged.
- Final timing pass after T9 with settled switches (parked scripts in `cuo-poc-state/measure/*/job-*-PARKED.sh`; several need their AOT builds rebuilt); then .NET 11 on its own branch.
- Plugin API: read API over the OPL store (by serial/revision with a change event); marshal plugin movement entry points (`RequestMove`, `WalkTo`) onto the game thread (today main-thread only).
- Friflo: clear vacated component tails that hold references (`EntityName` strings linger after delete); fork or upstream, not a per-component workaround.
- Serial scheme flexibility (survey in `cuo-poc-state/surveys/`): shape the code so a shard could change its serial ranges mechanically.
- ModernUO-style double-mapped network pipe (high risk, own project; perf-opportunities.md).
- Minor: `[TestScene` leftovers in `tools/fps-test/run-fps-test.ps1` and `quiet-1108/bisect/series.sh` (server lacks the command; use `[go 5156 2150` / `[go 1495 1629`); deploy ModernUO 7d52e9b33 (cosmetic `[TestScene create` timer) with the next server restart.

## 4. History

What merged and when, with verification, is in [poc-log.md](poc-log.md): milestone 2026-10-31 (packs classic-1.1/1.2, audio, memory rounds, roof fix), the ECS phases T0-T9, the quiet-1108 timing and bisect, and the T9 waves. The history-cleanup plan and result are in `cuo-poc-state/handoffs/history-cleanup.md` and `history-cleanup-result.md`. Re-measure items from noisy-machine runs are folded into the final timing pass above.
