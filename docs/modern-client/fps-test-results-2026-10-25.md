# ClassicUO FPS test: Legacy vs Gpu world renderer

Run 2026-10-25 10:26, 4.8 min. NativeAOT build: poc/modern-client 072ae4d88, published 2026-10-25 10:15, NativeAOT win-x64 x86-64-v3, SDL3 modernuo modernuo/3.4.18-patched 27ac7d26b.

## Compared with 2026-10-24 (build 0684ab8d1)

Added by the main session. Same machine, spots and script.

| Spot | Gpu FPS 10-24 → 10-25 | Gpu frame p50 (ms) | Legacy FPS 10-24 → 10-25 | Legacy frame p50 (ms) |
|---|---|---|---|---|
| Britain | 1348 → 1538 (+14%) | 0.523 → 0.466 | 1234 → 1192 (−3%) | 0.586 → 0.672 |
| Britain bank | 1388 → 1527 (+10%) | 0.517 → 0.491 | 1298 → 1273 (−2%) | 0.555 → 0.608 |
| Zoomed out | 772 → 1395 (+81%) | 1.212 → 0.542 | 539 → 412 (−24%) | 1.756 → 2.379 |
| Walk | 1375 → 1506 (+10%) | 0.517 → 0.484 | 1243 → 1213 (−2%) | 0.576 → 0.657 |
| Zoomed out, large window | 232 → 710 (×3.1) | 4.217 → 1.297 | 148 → 106 (−28%) | 6.725 → 9.406 |

- **Gpu:** the gains come from W1/W4 (world prepare zoomed out 0.876 → 0.207 ms, large window 3.614 → 0.730 ms), W3, and the large-scale fixes. Gpu p99 at 1080p (4.4–5.2 ms) is Present back-pressure above 1,000 fps, as on 10-24. Zoomed out now runs in that regime too, which is why its p99 rose from 2.5 to 4.4 ms.
- **Legacy regressed** in its CPU phases (zoomed out: world prepare 0.855 → 1.064 ms, world draw 0.636 → 1.004 ms; large window: 3.436 → 4.633 and 2.731 → 4.126 ms) with the same number of draw calls. Legacy is developer-only, but its batcher also draws the UI in Gpu mode. A bisect was started 2026-10-25.
- This build doesn't include W5 (786d86c9c).

## Machine

- CPU: 12th Gen Intel(R) Core(TM) i9-12900K (16 cores, 24 threads), load at start 3%
- RAM: 64 GB; OS: Microsoft Windows 11 Pro build 26300; power plan: High performance
- GPU: Intel(R) UHD Graphics 770, driver 32.0.101.7088, display 3840x2160 @ 60 Hz
- GPU: NVIDIA GeForce RTX 3090 Ti, driver 32.0.16.1742, display 3840x2160 @ 143 Hz
- Client: device SDL_GPU vulkan (SPIRV), DPI scale 1.50; FPS uncapped, VSync off, inactive throttling off, window on top
- Server 127.0.0.1:2593, pack C:\Repositories\uo-packs\classic-1.0, 3 windows of 5 s per spot

## Summary

| Spot | Client area | Legacy FPS | Gpu FPS | FPS change | Legacy frame p50 (ms) | Gpu frame p50 (ms) | Legacy Draw (ms) | Gpu Draw (ms) |
|---|---|---|---|---|---|---|---|---|
| Britain | 1920x1080 | 1192 | 1538 | +29% (better) | 0.672 | 0.466 | 0.573 | 0.278 |
| Britain bank | 1920x1080 | 1273 | 1527 | +20% (better) | 0.608 | 0.491 | 0.514 | 0.302 |
| Zoomed out | 1920x1080 | 412 | 1395 | +239% (better) | 2.379 | 0.542 | 2.245 | 0.448 |
| Walk | 1920x1080 | 1213 | 1506 | +24% (better) | 0.657 | 0.484 | 0.549 | 0.301 |
| Zoomed out, large window | 3840x2054 | 106 | 710 | +571% (better) | 9.406 | 1.297 | 9.079 | 1.122 |

## Per spot

### Britain (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 1192 | 1538 | +29% (better) |
| Frame avg (ms) | 0.839 | 0.650 | -23% (better) |
| Frame p50 (ms) | 0.672 | 0.466 | -31% (better) |
| Frame p95 (ms) | 2.049 | 0.739 | -64% (better) |
| Frame p99 (ms) | 4.185 | 5.211 | +25% (worse) |
| Update (ms avg) | 0.017 | 0.017 | +1% (same) |
| World prepare (ms avg) | 0.170 | 0.056 | -67% (better) |
| World draw (ms avg) | 0.238 | 0.057 | -76% (better) |
| Draw total (ms avg) | 0.573 | 0.278 | -52% (better) |
| Present (ms avg) | 0.242 | 0.349 | +44% (worse) |
| Present p50 / p99 (ms) | 0.090 / 3.650 | 0.136 / 4.951 | |
| Allocated per frame (KB) | 0.3 | 0.2 | -3% (better) |
| GC count gen0/1/2 | 0/0/0 | 2/2/1 | |
| GC pause (ms per s) | 0.00 | 0.49 |  |
| FNA draw calls | 87 | 18 |  |

### Britain bank (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 1273 | 1527 | +20% (better) |
| Frame avg (ms) | 0.785 | 0.655 | -17% (better) |
| Frame p50 (ms) | 0.608 | 0.491 | -19% (better) |
| Frame p95 (ms) | 1.124 | 0.775 | -31% (better) |
| Frame p99 (ms) | 4.362 | 5.086 | +17% (worse) |
| Update (ms avg) | 0.016 | 0.018 | +11% (worse) |
| World prepare (ms avg) | 0.133 | 0.065 | -51% (better) |
| World draw (ms avg) | 0.221 | 0.062 | -72% (better) |
| Draw total (ms avg) | 0.514 | 0.302 | -41% (better) |
| Present (ms avg) | 0.249 | 0.328 | +32% (worse) |
| Present p50 / p99 (ms) | 0.087 / 3.880 | 0.131 / 4.809 | |
| Allocated per frame (KB) | 0.1 | 0.1 | +48% (worse) |
| GC count gen0/1/2 | 0/0/0 | 1/0/0 | |
| GC pause (ms per s) | 0.00 | 0.17 |  |
| FNA draw calls | 86 | 18 |  |

### Zoomed out (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 412 | 1395 | +239% (better) |
| Frame avg (ms) | 2.426 | 0.717 | -70% (better) |
| Frame p50 (ms) | 2.379 | 0.542 | -77% (better) |
| Frame p95 (ms) | 2.748 | 0.890 | -68% (better) |
| Frame p99 (ms) | 3.038 | 4.436 | +46% (worse) |
| Update (ms avg) | 0.028 | 0.018 | -37% (better) |
| World prepare (ms avg) | 1.064 | 0.207 | -81% (better) |
| World draw (ms avg) | 1.004 | 0.075 | -93% (better) |
| Draw total (ms avg) | 2.245 | 0.448 | -80% (better) |
| Present (ms avg) | 0.143 | 0.244 | +71% (worse) |
| Present p50 / p99 (ms) | 0.140 / 0.196 | 0.084 / 4.009 | |
| Allocated per frame (KB) | 0.2 | 0.2 | +24% (worse) |
| GC count gen0/1/2 | 0/0/0 | 0/0/0 | |
| GC pause (ms per s) | 0.00 | 0.00 |  |
| FNA draw calls | 327 | 18 |  |

### Walk (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 1213 | 1506 | +24% (better) |
| Frame avg (ms) | 0.824 | 0.664 | -19% (better) |
| Frame p50 (ms) | 0.657 | 0.484 | -26% (better) |
| Frame p95 (ms) | 1.523 | 0.762 | -50% (better) |
| Frame p99 (ms) | 4.141 | 5.150 | +24% (worse) |
| Update (ms avg) | 0.021 | 0.026 | +22% (worse) |
| World prepare (ms avg) | 0.155 | 0.066 | -57% (better) |
| World draw (ms avg) | 0.229 | 0.063 | -73% (better) |
| Draw total (ms avg) | 0.549 | 0.301 | -45% (better) |
| Present (ms avg) | 0.247 | 0.331 | +34% (worse) |
| Present p50 / p99 (ms) | 0.092 / 3.618 | 0.111 / 4.877 | |
| Allocated per frame (KB) | 0.9 | 1.4 | +65% (worse) |
| GC count gen0/1/2 | 1/1/0 | 2/1/0 | |
| GC pause (ms per s) | 0.23 | 0.23 | -3% (better) |
| FNA draw calls | 88 | 18 |  |

### Zoomed out, large window (3840x2054)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 106 | 710 | +571% (better) |
| Frame avg (ms) | 9.447 | 1.408 | -85% (better) |
| Frame p50 (ms) | 9.406 | 1.297 | -86% (better) |
| Frame p95 (ms) | 10.233 | 2.163 | -79% (better) |
| Frame p99 (ms) | 10.756 | 2.443 | -77% (better) |
| Update (ms avg) | 0.059 | 0.022 | -63% (better) |
| World prepare (ms avg) | 4.633 | 0.730 | -84% (better) |
| World draw (ms avg) | 4.126 | 0.107 | -97% (better) |
| Draw total (ms avg) | 9.079 | 1.122 | -88% (better) |
| Present (ms avg) | 0.293 | 0.256 | -12% (better) |
| Present p50 / p99 (ms) | 0.263 / 0.952 | 0.102 / 1.377 | |
| Allocated per frame (KB) | 0.2 | 0.2 | +27% (worse) |
| GC count gen0/1/2 | 0/0/0 | 0/0/0 | |
| GC pause (ms per s) | 0.00 | 0.00 |  |
| FNA draw calls | 1042 | 19 |  |

Notes: frame and phase times are averages of the 5-s windows (p50/p95/p99 are per-window percentiles, averaged). World draw in Gpu mode includes WorldRenderer.Prepare/Render plus the FNA overlay. Present is FNA3D SwapBuffers; above about 1,000 fps it is bimodal (back-pressure), so compare CPU phases and frame p50 as well as FPS. Allocations are for all threads; GC counts are totals over the spot.
