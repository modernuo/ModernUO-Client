# Known issues

Open defects only, one entry each: severity, symptom, location, status. Fixed ones are removed (the poc-log has the history); a short Resolved list at the bottom keeps commit ids worth finding.

## Bugs

- **Dev tooling only. DevBench pickscan makes Gpu land blink black.** The scan flips `gpu_prepare_skip_baked` every ~3 frames per point (`DevBench.cs` ~1500-1520) and the frame of each flip draws baked land neither way. Normal play never toggles that switch. Goes away if the perf-switch sweep after the quiet pass deletes the off path; if the switch stays, make the transition frame draw land.
- **Medium. Ghost mobiles and items at the update-range edge** (predates the PoC). Rarely a mobile or item the server stopped tracking stays idle forever, or an edge mobile disappears although it should stay; more likely with large viewports, older RunUO servers, houses and boats (a house can arrive without its door). Cause is either the server's create/remove messages at the edge or the client's ordering and out-of-range cleanup. Not reproduced. Status: deferred; first step of range-based streaming (perf-opportunities.md, "Server update range").
- **Low. Paperdoll layer bleed:** with a robe on, the black halo around armour arm pieces disappears (ClassicUO does not do the original client's graphic bleed). The halo may hide another layering oddity, so removing it could expose that. Not started.
- **Low. Gpu shoreline ticks at zoom 2:** a few short dark ticks where shore statics (not 44x44) meet open water; they take the generic trilinear alpha-test path. Stand at Britain 1495 1629, Gpu, zoom 2: east about x 1519-1523, y 1598-1619 and west about x 1456-1458, y 1631-1640 (+-1-2 tiles). Legacy shows none. Waiting for the owner's manual check before anyone fixes it.
- **Low. Gpu pop-in leftovers after the world-settle hold:** (a) walking never holds, so a dynamic sprite whose art page is not resident is presented for about 10 ms (5-20 frames per 10 s walk); fix on the streaming side by prefetching item and mobile art pages. (b) A held frame shows the old place while UI, overhead text and picking already follow the new position (3-5 frames at 60 fps after a teleport). (c) The hold does not detect stale override state (suspected `gpu_prepare_skip_baked` flicker after a recreation). (d) No live facet-change test (unit-tested; `reinit` exercises the same path). (e) A body seen for the first time draws nothing until its first direction uploads, so new equipment on a visible human can appear a frame or two late (`CUO_ANIM_SYNC=1` compares); nobody checked by eye.
- **Low. A chunk on a new glide column can appear a few frames late** (art latency; walking-unsettled frames 198 to 338 over three glide runs). Fix if it matters: request art for a ring beyond the draw range.
- **Low. Legacy `MeshLayer` arrays keep growing in reused chunks** (0.3-1 MB per 5 s while teleporting; login warm-up audit, not re-checked since the chunk pool became per World).
- **Low. Legacy fallback paths (D3D11, OpenGL):** nothing broken, observations for the deferred Legacy project. OpenGL lands on the Intel UHD 770, not the RTX (needs adapter selection). D3D11 spends 2.5x longer in Present than SDL_GPU on the same GPU (suspect `FNA3D_D3D11_FORCE_BITBLT=1` in `Main.cs`, untested; measure with the display on). FNA3D does not fall through a forced driver: if MojoShader's d3dcompiler is missing, the client cannot start (very unlikely on Windows 10/11). Britain fps: SDL_GPU Legacy 869, D3D11 407, OpenGL (UHD) 258.
- **Low. GPU engine resets take down every client on the GPU** (a page fault in one process causes an NVIDIA reset; the others exit 0xDEAD). Our own faults are gone with the SDL fork's dedicated large-image allocations (sdl-fork.md); a build with an upstream SDL3.dll still faults at about 3 % of logins. When attributing an Aftermath dump, match its PID to the run; keep deliberate repros few.
- **Low. Pickscan:** about 250 "chunks missing near view" unsettled frames per entry (base too). The skip checker reports land at the top edge "drawn differently" during zoom 2.5 walks (199 on base, 632 on E7's build in one run; look at W1's edge band).
- **Low. Plugin threads calling `RequestMove`/`WalkTo`** race the pathfinder's reused buffers and `Player.Walk`'s world writes (main-thread only today; perf-alloc-5 Codex P2). Possible later item: marshal plugin entry points onto the game thread.
- **Low. Item 0x3996 near 5138,2157 (z 8) changes position between scans in base too** (moving fire field); it shows up as a pick-scan difference. `DevBench.Describe` prints the graphic, not the serial, which makes moving-NPC and moving-item differences hard to tell apart.
- **Low, unverified. WorldMapGump P3:** `Log.Info` at `WorldMapGump.cs:~1565` sits before ownership transfer of the new texture, so a throwing logger would leak it until finalization; move it inside the cleanup `try` at the next touch.
- **Low. Unexplained single-frame spikes** (16-50 ms `update_other`, 95 ms at a facet switch) seen during verification, never compared with base.

## Tests and gates

- **Possible hang:** a full-suite stall after the `MountHeightTests` skips (seen twice with another dotnet process active) was not reproduced. If it recurs, rerun with `--blame-hang-timeout 120s`. Tests outside the non-parallel collections that mutate `Time.Ticks` or the shared RNG can still overlap each other.
- **Gate flake: disconnect right after login** (bench-000292: "Disconnected from the game server: Success" 2 s after entering the world, 15 s after the previous session of the same account quit). Rerun clean. If it recurs, check the server log for the previous session still logging out.
- **Gate flake: uistate vendor buy.** `buy a_horse 1` sometimes finds no buy-list entry and the rename/pet steps fail (bench-000130, 000177). Fix: wait for the buy list and restock or pick another item.
- **Gate rules to refresh before reuse:** ecs-flip-8 `gate-body.sh` still expects `ecs_effect_sprites` off and a "shadow steps" effect-check line (both gone); `measure/t9-delete-1/common9.sh` regex was fixed. The textflood Gpu read rule was relaxed. Fix `iocheck` to skip scenes with no dynamic item draws.

## Resolved (commit worth keeping)

- Roof over the player drawn after login/teleport: 7289d751f (`MaxDrawZState`, `Map.DecisionDirty`).
- Legacy tile corruption (B1): b62406b8e workaround, then FNA3D fork e3bd4f2 (cycled index buffer rebind); freed pending render target: FNA3D 19e5188.
- Scissor exception: b59735443. Zoomed-out land seams: 115e93c71. GPU page fault: SDL fork patch (08b952f3a, now `modernuo/3.4.18-patched`).
- Raw pop-in on teleports/world entry: a6110f967 (world settle).
- `WorldMapGump` texture race and off-thread `Print`: 398510734, 5dbb5665d (texture handed over, created in Update; verified the load completion no longer prints from the task at `WorldMapGump.cs:545-547`).
- Zero-size backbuffer from `WindowOnClientSizeChanged`: 957237f06.
- Login disconnect flake (auth id starting 0xEF read as the seed): ModernUO#2744.
- Flaky tests isolated on `bg/flaky-tests` and `bg/removal-flake`; `ChunkLoadingTests` prefetch timing: c6bd97205.
- Static chunk pool not per World, double `Chunk.Destroy`: 6c7f60f43. E4 `Dictionary` version counter: fdd40344a (owned `MobileTable`). `-ecsdriver` reachable without devtools: now compiled only with `CUO_DEVTOOLS` (`EcsSessionDriver.cs`).
- Main-thread ground-item/multi art decode on first sight: 697e4c668 (the earlier "item checker compares different frames" E6 audit note was not rechecked).
- 2026-11-10 bug-bundle-1110 (7d6419d03): SeasonManager creates `Data/Client`; minimap draws safely before its first Update; the two flaky allocation tests warm their paths. Multi-instance file races: merged 766efc9ca (poc-log "Multi-instance audit and fixes"). Empty tiledata entries: fix on `bg/tiledata-empty`, parity job pending.
