# ClassicUO Per-Phase Performance Baseline (Phase 4, step 7b)

Recorded 2026-10-24 on `poc/modern-client` (on top of f89c6107f, pack-only assets), with the per-phase hooks added in step 7b. It replaces the coarse frame/scene/Draw() numbers in classicuo-aot-baseline.md as the reference for step 10.

## Method

**Hooks** (`src/ClassicUO.Client/FrameStats.cs`, wired into GameController, GameScene, World and `UltimaBatcher2D`):

- Enabled with `CUO_FRAMESTATS=1`. `FrameStats.On` and `UltimaBatcher2D.CollectFrameStats` are `static readonly bool`s, so with the variable unset every hook is one predictable branch with no timestamps and no allocation. The sample arrays are only allocated when it is on.
- Each phase records `Stopwatch.GetTimestamp()` deltas into a per-frame accumulator. At the end of each frame (`GameController.EndDraw` override, after `base.EndDraw()`) the accumulators are copied into fixed `float[32768]` arrays. Every 5 s it logs a human table (avg/p50/p95/p99 per phase, avg/p50/p95/max per count) and one machine-readable line, `FRAMESTATS {json}`, for diffing runs.
- **Phases:** event pump (end of the previous EndDraw to the start of Update); ParsePackets; Plugin.Tick; GameScene.Update, split into World mobiles loop, items loop, effects+text (+WMap cleanup) and Ecs.Update, plus the rest of the scene update; UIManager.Update; other Update work; draw setup (EnsureSizes and clear); lights; world prepare (`FillGameObjectList` plus mesh highlight); world draw (`DrawRenderLists`, multi preview, weather, selection); scene DrawUI (health lines, world text); UIManager.Draw; cursor; RT composite (`RenderTargets.Draw`); plugin draw; other Draw work; **Present** (`base.EndDraw()`, i.e. FNA3D SwapBuffers); frame total.
- **Batcher:** `Flush()` and `DrawDirectIndexed()` count flushes and draw calls and time the FNA calls inside them (ApplyStates, `SetDataPointerEXT`, effect apply, `DrawIndexedPrimitives`). This is the "time inside FNA per draw" number from the plan.
- **Counts per frame:** World items and mobiles, gumps (controls are counted once per window, at report time), world render-list entries (`RenderedObjectsCount`), flushes, draw calls, bytes allocated on the main thread.
- **GC per window:** gen0/1/2 counts, `GC.GetTotalPauseDuration()` delta, total allocated bytes.
- `CUO_FRAMESTATS_PHASES=0` keeps only frame and Present timing, which is how hook overhead was measured.

**Scripted bench** (`DevBench.cs`, `CUO_BENCH_PLAN`): each entry `[go`es to a spot, sets the zoom, waits 8 s for streaming, then measures `CUO_BENCH_WINDOWS` (3) windows of 5 s. `travel` teleports Britain → Trinsic → Vesper → Minoc → Yew → Skara Brae → Moonglow every 2 s while measuring. `walk` pathfinds back and forth between 1495,1629 and 1475,1650 (in practice it shuttles 1495,1629 ↔ 1482,1638). `CUO_BENCH_SIZE=1920x1080` resizes the client area and makes the game viewport fill it. `say:text` and `vsync:on|off` entries are also supported.

```
CUO_FRAMESTATS=1 CUO_BENCH_EXIT=1 CUO_BENCH_SIZE=1920x1080 CUO_BENCH_WINDOWS=3
CUO_BENCH_PLAN="britain:1495 1629:1;bank:1430 1690:1;zoomout:1495 1629:2.5;zoomin:1495 1629:0.5;walk:walk:1;travel:travel:1"
cuo.exe -settings <scratch>\settings.json -profilespath <scratch>\profiles -username <acct> -password <pw> -autologin true -skiploginscreen -saveaccount false -pack C:\Repositories\uo-packs\classic-1.0
```

ClassicUO rewrites `window_size` in settings.json on exit (in DPI-scaled units), so each run restored a pristine settings.json and profile first.

## Conditions

- **Machine:** i9-12900K (24 threads), RTX 3090 Ti, Windows 11, a 4K display at 150% scale (DPI scale 1.5). Vulkan (`driver=vulkan shaders=SPIRV`).
- **Load:** checked before the runs: 0–4.5% total CPU, no other agents, only idle IDE/language-server processes. The ModernUO server (local, already running) used a little CPU.
- **Window:** client area **1920×1080 px**, backbuffer 1920×1080, game viewport maximised (camera 1280×720 logical × 1.5). One extra run used a maximised 3840×2054 window to bridge to the old baseline.
- **Settings:** FPS uncapped (`fps: 0`), VSync off (`PresentationInterval.Immediate`), "reduce FPS when inactive" forced off by `CUO_FRAMESTATS`, ECS consistency check off, no plugins. The character profile had the paperdoll and a reward prompt open (4 gumps, about 101 controls).
- **Builds:** Release JIT (`bin/Release/net10.0/cuo.exe`, tiered PGO). NativeAOT win-x64 with `IlcInstructionSet=x86-64-v3`, and the same AOT build with the default ISA (`-p:IlcInstructionSet=`).
- **Runs:** JIT is the mean of two runs (their per-spot frame avg agree within 5%). AOT v3 and AOT default-ISA are one run each. Every value is the mean of three 5-s windows.

**Caveats:**

- **The shard is sparse.** Britain has 4–7 mobiles and about 60–90 items in range, and the "bank" spot (1430,1690) has 4 mobiles, so the bank is *not* the busy scene step 10 asks for. Spawning a crowd on the shared server was not permitted in this session, so a busy-scene run (≥100 mobiles) is still to do (see "Next measurements").
- **Zoom:** ClassicUO's camera scale is `1/Zoom`, so `Zoom 0.5` is the closest zoom-*in* and `Zoom 2.5` is the widest zoom-*out*. "zoomout" below is 2.5, the heaviest world view; "zoomin" is 0.5.

## Results (1920×1080)

| build | spot | fps | frame avg / p50 / p95 / p99 | Update | Draw | Present p50 / avg / p99 | gen0/s | alloc/frame |
|---|---|---|---|---|---|---|---|---|
| JIT | britain | 1439 | 0.695 / 0.515 / 0.735 / 5.11 | 0.012 | 0.331 | 0.128 / 0.346 / 4.79 | 1.7 | 17.7 KB |
| JIT | bank | 1505 | 0.664 / 0.483 / 0.711 / 5.11 | 0.012 | 0.303 | 0.123 / 0.344 / 4.83 | 1.7 | 17.7 KB |
| JIT | zoomout | 746 | 1.341 / 1.319 / 1.534 / 1.80 | 0.016 | 1.204 | 0.110 / 0.115 / 0.20 | 0.9 | 17.7 KB |
| JIT | zoomin | 1568 | 0.638 / 0.490 / 0.770 / 5.23 | 0.012 | 0.204 | 0.239 / 0.416 / 5.04 | 1.8 | 17.7 KB |
| JIT | walk | 1452 | 0.689 / 0.516 / 0.734 / 5.01 | 0.017 | 0.342 | 0.111 / 0.324 / 4.69 | 1.7 | 18.2 KB |
| JIT | travel | 1430 | 0.700 / 0.521 / 0.756 / 5.10 | 0.016 | 0.348 | 0.114 / 0.330 / 4.78 | 1.8 | 21.0 KB |
| AOT v3 | britain | 1396 | 0.717 / 0.548 / 0.777 / 4.88 | 0.016 | 0.391 | 0.105 / 0.302 / 4.53 | 1.5 | 16.6 KB |
| AOT v3 | bank | 1460 | 0.685 / 0.509 / 0.728 / 5.01 | 0.015 | 0.352 | 0.110 / 0.311 / 4.70 | 1.6 | 16.7 KB |
| AOT v3 | zoomout | 718 | 1.393 / 1.359 / 1.680 / 2.02 | 0.018 | 1.251 | 0.112 / 0.117 / 0.20 | 0.7 | 16.6 KB |
| AOT v3 | zoomin | 1522 | 0.657 / 0.508 / 0.780 / 5.16 | 0.015 | 0.236 | 0.240 / 0.400 / 4.95 | 1.7 | 16.6 KB |
| AOT v3 | walk | 1416 | 0.706 / 0.539 / 0.723 / 4.84 | 0.019 | 0.386 | 0.103 / 0.294 / 4.50 | 1.6 | 17.1 KB |
| AOT v3 | travel | 1395 | 0.717 / 0.546 / 0.764 / 4.80 | 0.019 | 0.398 | 0.102 / 0.293 / 4.44 | 1.6 | 19.8 KB |
| AOT default ISA | britain | 1410 | 0.709 / 0.536 / 0.717 / 4.89 | 0.020 | 0.385 | 0.101 / 0.298 / 4.53 | 1.5 | 16.6 KB |
| AOT default ISA | bank | 1468 | 0.681 / 0.510 / 0.689 / 4.99 | 0.019 | 0.357 | 0.104 / 0.299 / 4.67 | 1.6 | 16.6 KB |
| AOT default ISA | zoomout | 715 | 1.399 / 1.382 / 1.606 / 1.85 | 0.022 | 1.252 | 0.113 / 0.117 / 0.17 | 0.8 | 16.6 KB |
| AOT default ISA | zoomin | 1528 | 0.654 / 0.497 / 0.760 / 5.15 | 0.019 | 0.244 | 0.219 / 0.385 / 4.93 | 1.7 | 16.6 KB |
| AOT default ISA | walk | 1419 | 0.705 / 0.525 / 0.731 / 4.94 | 0.023 | 0.376 | 0.101 / 0.300 / 4.60 | 1.5 | 17.1 KB |
| AOT default ISA | travel | 1402 | 0.713 / 0.539 / 0.759 / 4.86 | 0.023 | 0.390 | 0.102 / 0.294 / 4.50 | 1.6 | 19.8 KB |
| JIT 3840x2054 | britain | 712 | 1.406 / 1.328 / 2.129 / 2.63 | 0.015 | 0.850 | 0.455 / 0.534 / 1.80 | 0.9 | 17.6 KB |
| JIT 3840x2054 | zoomout | 177 | 5.658 / 5.659 / 6.317 / 6.71 | 0.031 | 5.425 | 0.186 / 0.192 / 0.29 | 0.2 | 17.7 KB |

#### JIT: avg (p99) in ms
| phase | britain | bank | zoomout | zoomin | walk | travel |
|---|---|---|---|---|---|---|
| event pump | 0.006 (0.025) | 0.006 (0.024) | 0.007 (0.023) | 0.006 (0.018) | 0.006 (0.028) | 0.006 (0.026) |
| ParsePackets | 0.003 (0.017) | 0.003 (0.010) | 0.004 (0.014) | 0.003 (0.009) | 0.004 (0.036) | 0.005 (0.024) |
| GameScene.Update | 0.002 (0.011) | 0.002 (0.009) | 0.003 (0.011) | 0.002 (0.009) | 0.003 (0.021) | 0.003 (0.017) |
|   World: mobiles | 0.001 (0.002) | 0.001 (0.001) | 0.001 (0.002) | 0.001 (0.002) | 0.001 (0.005) | 0.001 (0.002) |
|   World: items | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.002) |
|   World: effects+text | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.001) | 0.000 (0.001) | 0.000 (0.000) | 0.000 (0.001) |
|   World: Ecs.Update | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) |
|   scene update rest | 0.001 (0.007) | 0.001 (0.007) | 0.001 (0.009) | 0.001 (0.006) | 0.001 (0.008) | 0.001 (0.008) |
| UIManager.Update | 0.003 (0.010) | 0.003 (0.006) | 0.003 (0.010) | 0.003 (0.005) | 0.003 (0.013) | 0.003 (0.014) |
| update other | 0.004 (0.011) | 0.004 (0.005) | 0.005 (0.014) | 0.004 (0.006) | 0.007 (0.017) | 0.004 (0.019) |
| **Update total** | 0.012 (0.039) | 0.012 (0.034) | 0.016 (0.070) | 0.012 (0.037) | 0.017 (0.059) | 0.016 (0.049) |
| lights | 0.012 (0.037) | 0.011 (0.034) | 0.013 (0.048) | 0.012 (0.035) | 0.012 (0.040) | 0.011 (0.039) |
| world prepare | 0.110 (0.176) | 0.087 (0.143) | 0.565 (0.813) | 0.041 (0.086) | 0.105 (0.180) | 0.105 (0.175) |
| world draw | 0.113 (0.183) | 0.110 (0.178) | 0.520 (0.671) | 0.053 (0.108) | 0.120 (0.200) | 0.126 (0.218) |
| UIManager.Draw | 0.051 (0.100) | 0.049 (0.099) | 0.055 (0.102) | 0.050 (0.104) | 0.050 (0.108) | 0.050 (0.108) |
| RT composite | 0.040 (0.082) | 0.040 (0.085) | 0.042 (0.081) | 0.042 (0.085) | 0.042 (0.098) | 0.041 (0.096) |
| draw other | 0.004 (0.013) | 0.004 (0.012) | 0.007 (0.026) | 0.004 (0.019) | 0.005 (0.022) | 0.005 (0.019) |
| **Draw total** | 0.331 (0.474) | 0.303 (0.441) | 1.204 (1.556) | 0.204 (0.315) | 0.342 (0.501) | 0.348 (0.522) |
|   of which batcher FNA calls | 0.107 (0.177) | 0.106 (0.177) | 0.294 (0.395) | 0.082 (0.146) | 0.115 (0.195) | 0.111 (0.191) |
| **Present** | 0.346 (4.791) | 0.344 (4.828) | 0.115 (0.201) | 0.416 (5.037) | 0.324 (4.693) | 0.330 (4.779) |
| **frame** | 0.695 (5.107) | 0.664 (5.114) | 1.341 (1.795) | 0.638 (5.227) | 0.689 (5.010) | 0.700 (5.100) |
| count: items | 69 | 61 | 69 | 70 | 45 | 92 |
| count: mobiles | 6 | 4 | 6 | 6 | 5 | 6 |
| count: render_list | 2699 | 1522 | 12054 | 794 | 2096 | 1998 |
| count: flushes | 16 | 16 | 16 | 17 | 17 | 17 |
| count: draw_calls | 86 | 85 | 326 | 46 | 87 | 86 |

#### AOT v3: avg (p99) in ms
| phase | britain | bank | zoomout | zoomin | walk | travel |
|---|---|---|---|---|---|---|
| event pump | 0.007 (0.034) | 0.006 (0.028) | 0.007 (0.019) | 0.006 (0.020) | 0.007 (0.033) | 0.007 (0.033) |
| ParsePackets | 0.004 (0.025) | 0.004 (0.022) | 0.004 (0.014) | 0.004 (0.012) | 0.004 (0.036) | 0.006 (0.031) |
| GameScene.Update | 0.003 (0.019) | 0.003 (0.016) | 0.004 (0.013) | 0.003 (0.011) | 0.004 (0.024) | 0.004 (0.025) |
|   World: mobiles | 0.001 (0.002) | 0.001 (0.002) | 0.001 (0.002) | 0.001 (0.002) | 0.001 (0.007) | 0.001 (0.003) |
|   World: items | 0.001 (0.002) | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.001) | 0.001 (0.002) |
|   World: effects+text | 0.000 (0.001) | 0.000 (0.001) | 0.000 (0.001) | 0.000 (0.001) | 0.000 (0.000) | 0.000 (0.001) |
|   World: Ecs.Update | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) | 0.000 (0.000) |
|   scene update rest | 0.001 (0.009) | 0.001 (0.008) | 0.001 (0.010) | 0.001 (0.008) | 0.001 (0.009) | 0.001 (0.009) |
| UIManager.Update | 0.004 (0.024) | 0.004 (0.019) | 0.004 (0.013) | 0.004 (0.012) | 0.004 (0.024) | 0.004 (0.025) |
| update other | 0.004 (0.023) | 0.004 (0.018) | 0.006 (0.012) | 0.004 (0.010) | 0.007 (0.023) | 0.005 (0.025) |
| **Update total** | 0.016 (0.051) | 0.015 (0.043) | 0.018 (0.062) | 0.015 (0.041) | 0.019 (0.068) | 0.019 (0.063) |
| lights | 0.017 (0.052) | 0.018 (0.053) | 0.018 (0.042) | 0.018 (0.054) | 0.017 (0.052) | 0.015 (0.049) |
| world prepare | 0.119 (0.204) | 0.092 (0.163) | 0.563 (0.935) | 0.046 (0.099) | 0.106 (0.182) | 0.107 (0.183) |
| world draw | 0.129 (0.221) | 0.122 (0.199) | 0.547 (0.748) | 0.057 (0.116) | 0.130 (0.217) | 0.140 (0.241) |
| UIManager.Draw | 0.064 (0.135) | 0.061 (0.127) | 0.063 (0.113) | 0.058 (0.112) | 0.061 (0.125) | 0.062 (0.126) |
| RT composite | 0.054 (0.118) | 0.053 (0.118) | 0.051 (0.088) | 0.051 (0.096) | 0.055 (0.119) | 0.055 (0.118) |
| draw other | 0.006 (0.029) | 0.005 (0.025) | 0.007 (0.024) | 0.004 (0.021) | 0.006 (0.029) | 0.006 (0.028) |
| **Draw total** | 0.391 (0.552) | 0.352 (0.508) | 1.251 (1.773) | 0.236 (0.344) | 0.386 (0.537) | 0.398 (0.567) |
|   of which batcher FNA calls | 0.130 (0.225) | 0.125 (0.213) | 0.317 (0.447) | 0.093 (0.160) | 0.133 (0.222) | 0.130 (0.215) |
| **Present** | 0.302 (4.527) | 0.311 (4.699) | 0.117 (0.204) | 0.400 (4.948) | 0.294 (4.500) | 0.293 (4.443) |
| **frame** | 0.717 (4.880) | 0.685 (5.008) | 1.393 (2.023) | 0.657 (5.165) | 0.706 (4.838) | 0.717 (4.800) |
| count: items | 68 | 61 | 69 | 69 | 44 | 92 |
| count: mobiles | 4 | 4 | 5 | 4 | 5 | 6 |
| count: render_list | 2698 | 1522 | 12054 | 794 | 2102 | 1996 |
| count: flushes | 17 | 17 | 17 | 17 | 18 | 18 |
| count: draw_calls | 87 | 86 | 327 | 47 | 88 | 86 |

## Bridge to the old baseline (maximised 3840×2054, JIT)

The old JIT baseline was p50 **1.33 ms** (about 700 fps) at 3840×2054 with a 1600×1000 viewport, with the frame split as "Draw() 0.53 ms, about 0.8 ms in Update/pump/Present". The same window size, now with a full-window viewport (2560×1369 logical, so roughly 1.6× the old viewport's world area), gives:

#### JIT 3840x2054: avg (p99) in ms
| phase | britain | zoomout |
|---|---|---|
| event pump | 0.006 (0.012) | 0.010 (0.028) |
| ParsePackets | 0.004 (0.006) | 0.006 (0.029) |
| GameScene.Update | 0.003 (0.011) | 0.006 (0.060) |
|   World: mobiles | 0.001 (0.002) | 0.001 (0.004) |
|   World: items | 0.001 (0.001) | 0.001 (0.002) |
|   World: effects+text | 0.000 (0.001) | 0.000 (0.001) |
|   World: Ecs.Update | 0.000 (0.000) | 0.000 (0.000) |
|   scene update rest | 0.001 (0.009) | 0.003 (0.058) |
| UIManager.Update | 0.003 (0.005) | 0.005 (0.010) |
| update other | 0.005 (0.005) | 0.014 (0.589) |
| **Update total** | 0.015 (0.044) | 0.031 (0.609) |
| lights | 0.008 (0.020) | 0.010 (0.017) |
| world prepare | 0.328 (0.521) | 2.785 (3.374) |
| world draw | 0.337 (0.425) | 2.443 (2.950) |
| UIManager.Draw | 0.053 (0.084) | 0.064 (0.099) |
| RT composite | 0.117 (0.166) | 0.111 (0.158) |
| draw other | 0.005 (0.012) | 0.010 (0.019) |
| **Draw total** | 0.850 (1.132) | 5.425 (6.408) |
|   of which batcher FNA calls | 0.241 (0.308) | 1.073 (1.225) |
| **Present** | 0.534 (1.796) | 0.192 (0.290) |
| **frame** | 1.406 (2.626) | 5.658 (6.708) |
| count: items | 71 | 71 |
| count: mobiles | 7 | 7 |
| count: render_list | 8070 | 40532 |
| count: flushes | 17 | 17 |
| count: draw_calls | 225 | 1041 |

- **Frame p50 is again 1.33 ms**, even though the viewport covers more world (8,070 render-list entries).
- **The old "0.8 ms outside Draw" was almost entirely Present, not Update.** Update is 0.015 ms; Present is 0.45–0.53 ms at 4K (faux-backbuffer blit plus swapchain), and the RT composite is another 0.12 ms.
- At 1920×1080 the same spot is p50 0.52 ms: Present drops to about 0.12 ms (p50) and the composite to 0.04 ms.
- **Zoomed out at 4K is the worst case so far:** 5.7 ms per frame (177 fps), with 2.8 ms of world prepare and 2.4 ms of world draw, 40.5k render-list entries and 1,041 draw calls.

## Hook overhead

| JIT, 1920×1080 | hooks off | frame + Present only (`PHASES=0`) | all phases |
|---|---|---|---|
| Britain fps (update count) | 1357 | 1364 | 1360 |
| Zoomed-out fps (update count) | 737 | 739 | 729 |
| Britain frame avg (FrameStats) | n/a | 0.700 ms | 0.701 ms |
| Zoomed-out frame avg (FrameStats) | n/a | 1.292 ms | 1.309 ms |

- **Off:** indistinguishable from the other modes, as expected (`static readonly` false, no timestamps, no allocation).
- **On:** about 50 timestamps per frame, plus two per batcher flush or mesh draw. That is noise at Britain (86 draw calls) and about 0.017 ms (+1.3%) zoomed out (326 draw calls).
- **Report:** sorting the arrays every 5 s costs about 2 ms. It is kept out of the next frame's pump time.

## AOT and the x86-64-v3 instruction set

- **v3 changes nothing measurable.** AOT v3 and AOT with the default ISA are within run-to-run noise on every phase: Britain Draw 0.391 vs 0.385 ms, zoomed out 1.251 vs 1.252 ms.
- **AOT is still about 15–20% slower than JIT on the CPU-heavy phases:**
  - Draw: Britain 0.39 vs 0.33 ms; zoomed out 1.25 vs 1.20 ms.
  - UIManager.Draw: 0.064 vs 0.051 ms.
  - Composite: 0.054 vs 0.040 ms.
  - Update: 0.016 vs 0.012 ms.
- **Total frame time is within 3–4%,** because Present waits dominate the frame at these frame rates.
- **So the gap is not ISA.** It is most likely the lack of Dynamic PGO (guarded devirtualisation and hot/cold layout of the many virtual calls in the batcher and the UI). The next AOT experiments are static PGO (a `.mibc` from a JIT trace) and `<OptimizationPreference>Speed</OptimizationPreference>`.
- **Allocation:** AOT allocates about 1.1 KB per frame less (16.6 vs 17.7 KB).
- v3 stays enabled per the owner's direction. It costs nothing and lets future vectorised code assume AVX2.

## Present behaviour

- **Present is bimodal above about 1,000 fps.** p50 is 0.10–0.13 ms, but about 4% of frames stall 4–5 ms, which lifts the average to about 0.33 ms and the frame p99 to about 5 ms.
- **No stalls at 746 fps** (zoomed out) or at 4K: Present is flat there (p99 0.2 ms zoomed out).
- **This is back-pressure** from SDL_GPU's frames in flight and windowed (DWM) IMMEDIATE presentation when the CPU submits faster than the GPU and present engine retire frames. It is not CPU work.
- **For CPU comparisons, use frame p50 or Update + Draw,** not frame avg or p99, until real frame rates are lower than this.
- VSync on (now a setting) gives a steady 61 fps.

## Where the time goes (1920×1080, JIT, Britain zoom 1)

- **Draw: 0.33 ms.** World prepare 0.11, world draw 0.11 (of which 0.107 is FNA calls across all batches), UIManager.Draw 0.05, RT composite 0.04, lights 0.012.
- **Present: 0.13 ms p50,** plus the stalls above.
- **Update: 0.012 ms in total.**
  - World mobiles plus items loops: about 1 µs (69 items, 6 mobiles).
  - Ecs.Update: below 0.1 µs.
  - UIManager.Update 0.003, ParsePackets 0.003, event pump 0.006.
- **CPU samples** (EventPipe sample profiler, 12 s at Britain):
  - `GameController.Draw` 45% of main-thread samples; the rest is mostly native Present.
  - `DrawWorld` 29%, `UltimaBatcher2D.Flush` 15.5%, `Effect.INTERNAL_applyEffect` 9.4%, `ApplyStates` 8.1%, `UIManager.Draw` 8.1%, `DrawDirectIndexed` 7.1%, `RenderTargets.Draw` 6.3%, `FillGameObjectList` 5.8%.
- **Allocation, 17.7 KB per frame:** gen0 about 1.7/s, gen1 0.3–3 per 5 s, GC pause 0.3–0.5 ms/s, rising to about 21 KB per frame and 1.2–1.5 ms/s of pause while travelling. AllocationTick sampling attributes about 90% to the UI draw path:
  - per-control `Func<UltimaBatcher2D,bool>` closures and display classes in `GumpPic`, `GumpPicTiled`, `Button` and `HtmlControl.AddToRenderLists`;
  - `Control.DrawDebug`'s display class, which is created on every call even when debug is off, because C# hoists the closure allocation to method entry (about 20%);
  - `new RenderLists()` (seven `List<>`s) inside `ResizePic`/`StbTextBox` render lambdas, every frame;
  - a string from `PaperDollGump.EquipmentSlot.Update` → `SetTooltip($"{Layer} slot")` every frame for each empty slot.

## Top costs, ranked

1. **World prepare plus world draw.**
   - Size: 0.22 ms of a 0.33 ms Draw at Britain; 1.09 of 1.20 ms zoomed out; 5.2 of 5.4 ms zoomed out at 4K.
   - Both scale with the render list (12k entries at 1080p, 40k at 4K) and draw calls (326 / 1,041).
   - This is what Uo.Render (step 8) replaces with baked chunks and one draw call. It is the biggest lever in every heavy scene.
2. **FNA per-batch overhead.**
   - Size: 0.11 ms at Britain, 0.29 ms zoomed out, 1.07 ms zoomed out at 4K.
   - ApplyStates and effect parameter upload run on every flush and every mesh-run draw (`DrawDirectIndexed` re-applies the pass per run).
   - Mostly removed by Uo.Render for the world. For the UI, a command-list device is the long-term route (see perf-opportunities.md).
3. **Present plus composite, scaling with window pixels.**
   - Size: 0.13 + 0.04 ms at 1080p; 0.45 + 0.12 ms at 4K.
   - Fix: render straight into the swapchain and skip the faux-backbuffer blit (FNA3D patch). Worth about 0.4 ms per frame at 4K.
4. **UI: Draw 0.05 ms, Update 0.003 ms, plus about 16 KB per frame of closure garbage.**
   - Quick wins: hoist `DrawDebug`'s lambda out of the hot path, cache the empty-slot tooltip string, and reuse the nested `RenderLists`.
   - Then: cache the UI render target and redraw it only when dirty.
5. **AOT steady-state code quality,** a 15–20% CPU gap to JIT that is not ISA-related. Try static PGO and `OptimizationPreference=Speed`.
6. **World update and ECS: negligible on this shard (about 1 µs).**
   - Moving update loops into ECS systems is not justified by these numbers.
   - Re-measure in a populated scene before ranking it (cuo-2.0-perf-comparison.md assumed Update was a large part of the frame; at 4K it was really Present).
7. **Pipelining or a render thread: not justified.** CPU work is 0.35 ms per frame at 1080p, and Present waits are GPU/present back-pressure, not CPU.

## Next measurements

- **A populated scene:** ≥100 mobiles at the bank, for example spawned by an owner-approved `[TileXYZ 1422 1692 12 10 0 Banker set CantWalk true Name benchcrowd` and removed afterwards with `[range 40 delete where Banker Name == benchcrowd` (the plan's `say:` entries can drive both). It is needed for the mobile/item update loops, ground-item baking and step 10.
- **The ClassicUO 2.0 beta** under the same window size, zoom and spot (cuo-2.0-perf-comparison.md).
- **After Uo.Render (step 8):** rerun the same plan; the FRAMESTATS lines diff directly.

Raw logs and the summariser scripts are in the session scratchpad; they are not part of the repo.

## Step 8: the GPU world renderer (2026-10-24)

Same machine, 1920×1080 client, game viewport maximised (1280×720 logical × 1.5), FPS uncapped, VSync off, inactive throttling off, `CUO_BENCH_TOPMOST=1`. Each spot ran Legacy then Gpu in the same session (`world:legacy;spot;world:gpu;spot`), 3 windows of 5 s each.

**Caveat: the display was off during these runs.** Presents stalled about 500 ms roughly once a second in both modes (this also happens on the login screen), so fps, frame avg and Present avg are not comparable with the baseline above. Compare the CPU phases and the frame p50.

#### JIT (ms avg; frame is p50)
| spot | mode | frame p50 | Update | world prepare | world draw | Draw total | FNA calls | FNA draws | world instances (dynamic) | overrides |
|---|---|---|---|---|---|---|---|---|---|---|
| britain | legacy | 0.554 | 0.014 | 0.105 | 0.122 | 0.352 | 0.117 | 89 | — | — |
| britain | gpu | 0.438 | 0.013 | 0.136 | 0.061 | 0.327 | 0.064 | 20 | 4169 (59) | 129 |
| bank | legacy | 0.522 | 0.014 | 0.086 | 0.116 | 0.325 | 0.114 | 88 | — | — |
| bank | gpu | 0.445 | 0.013 | 0.121 | 0.101 | 0.347 | 0.076 | 21 | 3673 (64) | 478 |
| zoomout | legacy | 1.460 | 0.020 | 0.634 | 0.568 | 1.331 | 0.318 | 326 | — | — |
| zoomout | gpu | 1.038 | 0.019 | 0.707 | 0.094 | 0.924 | 0.058 | 17 | 15014 (97) | 232 |
| zoomin | legacy | 0.459 | 0.013 | 0.040 | 0.058 | 0.217 | 0.085 | 49 | — | — |
| zoomin | gpu | 0.462 | 0.013 | 0.063 | 0.054 | 0.249 | 0.065 | 20 | 2208 (21) | 54 |
| walk | legacy | 0.537 | 0.020 | 0.100 | 0.128 | 0.360 | 0.122 | 90 | — | — |
| walk | gpu | 0.457 | 0.017 | 0.129 | 0.071 | 0.334 | 0.066 | 20 | 3397 (36) | 241 |
| travel | legacy | 0.548 | 0.019 | 0.100 | 0.136 | 0.372 | 0.122 | 89 | — | — |
| travel | gpu | 0.458 | 0.016 | 0.135 | 0.076 | 0.351 | 0.068 | 21 | 3588 (86) | 258 |
| night (0x1A) | legacy | 0.551 | 0.015 | 0.114 | 0.123 | 0.379 | 0.131 | 90 | — | — |
| night (0x1A) | gpu | 0.439 | 0.013 | 0.143 | 0.060 | 0.340 | 0.073 | 21 | 4167 (57) | 129 |

#### NativeAOT x86-64-v3
| spot | mode | frame p50 | Update | world prepare | world draw | Draw total | FNA calls | FNA draws | world instances (dynamic) | overrides |
|---|---|---|---|---|---|---|---|---|---|---|
| britain | legacy | 0.554 | 0.015 | 0.123 | 0.133 | 0.398 | 0.124 | 89 | — | — |
| britain | gpu | 0.468 | 0.015 | 0.159 | 0.065 | 0.367 | 0.064 | 20 | 4171 (61) | 129 |
| bank | legacy | 0.518 | 0.015 | 0.090 | 0.123 | 0.351 | 0.119 | 88 | — | — |
| bank | gpu | 0.439 | 0.014 | 0.131 | 0.081 | 0.350 | 0.061 | 20 | 3673 (64) | 478 |
| zoomout | legacy | 1.405 | 0.021 | 0.592 | 0.559 | 1.288 | 0.316 | 326 | — | — |
| zoomout | gpu | 1.032 | 0.017 | 0.692 | 0.086 | 0.904 | 0.054 | 17 | 15014 (96) | 232 |
| zoomin | legacy | 0.471 | 0.016 | 0.049 | 0.060 | 0.244 | 0.088 | 49 | — | — |
| zoomin | gpu | 0.443 | 0.015 | 0.069 | 0.058 | 0.273 | 0.066 | 20 | 2208 (21) | 54 |
| walk | legacy | 0.527 | 0.018 | 0.108 | 0.134 | 0.384 | 0.125 | 90 | — | — |
| walk | gpu | 0.464 | 0.017 | 0.143 | 0.072 | 0.360 | 0.066 | 19 | 3447 (38) | 226 |
| travel | legacy | 0.539 | 0.017 | 0.101 | 0.132 | 0.371 | 0.116 | 87 | — | — |
| travel | gpu | 0.451 | 0.016 | 0.138 | 0.072 | 0.357 | 0.066 | 21 | 3596 (91) | 232 |

#### 3840×2054 (JIT) and the 120-mobile crowd (JIT, bank, `[TileXYZ 1422 1692 12 10 0 Banker ...`, deleted afterwards)
| spot | mode | frame p50 | Update | world prepare | world draw | Draw total | FNA calls | FNA draws | world instances (dynamic) | overrides |
|---|---|---|---|---|---|---|---|---|---|---|
| 4K zoomout | legacy | 5.903 | 0.054 | 2.864 | 2.571 | 5.677 | 1.161 | 1042 | — | — |
| 4K zoomout | gpu | 3.497 | 0.034 | 2.958 | 0.189 | 3.371 | 0.131 | 20 | 45862 (399) | 718 |
| 4K britain | legacy | 1.279 | 0.017 | 0.335 | 0.394 | 0.952 | 0.289 | 230 | — | — |
| 4K britain | gpu | 1.216 | 0.018 | 0.404 | 0.112 | 0.735 | 0.129 | 23 | 11139 (89) | 240 |
| crowd | legacy | 0.622 | 0.020 | 0.100 | 0.275 | 0.507 | 0.139 | 94 | — | — |
| crowd | gpu | 0.613 | 0.019 | 0.295 | 0.090 | 0.513 | 0.061 | 26 | 4752 (1143) | 478 |
| crowd zoomout | legacy | 1.358 | 0.024 | 0.472 | 0.642 | 1.243 | 0.303 | 305 | — | — |
| crowd zoomout | gpu | 1.292 | 0.025 | 0.767 | 0.271 | 1.163 | 0.057 | 23 | 15806 (1173) | 3235 |

**Reading:**

- **World draw drops sharply:** 0.12 → 0.06 ms at Britain, 0.57 → 0.09 zoomed out, 2.57 → 0.19 at 4K zoomed out. FNA draw calls go from 89/326/1042 to about 20, and the world itself is 2 draws. "World draw" in Gpu mode is the FNA overlay (effects, handles, weather) plus `WorldRenderer.Prepare`/`Render` (about 0.05–0.08 ms).
- **World prepare goes up by 0.02–0.1 ms** (0.3 with 120 mobiles). The list fill is unchanged; on top come the override collection and the item/mobile views, which now run in prepare (capture) instead of in draw. ClassicUO's walk itself is the remaining cost: 0.5 ms zoomed out at 1080p and 2.9 ms at 4K. It is the next lever.
- **Draw total:** −7% at Britain, −31% zoomed out, −41% at 4K zoomed out; equal at zoom-in and in the crowd, where the views' own logic dominates. AOT tracks JIT, about 5–15% slower on the CPU phases, as before.
- **Roofs:** at the bank the player is under a roof, so roofs are hidden through overrides: 478 at zoom 1 and 3235 zoomed out (crowd zoomout world draw 0.27 ms, mostly override resolution).

## Step 9: parity work (2026-10-24)

Same conditions as step 8 (1920×1080, uncapped, VSync off, Legacy then Gpu in one session, 3 windows of 5 s, fresh default profile, circle of transparency off unless stated). Before is HEAD 88f455e4f, the mean of two runs; after is one run of the step 9 working tree. JIT, ms avg; frame is p50.

| spot | mode | frame p50 before → after | world prepare | world draw | Draw total | FNA draws | overrides |
|---|---|---|---|---|---|---|---|
| britain | legacy | 0.567 → 0.505 | 0.110 → 0.105 | 0.132 → 0.119 | 0.365 → 0.335 | 95 → 92 | — |
| bank | legacy | 0.527 → 0.485 | 0.087 → 0.076 | 0.126 → 0.116 | 0.339 → 0.298 | 93 → 91 | — |
| zoomout | legacy | 1.365 → 1.348 | 0.556 → 0.531 | 0.558 → 0.558 | 1.239 → 1.210 | 332 → 332 | — |
| britain | gpu | 0.485 → 0.505 | 0.156 → 0.152 | 0.086 → 0.059 | 0.374 → 0.335 | 26 → 23 | 129 → 71 |
| bank | gpu | 0.476 → 0.499 | 0.130 → 0.127 | 0.082 → 0.057 | 0.340 → 0.304 | 25 → 23 | 478 → 39 |
| zoomout | gpu | 0.931 → 0.903 | 0.608 → 0.605 | 0.090 → 0.078 | 0.820 → 0.802 | 24 → 23 | 232 → 182 |

The Legacy path is unchanged, so its rows show the run-to-run noise (about ±0.03 ms on frame p50).

With the circle of transparency on, at the bank's front wall (1435,1692):

| spot | mode | frame p50 | world prepare | world draw | Draw total | ring instances |
|---|---|---|---|---|---|---|
| zoom 1 | legacy | 0.510 | 0.090 | 0.150 | 0.353 | — |
| zoom 1 | gpu | 0.495 | 0.140 | 0.057 | 0.323 | 8 |
| zoom 2.5 | legacy | 1.284 | 0.420 | 0.625 | 1.165 | — |
| zoom 2.5 | gpu | 0.813 | 0.504 | 0.080 | 0.712 | 8 |

## W0: inside world prepare (2026-10-24)

FrameStats now splits world prepare (world-prepare-ecs-plan.md W0); the method is in [testing/world-prepare.md](testing/world-prepare.md). JIT Release, 1920×1080 client (3840×2054 maximised for the last column), FPS uncapped, VSync off, not topmost, display on, 2 windows of 5 s. Other agents' clients were running on the same machine, so absolute numbers are higher than the owner's AOT test (4K zoomed out: 5.2 ms here vs 3.6 ms there). Read the shares, not the absolutes.

#### Gpu mode, ms per frame (avg), ranked by the 4K share
| Sub-phase | Britain | Bank | Zoomed out | Zoomed out 3840×2054 | Share at 4K | Data shape |
|---|---|---|---|---|---|---|
| Tile walk (`AddTileToRenderList` over every tile) | 0.135 | 0.125 | 0.838 | 3.926 | 76% | Pointer chasing (linked tile lists): not vectorisable |
| Gpu hidden collect (`CollectGpuHidden`, a second pass over the same lists) | 0.056 | 0.054 | 0.247 | 0.874 | 17% | Pointer chasing |
| Rest (chunk loop, mesh dirty checks, `BeginFrame`, multi preview) | 0.008 | 0.008 | 0.046 | 0.253 | 5% | Mixed |
| Capture: statics drawn by their view (trees' shadows, season, animated) | 0.002 | 0.002 | 0.009 | 0.071 | 1.4% | Per-sprite view logic |
| Capture: items | 0.006 | 0.004 | 0.011 | 0.021 | 0.4% | Per-sprite view logic |
| Capture: mobiles | 0.005 | 0.017 | 0.008 | 0.008 | 0.2% | Per-sprite view logic |
| Text entities, setup, foliage pass | 0.005 | 0.006 | 0.008 | 0.018 | 0.3% | Small |
| **World prepare total** | **0.218** | **0.217** | **1.167** | **5.172** | | |
| Legacy world prepare, same session | 0.237 | 0.154 | 1.042 | 4.531 | | |
| Tiles / objects walked | 4608 / 8148 | 4608 / 7566 | 25600 / 41553 | 92416 / 140451 | | |
| Pick tests (`CheckMouseSelection`) | 2699 | 1523 | 12053 | 40527 | | |
| Override calls (`GpuOnDrawnSlow`) / overrides applied | 1540 / 71 | 321 / 39 | 5828 / 182 | 17492 / 487 | | |

#### Inside the tile walk (detail mode, corrected for its own timestamps)
Detail mode (`CUO_FRAMESTATS_PREP=1`) charges the walk to exclusive buckets and adds about 20 ns per bucket switch (calibrated: detail-on minus detail-off world prepare, divided by the switch count). After subtracting that:

| Bucket | Zoomed out 1080p | Zoomed out 4K | Data shape |
|---|---|---|---|
| Bare list walk: visit every object, no work (`BareWalk`, outside world prepare) | 0.49–0.64 | 2.2–2.4 | Pointer chasing: the floor of any per-tile walk |
| Baked land (incl. its traversal) | ~0.55 | ~2.6 | Pointer chasing |
| Baked statics (alpha rules, height cull) | ~0.25 | ~0.9 | Pointer chasing |
| Picking (pixel tests on every drawn object) | ~0.11 | ~0.28 | Branchy per object |
| Overrides (`GpuOnDrawnSlow`), lights, foliage, unmeshed, dynamic | < 0.05 | < 0.1 | Small |

**Reading:**
- **The walk is memory-bound.** Just visiting the 140k objects of a 4K zoomed-out view costs 2.2–2.4 ms, more than half the walk; the per-object rules are the smaller part. Skipping work per object isn't enough: the baked tiles must not be visited at all. That is what W1 does.
- **The second pass, the hidden collect, is the next 17%.** It exists only because the walk and the bake disagree at the edges; W1 removes it for skipped tiles.
- **Picking is about 7% at 4K** (40k pixel tests). Targeted picking (only the tiles under the mouse) removes it.
- **Captures are small on this shard** (60 items, 4 mobiles). They matter in the crowd (step 8: 0.3 ms with 120 mobiles) and are W2/W3 work.
- **SIMD:** nothing here is SIMD-friendly. The walk and hidden collect chase linked lists, and picking is branchy per object. W1 adds one arithmetic-bound loop (the per-chunk band masks, 0.05 ms at 4K): too small to vectorise. The packed arrays that would suit `Vector256` (screen positions, visibility) arrive with W2's ECS queries.

## W1: the Gpu world prepare stops walking baked land and statics (2026-10-24)

Setting `gpu_prepare_skip_baked` (default on), plus a season static remap baked by Uo.Render. Same conditions as W0; skipping off → on in one session, two alternations at the zoomed-out spots. JIT, ms avg; frame is p50.

| Spot | World prepare off → on | Draw total off → on | Frame p50 off → on | FPS off → on | Tiles walked off → on |
|---|---|---|---|---|---|
| Britain 1080p | 0.226 → 0.079 | 0.469 → 0.307 | 0.641 → 0.611 | 1068 → 1128 | 4608 → 119 |
| Bank 1080p (earlier build, no season remap) | 0.211 → 0.106 | 0.480 → 0.360 | 0.643 → 0.577 | 1077 → 1086 | 4608 → 374 |
| Zoomed out 1080p | 1.141 → 0.231 | 1.439 → 0.475 | 1.547 → 0.636 | 627 → 1086 | 25600 → 489 |
| Zoomed out 3840×2054 | 4.856 → 0.727 | 5.388 → 1.105 | 5.406 → 1.551 | 179 → 631 | 92416 → 1565 |
| Walk 1080p (earlier build; Present stalls from other clients made FPS noisy) | 0.331 → 0.173 | 0.660 → 0.491 | | | 4630 → 772 |

**Where the remaining 0.73 ms at 4K goes:**
- walk 0.32 ms over 1565 tiles: 904 tiles with trees, foliage, items and mobiles, plus 661 tiles with lights, translucent statics or the mouse;
- edge replay and hidden collect 0.18 ms (626 screen-edge tiles);
- rest 0.13, masks 0.05, captures 0.04.

The season remap also cut 4K captures (statics 0.061 → 0.015 ms) and world draw (0.23 → 0.12 ms), because Spring graphics no longer need a hidden override plus a captured sprite each.

**Parity:**
- **Screenshots, skipping off vs on in the same session:** Britain, the bank (under its roof), the bank's front wall with the circle of transparency on, Yew (foliage), and zoomed out at Britain and the bank. 0 pixels differ outside moving NPCs, animals, the player's idle frame and rain drops (I looked at every differing region). Light RTs are identical.
- **Base build (43dec9ae4) vs W1 with skipping on,** separate sessions: the differences are again only moving creatures.
- **`CUO_GPU_SKIP_CHECK=1`:** every 30th skipping frame it checks each skipped object (about 8 million per 5 s zoomed out): 0 unsettled, 0 drawn differently, at all spots and while walking.
- **Picking:**
  - `CUO_BENCH_PICK` picks the same static, land tile, ground item and player in Legacy and in Gpu with skipping on;
  - `pickscan:8x6`, an 8×6 grid of the view with skipping off then on at each point: 3 × 48 points, 0 different, at the bank zoomed out, the bank wall with the circle of transparency, and Britain.
