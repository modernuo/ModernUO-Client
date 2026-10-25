# ClassicUO FPS test: Legacy vs Gpu world renderer

Run 2026-10-24 20:16, 4.8 min. NativeAOT build: poc/modern-client 0684ab8d1, published 2026-10-24 10:16, NativeAOT win-x64 x86-64-v3.

## Machine

- CPU: 12th Gen Intel(R) Core(TM) i9-12900K (16 cores, 24 threads), load at start 23%
- RAM: 64 GB; OS: Microsoft Windows 11 Pro build 26300; power plan: High performance
- GPU: Intel(R) UHD Graphics 770, driver 32.0.101.7088, display 3840x2160 @ 60 Hz
- GPU: NVIDIA GeForce RTX 3090 Ti, driver 32.0.16.1742, display 3840x2160 @ 143 Hz
- Client: device SDL_GPU vulkan (SPIRV), DPI scale 1.50; FPS uncapped, VSync off, inactive throttling off, window on top
- Server 127.0.0.1:2593, pack C:\Repositories\uo-packs\classic-1.0, 3 windows of 5 s per spot

## Summary

| Spot | Client area | Legacy FPS | Gpu FPS | FPS change | Legacy frame p50 (ms) | Gpu frame p50 (ms) | Legacy Draw (ms) | Gpu Draw (ms) |
|---|---|---|---|---|---|---|---|---|
| Britain | 1920x1080 | 1234 | 1348 | +9% (better) | 0.586 | 0.523 | 0.450 | 0.413 |
| Britain bank | 1920x1080 | 1298 | 1388 | +7% (better) | 0.555 | 0.517 | 0.412 | 0.387 |
| Zoomed out | 1920x1080 | 539 | 772 | +43% (better) | 1.756 | 1.212 | 1.662 | 1.155 |
| Walk | 1920x1080 | 1243 | 1375 | +11% (better) | 0.576 | 0.517 | 0.448 | 0.385 |
| Zoomed out, large window | 3840x2054 | 148 | 232 | +57% (better) | 6.725 | 4.217 | 6.463 | 4.102 |

## Per spot

### Britain (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 1234 | 1348 | +9% (better) |
| Frame avg (ms) | 0.811 | 0.742 | -8% (better) |
| Frame p50 (ms) | 0.586 | 0.523 | -11% (better) |
| Frame p95 (ms) | 2.134 | 1.547 | -28% (better) |
| Frame p99 (ms) | 4.824 | 4.915 | +2% (same) |
| Update (ms avg) | 0.025 | 0.024 | -4% (better) |
| World prepare (ms avg) | 0.148 | 0.182 | +23% (worse) |
| World draw (ms avg) | 0.145 | 0.076 | -48% (better) |
| Draw total (ms avg) | 0.450 | 0.413 | -8% (better) |
| Present (ms avg) | 0.330 | 0.299 | -10% (better) |
| Present p50 / p99 (ms) | 0.129 / 4.421 | 0.097 / 4.531 | |
| Allocated per frame (KB) | 17.5 | 17.5 | 0% (same) |
| GC count gen0/1/2 | 21/1/0 | 23/2/0 | |
| GC pause (ms per s) | 0.57 | 0.62 | +9% (worse) |
| FNA draw calls | 94 | 25 |  |

### Britain bank (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 1298 | 1388 | +7% (better) |
| Frame avg (ms) | 0.770 | 0.720 | -7% (better) |
| Frame p50 (ms) | 0.555 | 0.517 | -7% (better) |
| Frame p95 (ms) | 1.646 | 1.360 | -17% (better) |
| Frame p99 (ms) | 4.926 | 4.901 | -1% (same) |
| Update (ms avg) | 0.024 | 0.023 | -3% (better) |
| World prepare (ms avg) | 0.114 | 0.153 | +35% (worse) |
| World draw (ms avg) | 0.143 | 0.078 | -46% (better) |
| Draw total (ms avg) | 0.412 | 0.387 | -6% (better) |
| Present (ms avg) | 0.328 | 0.305 | -7% (better) |
| Present p50 / p99 (ms) | 0.125 / 4.543 | 0.101 / 4.544 | |
| Allocated per frame (KB) | 17.5 | 17.7 | +2% (same) |
| GC count gen0/1/2 | 22/2/0 | 25/3/1 | |
| GC pause (ms per s) | 0.59 | 0.75 | +25% (worse) |
| FNA draw calls | 93 | 25 |  |

### Zoomed out (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 539 | 772 | +43% (better) |
| Frame avg (ms) | 1.856 | 1.295 | -30% (better) |
| Frame p50 (ms) | 1.756 | 1.212 | -31% (better) |
| Frame p95 (ms) | 2.542 | 1.840 | -28% (better) |
| Frame p99 (ms) | 3.624 | 2.486 | -31% (better) |
| Update (ms avg) | 0.035 | 0.028 | -18% (better) |
| World prepare (ms avg) | 0.855 | 0.876 | +2% (same) |
| World draw (ms avg) | 0.636 | 0.120 | -81% (better) |
| Draw total (ms avg) | 1.662 | 1.155 | -31% (better) |
| Present (ms avg) | 0.151 | 0.105 | -30% (better) |
| Present p50 / p99 (ms) | 0.139 / 0.359 | 0.096 / 0.245 | |
| Allocated per frame (KB) | 17.5 | 17.5 | 0% (same) |
| GC count gen0/1/2 | 10/1/1 | 13/1/0 | |
| GC pause (ms per s) | 0.29 | 0.34 | +17% (worse) |
| FNA draw calls | 334 | 25 |  |

### Walk (1920x1080)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 1243 | 1375 | +11% (better) |
| Frame avg (ms) | 0.805 | 0.728 | -10% (better) |
| Frame p50 (ms) | 0.576 | 0.517 | -10% (better) |
| Frame p95 (ms) | 1.935 | 1.170 | -40% (better) |
| Frame p99 (ms) | 4.842 | 4.889 | +1% (same) |
| Update (ms avg) | 0.030 | 0.036 | +22% (worse) |
| World prepare (ms avg) | 0.136 | 0.162 | +19% (worse) |
| World draw (ms avg) | 0.150 | 0.068 | -54% (better) |
| Draw total (ms avg) | 0.448 | 0.385 | -14% (better) |
| Present (ms avg) | 0.321 | 0.301 | -6% (better) |
| Present p50 / p99 (ms) | 0.123 / 4.433 | 0.097 / 4.529 | |
| Allocated per frame (KB) | 18.2 | 18.8 | +3% (worse) |
| GC count gen0/1/2 | 22/5/0 | 25/7/0 | |
| GC pause (ms per s) | 1.17 | 0.95 | -19% (better) |
| FNA draw calls | 95 | 25 |  |

### Zoomed out, large window (3840x2054)

| Metric | Legacy | Gpu | Gpu vs Legacy |
|---|---|---|---|
| FPS (avg) | 148 | 232 | +57% (better) |
| Frame avg (ms) | 6.767 | 4.314 | -36% (better) |
| Frame p50 (ms) | 6.725 | 4.217 | -37% (better) |
| Frame p95 (ms) | 7.517 | 4.832 | -36% (better) |
| Frame p99 (ms) | 8.240 | 6.359 | -23% (better) |
| Update (ms avg) | 0.058 | 0.045 | -22% (better) |
| World prepare (ms avg) | 3.436 | 3.614 | +5% (worse) |
| World draw (ms avg) | 2.731 | 0.209 | -92% (better) |
| Draw total (ms avg) | 6.463 | 4.102 | -37% (better) |
| Present (ms avg) | 0.231 | 0.154 | -33% (better) |
| Present p50 / p99 (ms) | 0.228 / 0.305 | 0.121 / 1.547 | |
| Allocated per frame (KB) | 17.5 | 18.3 | +5% (worse) |
| GC count gen0/1/2 | 3/1/0 | 4/0/0 | |
| GC pause (ms per s) | 0.19 | 0.11 | -41% (better) |
| FNA draw calls | 1048 | 26 |  |

Notes: frame and phase times are averages of the 5-s windows (p50/p95/p99 are per-window percentiles, averaged). World draw in Gpu mode includes WorldRenderer.Prepare/Render plus the FNA overlay. Present is FNA3D SwapBuffers; above about 1,000 fps it is bimodal (back-pressure), so compare CPU phases and frame p50 as well as FPS. Allocations are for all threads; GC counts are totals over the spot.
