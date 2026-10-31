# Art, texmaps and animations as per-graphic records: evaluation (2026-10-31)

Owner's question: should art (and texmaps) and animations ship as per-graphic records (art: a record per graphic id; anims: a record per body, or per body+action for large bodies), packed into the GPU texture-array pages at runtime, or as today's pre-packed atlas pages? Performance may not get worse, so the numbers decide.

Branch `poc/records-eval` (base `poc/modern-client` 2ced75119). **Measurement PoC: nothing here merges as is, and pack format 1.0 is unchanged.** Records stores, the runtime packer and the client switch exist only on this branch.

## Summary

- **Size.** Records are smaller than pages on disk: art 53.0 MiB vs 66.0 MiB of pages (−20%), texmaps 13.8 vs 16.2 MiB (−15%), anims 609.5 vs 628.0 MiB (−3%). Pages compress whole 1 MiB chunks of a page that holds gutters, mips (art) and empty space; records compress only the trimmed texels.
- **Parity.** The runtime packer replays the converter's packing over the records in their stored order, so the client gets the same sprite tables and **bit-identical pages**: all 31 art, 4 texmap and 2,652 anim pages rebuilt from records equal the page files byte for byte, mip chains included, and every sprite-table and frame-table entry is identical. The GPU input is therefore identical by construction, and the client screenshots confirm it (bench-000037, below).
- **Cost of a change with pages (Part A).** A same-size change rewrites only the pages the graphic is on (art: 1 page, 2.8 MiB; a 26-page dragon: 4.5 MiB). A size change is luck: in art one more row or a new id fitted a hole (2–3 files, 1.7–6.8 MiB), but one more row on one anim body repacked 2,600 of 2,652 anim pages (641 of 675 MiB). Stable incremental packing in the converter would bound every case to 1–2 pages plus the table, with no runtime change.
- **Runtime cost of records (offline, one thread unless stated).** Art: building a 2048² page from records with its 6-level mip chain takes 34 ms (p95 46) on one thread and 11.5 ms (p95 15) on 4 threads, against 23 ms (p95 31) for reading and decoding today's Kind 2 page on one thread (≈4 ms with the client's 8 decoder threads). Anims: a 1024² Indexed8 page from records 2.3 ms (p95 4.2, max 7.7) against 2.7 ms (p95 3.6, max 5.7) from the page file. Startup packing: art 48 ms, texmaps 1 ms, anims 0.47–0.72 s (951,693 distinct frames).
- **Client (bench-000037/38/39).** Screenshots match except for server-driven journal text and snow; no sprite, texmap or mip pixel differs. Pinned timing (39): frame p50/p95/p99 and world prepare are equal within noise in every scene; layers and uploads are identical; records add no per-frame CPU work. Costs: +≈0.5 s startup (anim packing), +50–80 MiB commit and +70–110 MiB private working set per client, art page latency 33 vs 14 ms average (45 vs 26 ms max).
- **Recommendation: (b) keep pre-packed pages with stable packing.** Records match pages at runtime but are never faster (the GPU sees the same pages), and they cost startup time, memory and art page latency. Their benefit, small per-change patches, comes from stable packing plus CDC instead.

## Method

### Records (Part B.1)

`Uo.Converter --records-from <pack> --out <dir> [--rec-small n] [--rec-unit-cap n] [--rec-unit-max n] [--rec-only art,tex,anims]` (`poc/Uo.Converter/RecordsTool.cs`) takes the texels of every sprite and frame from an existing pack's pages (they are bit-exact to the client data, pack-format "Verification") and writes record stores (`Uo.Pack.RecordStore`, `poc/Uo.Pack/RecordAtlas.cs`):

| File | Slots | Records | Payload |
|---|---|---|---|
| `art/records.bin` (`ARTR`), `tex/records.bin` (`TEXR`) | one per sprite id; `arg` = `SpriteFlags` | one per distinct trimmed image (aliases and identical images share), `meta0/1` = W/H; the last record is a geometry table (trimX, trimY, origW, origH per record; its index in `StoreHeader.flags`); a W = H = 0 record is an existing, fully transparent image | W·H B5G5R5A1 texels, exactly the page's mip0 texels |
| `anim/records.bin` (`ANRC`) | one per unit | a unit = consecutive distinct frames of one body; a body over 1 MiB raw is split per action, and every unit is capped at 256 KiB raw (`--rec-unit-cap 262144`); `meta0/1` = first distinct frame, `meta2` = frame count | the frames' Indexed8 indices back to back |
| `anim/framerec.bin` (`ANRF`) | | | u32 per `AnimFrameEntry`: its distinct frame (0xFFFFFFFF = empty) |

`anim/anim.bin` is written with page/x/y zeroed (the runtime fills them); `palettes.bin` and `animdata.bin` are unchanged. Codec per record as gumps: uncompressed up to `--rec-small` raw bytes, else Brotli (q9, window 22) when smaller.

**Runtime packing.** `SpriteRecordAtlas` / `AnimRecordAtlas` read the record tables, run the converter's packer (sequential skyline, the current page then the previous one, 2-texel gutter, 1-texel alignment; a copy of `SkylinePacker` as `RtSkyline`) over the records in record order and fill the sprite table (art, tex) or the frame table's page/x/y (anims). A page is built by decoding its records, blitting them with extruded gutters and, for art and texmaps, making mips 1–5 exactly as the converter does (ClassicUO's 5→8 table, `AtlasBuilder.BuildMips`, the 5-bit rounding of `PageCodec.EncodeB5G5R5A1`).

**Client** (`CUO_RECORD_ATLAS=1` and `art/records.bin` in the pack; default off):
- `PackAssets` opens the art and texmap stores and packs them; `ArtLoader` / `TexmapsLoader` take their tables from the packer; `AnimationsLoader` opens the anim store, which fills `_frames` in place.
- GPU: `PageResidency.Builder` (`IPageBuilder`, `poc/Uo.Render/PageResidency.cs`): a page job builds the page into the mapped staging buffer instead of reading `page_NNNN.bin`. Everything else is today's: logical page → layer table, LRU eviction, upload budget, shaders (`RecordPageBuilders.cs` in the client). Art pages build with `CUO_RECORD_ATLAS_THREADS` threads (default 4): records decoded and blitted in parallel (cells never overlap), mips in row bands.
- CPU reads (`PackPages.ReadRect`: picking masks, Legacy decode, UI art) read the sprite's record directly: one positional read plus a decode (art median 5–16 µs, p95 42 µs; anim units through an 8-unit LRU), no 1 MiB chunk decode.
- The FrameStats `GPU world:` line gains page job counts, run time, latency (enqueue → loaded) and main-thread upload time for the art and anim residencies, in both modes.

This is the "same layout" variant: the runtime places every sprite exactly where the converter would, so the pages, the layers they occupy and the GPU work are the same as today, and the records cost only CPU at load and at page build. A variant that packs only the sprites in use ("on demand") would fill fewer layers, but sprite positions would then depend on play order: chunk instance buffers bake page/x/y today, so on-demand placement needs either a placement at bake time with incremental sub-rect uploads, or an indirection table in the shader. Not built; see "Open questions".

### Part A: cost of a change with today's pages

`Uo.Converter` env hooks (`poc/Uo.Converter/Perturb.cs`) change one graphic as a shard would: `CUO_CONV_PERTURB_ART=recolor:<id>|grow:<id>|add:<newId>:<srcId>`, `CUO_CONV_PERTURB_ANIM=recolor:<body>|grow:<body>` (recolor: same size, other texels; grow: one more row; add: a new id with another sprite's recoloured texels). Each case re-runs the stage (`--only art` or `--only anims`) on a working copy of the CURRENT pack and compares every page file and table with an unperturbed run of the same converter (which is byte-identical to CURRENT: 0 of 32 art files differ). Script: scratch `records-eval/part-a.sh`.

Stage times (12 threads, machine shared with other agents' bench jobs): the art stage 1.5–2.0 s plus 1.5 s verify; the anim stage 35–37 s plus 22 s verify. Both unperturbed runs are byte-identical to CURRENT (0 of 32 art files, 0 of 2,655 anim files differ). Verify fails for the recolor/grow cases by design (the pack no longer matches the client files).

| Case | Files changed | Bytes a file-hash patcher fetches | Pages |
|---|---|---|---|
| Art: recolor static 0x0E75 (same size) | 1 of 32 | 2.84 of 67.6 MiB | page 4 only; sprite table unchanged |
| Art: grow 0x0E75 by one row | 3 of 32 | 6.80 MiB | pages 3–4 + `sprites.bin` (the extra row still fitted; the "previous page" rule moved one later sprite) |
| Art: add a new static 0x7000 (id 0xB000, mid-range) | 2 of 32 | 3.15 MiB | page 17 + `sprites.bin` (it fitted a hole; nothing after it moved) |
| Art: add a new static at the end (0xEF80) | 2 of 32 | 1.70 MiB | the last page + `sprites.bin` |
| Anim: recolor body 12 (dragon, 26 pages; same sizes) | 26 of 2,655 | 4.47 of 675.3 MiB | its own pages 53–78 |
| Anim: grow body 12's frames by one row | **2,601 of 2,656** | **641 of 675 MiB** | every page from 53 on (2,600 pages) + `anim.bin` (one more page in total) |

- A same-size change touches only the pages the graphic is on. A size change or an insertion is cheap only when the new cell happens to fit the skyline where the old one was: art had room in these cases, anims did not, and one row more on one body repacked 98% of the anim pack. Sequential packing has no guarantee either way.
- **Records:** any of these changes is one record (art: a few KiB; anim: the body's units, ≤ 256 KiB raw each) plus nothing else, since the layout is recomputed at runtime. But a store is one file: a patcher that compares file hashes (the manifest's sha256) fetches the whole store (53 MiB art, 606 MiB anims) unless it patches by binary delta, or the store is split, or the manifest lists record hashes.
- **Stable incremental packing (estimate, not built):** keep every unchanged sprite's page and position, put a grown or new sprite into a hole of its old page or onto the last (open) page, and leave freed cells empty until a full repack. Every case above would then change 1–2 page files plus the table: art 1.7–5.7 MiB, the anim grow ≈0.5 MiB (two 1024² pages of ≈0.24 MiB) instead of 641 MiB. The cost is slowly decaying occupancy (holes), recovered by an occasional full repack (a major patch). This keeps today's runtime path unchanged.

## Sizes (Part B.1, 7.0.103, `--rec-small 1024 --rec-unit-cap 262144`)

| Content | Pages today | Records | Raw texels | Notes |
|---|---|---|---|---|
| Art | 31 pages, 66.0 MiB (330.6 MiB raw chains) | `art/records.bin` 53.0 MiB, 34,850 records | 183.3 MiB | all Brotli 50.4 MiB; ≤ 1 KiB raw 51.3; ≤ 4 KiB raw 81.1 (small art compresses well, so the gump threshold doesn't carry over) |
| Texmaps | 4 pages, 16.2 MiB | `tex/records.bin` 13.8 MiB, 2,365 records | 25.6 MiB | every record is over 1 KiB, so all Brotli |
| Anims | 2,652 pages, 628.0 MiB | `anim/records.bin` 605.7 MiB (13,624 units) + `framerec.bin` 3.8 MiB | 1,893 MiB | per body / body+action without the 256 KiB cap: 8,324 units, 590.9 MiB, but the largest unit is 10 MiB raw |

Per unit raw size with the cap: median 147 KiB, p95 255 KiB. Without the cap (body, or body+action over 1 MiB): median 70 KiB, p95 902 KiB, max 10,061 KiB; a page then decodes up to 10 MiB to use a slice of it (page build p95 9.5 ms, max 55 ms against 4.2 / 7.7 ms with the cap).

## Page build cost (offline, i9-12900K, 12 converter threads busy only in the parallel steps; machine shared with other agents' jobs)

| Page | From records | From today's page file (read + Kind 2 decode) |
|---|---|---|
| Art 2048², 6 mips | 1 thread: 34.0 ms (p95 46.1); mip0 only 23.0 (p95 33.4); 4 threads: 11.5 (p95 14.8) | 1 thread: 23.0 ms (p95 31.2); client: 8 decoder threads, ≈4 ms (pack-format.md) |
| Texmap 2048², 6 mips | 1 thread: 59.4 ms; 4 threads: 17.5 ms | 1 thread: 47.9 ms |
| Anim 1024² Indexed8 | 1 thread: 2.3 ms (p95 4.2, max 7.7) | 1 thread: 2.7 ms (p95 3.6, max 5.7) |
| One art record (CPU picking, UI) | 5–16 µs median, p95 42 µs | a 1 MiB chunk decode (the chunks a sprite's rows touch) |

Startup packing (client thread, once): art 48 ms, texmaps 1 ms, anims 474–720 ms.

## Client measurements (Part B.3)

Same JIT binary (`DOTNET_TieredCompilation=0`), pages = the CURRENT pack, records = the scratch records pack with `CUO_RECORD_ATLAS=1`, interleaved.

| Job | What | Read |
|---|---|---|
| bench-000037 `records-parity` | one run each with `CUO_BENCH_SHOTS` at Britain and a quiet spot (zoom 1 and 2.5, so mips are sampled) and the large-scale scene; `shotdiff.py` compares the PNGs | `out.log`: the `[records]` lines and the per-image pixel diff. Static spots should be 0; the scene has moving creatures |
| bench-000038 `records-perf-pr`, bench-000039 `records-perf-rp` | 2 × (pages, records) each, opposite order; plan: Britain stand, walk, zoom 2.5; large-scale scene zoom 1, 2.5, lane walk; `ltravel` (cold towns); `bodies` (≈390 first-seen bodies) | per run `<name>.log` (FrameStats windows; `GPU world:` lines with page job stats), `<name>.mem.csv` (private WS, commit), `<name>.regions.json` (at 60 s) |

Analysis scripts (scratch, `records-eval/tools/perf.py`, `diffviz.py`): per plan tag, the mean over the tag's FrameStats windows of each window's p50/p95/p99; per arm the mean of its 2 runs and the within-arm spread (max−min)/min; a difference counts only when it is larger than twice the larger within-arm spread (noise rule). Page-job and layer figures come from the cumulative `GPU world:` line at the end of each tag (differences between tags); memory from `<run>.mem.csv` at the end of each tag.

### Parity (bench-000037): pass

Both runs logged `[renderer] gpu: Gpu (SDL_GPU vulkan, RTX 3090 Ti)`; the records run logged `[records] art 34851 records -> 31 pages (29.7 ms), tex 2366 -> 4 pages (0.6 ms)` and `anim 13624 units -> 2652 pages (517.1 ms)`, and no exception. Of 20 PNGs, 14 are identical to the pixel (quiet, quietzo and ls: world, lights, UI and full screen; brit: world and lights; britzo: lights). The 6 that differ were each inspected as a crop (pages / records / diff mask):

| Image | Pixels | What differs |
|---|---|---|
| `brit_gpu_ui`, `brit_gpu_screen` | 2,840 / 7,700 | journal text "You are now under the protection of the town guards." is on screen in the records run only (message timing) |
| `britzo_gpu_ui`, `britzo_gpu_screen` (text part) | 884 / ≈2,350 | journal text "It begins to snow." in the records run only |
| `britzo_gpu`, `britzo_gpu_screen` (world part) | 99 / ≈600 | the server started snow between the runs: all 99 world pixels are pure white (255,255,255) single dots spread evenly over roofs, ground and grass, none in the pages image. No sprite, tile or mip texel differs |

Every difference is server-driven (journal timing, weather); no pixel of any sprite, texmap or mip level differs, as expected from the offline bit-identity of the pages. Caveat: the large-scale scene (`ls`, 5156 2150) was empty in all three jobs (4 items, 1 mobile in the FrameStats counts: the server-side spawn was absent), so `ls_*`/`lsw` measure an almost empty view, and mobiles were not compared in a crowd.

### Timing: bench-000039 (records, pages, records, pages; pinned to P-cores, High), used for conclusions

Frame ms (p50 / p95 / p99) and world prepare ms (p50 / p95), mean of 2 runs per arm, within-arm spread in brackets.

| Tag | Pages frame p50 / p95 / p99 | Records frame p50 / p95 / p99 | Pages prepare p50 / p95 | Records prepare p50 / p95 |
|---|---|---|---|---|
| brit | 0.483 (0.7%) / 0.735 (3.6%) / 5.13 (1.0%) | 0.483 (1.5%) / 0.707 (0.5%) / 5.15 (1.4%) | 0.049 (2.4%) / 0.085 (1.7%) | 0.049 (0.0%) / 0.081 (1.4%) |
| bwalk | 0.478 (0.3%) / 0.712 (1.9%) / 5.11 (1.3%) | 0.478 (0.9%) / 0.697 (1.4%) / 5.15 (0.6%) | 0.051 (0.7%) / 0.139 (2.9%) | 0.051 (3.0%) / 0.140 (4.4%) |
| britzo (2.5) | 0.487 (4.4%) / 0.687 (0.6%) / 4.73 (2.4%) | 0.476 (2.6%) / 0.696 (1.5%) / 4.84 (1.2%) | 0.183 (1.6%) / 0.258 (7.4%) | 0.181 (1.2%) / 0.252 (7.1%) |
| ls_z1 | 0.464 (0.3%) / 0.757 (0.2%) / 5.19 (0.0%) | 0.464 (1.0%) / 0.742 (2.9%) / 5.16 (1.6%) | 0.025 (1.2%) / 0.049 (4.1%) | 0.025 (0.0%) / 0.042 (0.2%) |
| ls_zo (2.5) | 0.468 (0.1%) / 0.661 (0.6%) / 5.06 (0.6%) | 0.463 (1.2%) / 0.653 (7.1%) / 5.06 (0.9%) | 0.091 (1.7%) / 0.133 (12.9%) | 0.089 (0.0%) / 0.139 (2.2%) |
| lsw | 0.470 (1.5%) / 0.755 (0.1%) / 5.15 (0.8%) | 0.464 (1.1%) / 0.745 (5.7%) / 5.17 (1.7%) | 0.025 (1.9%) / 0.048 (18.3%) | 0.025 (0.0%) / 0.051 (7.9%) |
| lt (cold towns) | 0.477 (0.8%) / 0.709 (0.9%) / 5.07 (1.1%) | 0.474 (1.2%) / 0.755 (18.9%) / 5.13 (2.0%) | 0.047 (1.4%) / 0.153 (2.5%) | 0.046 (2.5%) / 0.153 (2.5%) |
| bodies (≈390 new) | 0.477 (0.6%) / 0.715 (0.5%) / 5.12 (0.0%) | 0.476 (1.8%) / 0.742 (5.4%) / 4.44 (33%) | 0.043 (0.2%) / 0.077 (0.2%) | 0.043 (0.5%) / 0.071 (19.7%) |

- Noise rule: every arm is within 10% on frame p50 (max 4.4%) and world prepare p50 (max 3.0%), and within 25% on p95 (max 18.9%), so the job is usable.
- **No frame-time or world-prepare difference passes the rule** except two world-prepare p95 values that favour records by 0.004 ms (brit) and 0.007 ms (ls_z1). No page job runs in those windows, so both are noise that happened to fall outside tight spreads. Frame p99 ≈5 ms in both arms is a `present` wait (draw p99 ≈0.3 ms), not CPU work.
- Hitches over 16 ms: the walk (`bwalk`) has 1–3 per run with a work max of 25–28 ms in both arms (pages 3 and 2, records 1 and 1). That is the known walk hitch; no page job runs in it. One records run has a single 166 ms frame at britzo with no work hitch (a `present` stall). No other tag has a work hitch over 8 ms in either arm. First-entry work max in `lt`: 2.7 / 2.7 ms (pages) vs 6.9 / 2.7 ms (records); in `bodies`: 6.4 / 6.3 vs 1.8 / 5.1 ms.

### Timing: bench-000038 (pages, records, pages, records; unpinned)

| Tag | Pages frame p50 / p95 / p99 | Records frame p50 / p95 / p99 | Pages prepare p50 / p95 | Records prepare p50 / p95 |
|---|---|---|---|---|
| brit | 0.463 (11.9%) / 0.749 (22.9%) / 3.13 (389%) | 0.470 (4.2%) / 0.726 (1.8%) / 4.34 (52%) | 0.052 (6.3%) / 0.084 (28.7%) | 0.051 (4.1%) / 0.079 (14.7%) |
| bwalk | 0.460 (10.1%) / 0.758 (6.6%) / 3.37 (220%) | 0.486 (0.3%) / 0.792 (14.7%) / 5.13 (0.6%) | 0.056 (0.8%) / 0.154 (4.4%) | 0.054 (3.4%) / 0.150 (1.5%) |
| britzo | 0.459 (5.1%) / 0.737 (34.6%) / 3.46 (640%) | 0.496 (0.5%) / 0.860 (11.6%) / 4.81 (0.9%) | 0.185 (2.2%) / 0.279 (35.6%) | 0.187 (0.3%) / 0.274 (0.2%) |
| ls_z1 | 0.387 (4.4%) / 0.650 (5.3%) / 1.92 (247%) | 0.468 (0.0%) / 0.883 (28.5%) / 5.23 (0.8%) | 0.025 (3.6%) / 0.033 (1.0%) | 0.026 (5.8%) / 0.045 (63%) |
| ls_zo | 0.377 (1.7%) / 0.599 (16.1%) / 1.11 (97%) | 0.469 (0.7%) / 0.700 (7.8%) / 5.08 (0.7%) | 0.093 (0.0%) / 0.136 (7.3%) | 0.092 (0.8%) / 0.138 (25.2%) |
| lsw | 0.400 (2.3%) / 0.650 (11.2%) / 1.69 (246%) | 0.470 (1.0%) / 0.782 (0.1%) / 5.20 (0.8%) | 0.025 (0.9%) / 0.037 (11.1%) | 0.026 (3.7%) / 0.049 (37.5%) |
| lt | 0.412 (0.7%) / 0.673 (0.1%) / 0.92 (9.5%) | 0.478 (0.5%) / 0.761 (7.8%) / 5.11 (0.0%) | 0.048 (6.2%) / 0.149 (1.2%) | 0.048 (4.6%) / 0.160 (1.0%) |
| bodies | 0.407 (2.0%) / 0.685 (0.1%) / 0.91 (6.4%) | 0.480 (0.1%) / 0.959 (0.8%) / 5.15 (0.8%) | 0.044 (2.7%) / 0.057 (7.5%) | 0.044 (1.6%) / 0.079 (3.5%) |

- **Fails the noise rule**: pages frame p50 spread 11.9% at brit and 10.1% at bwalk; p95 spread 34.6% (britzo pages) and 28.5% (ls_z1 records); world prepare p95 up to 63%. Not used for conclusions.
- Its apparent records penalty (frame p50 +16–25% and p99 0.9 → 5.1 ms from `ls_z1` on) is entirely in `present`. From the large-scale scene on, the pages runs dropped into a regime without the ≈5 ms present wait (present p99 0.6–0.7 ms, ≈35k frames per tag instead of ≈24k); the records runs did not. Draw p99 (0.27–0.43 ms) and world prepare p50 are equal across arms, and those windows run no page jobs (0 art jobs after `bwalk`, 0 anim jobs until `bodies`), so records do no CPU work there. Under pinning (39) both arms sit in the ≈5 ms regime with equal numbers. The regime switch is unexplained (unpinned scheduling, or the GPU/compositor). If it is ever suspected to depend on the mode, the one deciding run is an unpinned A/A (pages vs pages) next to an unpinned A/B.

### Page jobs, CPU cost of runtime packing, GPU layers (39; 38 agrees)

| | Pages | Records |
|---|---|---|
| Art page jobs at login (Britain) | 9 (+1 on the walk) | 9 (+1) |
| Art job run, avg per page (wall) | 11–12 ms (read + Kind 2 decode, 8 decoder threads) | **31–33 ms** (build from records, 4 threads) |
| Art job latency (enqueue → loaded) avg / max | 13.7–15.4 / 24.9–26.3 ms | **31.5–33.0 / 43.9–45.6 ms** |
| Art worker time for the 10 login pages | 99–108 ms | 280–281 ms |
| Anim page jobs in `bodies` | 257 / 257 (774 / 807 ms run, 3.0–3.1 ms each) | 256 / 257 (786 / 849 ms, 3.1–3.3 ms each) |
| Anim job latency avg / max (whole run) | 4.3–4.5 / 24.9–26.3 ms | 3.8–4.5 / 18.6–19.4 ms |
| Main-thread upload | art 2.0–2.1 ms over 9; anim 1.1–1.3 ms over ≈260 | the same: 2.0–2.4 ms; 1.1–1.2 ms |
| Page jobs in steady windows (brit after login, britzo, ls_*, lsw, lt) | 0 art, 0–2 anim | 0 art, 0–1 anim |
| GPU layers at the end of the tags | art 9→10 of 24; anim 4–6 of 128, then 128 of 128 in `bodies` with 133–135 evictions (≈262 uploads) | identical: art 10 of 24; anim 128 of 128, 132–133 evictions (≈261 uploads) |

- **CPU of runtime packing per frame: zero in steady state.** No page job runs, and main-thread upload is identical. It is a one-off worker cost: +≈180 ms of worker wall time for the 10 art pages at login (+20 ms per art page, 2.3× the latency); anim pages are at parity (+3% run total, within the 8% records spread). Averaged over the `bodies` windows, anim jobs take ≈0.034 ms of worker time per frame in both arms. The art cost recurs only when an art page is evicted and reloaded (31 pages for 24 layers); `ltravel` loaded no new art page, so this was not exercised after login.
- Layers are identical by construction (same layout); the records variant cannot use fewer layers without on-demand placement (Open questions 2).
- Load (`[load]` total): pages 167–169 ms (39) / 177–180 ms (38); records 648–692 ms / 669–692 ms. That is **+≈500 ms startup**, of which anim packing takes 486–519 ms (`[records] anim`) and art + texmap packing 32–35 ms. GC heap after load: 148 MB vs 76 MB; allocated during load: 224 MB vs 96 MB.

### Memory per client (39; 38 in brackets)

| Point | Pages private WS / commit (MiB) | Records private WS / commit (MiB) | Records − pages |
|---|---|---|---|
| End of `brit` | 799 / 1,937 | 908 / 2,018 | **+109 / +81** (+122 / +55) |
| End of `lt` | 908 / 2,106 | 991 / 2,169 | +83 / +63 (+95 / +53) |
| End of `bodies` | 946 / 2,145 | 1,016 / 2,195 | +70 / +50 (+88 / +48) |
| Peak over the run | 946 / 2,152 | 1,018 / 2,222 | +72 / +70 (+85 / +58) |
| Regions at 60 s, private | 816 / 1,959 | 890 / 2,025 | +74 / +66 (+98 / +71) |

Records cost **≈+50–80 MiB commit and +70–110 MiB private working set per client**. Most of it is the managed anim runtime tables (GC heap after load +72 MB, near the ≈50 MB table estimate plus packer state), plus the art build scratch while art pages are built. With four clients that is ≈+300 MiB of commit for no runtime gain. Mapped and image regions are equal.

## Recommendation: (b) keep pre-packed pages, with stable packing

1. **Runtime performance is equal, not better.** With pinning (39), no frame p50/p95/p99 or world-prepare figure differs beyond twice the within-arm spread, and the GPU layout, layers and uploads are identical by construction. Records add no per-frame CPU work, and in this variant they cannot be faster at runtime: the GPU sees the same pages.
2. **Every cost is on the records side.** +≈0.5 s startup per launch (anim packing), +50–80 MiB commit per client (≈+300 MiB for four), and 2.3× the art page latency (33 vs 14 ms average, 45 vs 26 ms max) at login and on every art page reload. None of these is a frame stall today, because page jobs run on workers, but each is a regression the owner asked not to accept. The anim packing cost has only one cure, caching the packed table, and that is today's pre-packed table.
3. **What records were for is delivered by the converter.** Records shrink a change to one record. The pipeline is now MUL/UOP → source → pack with stable packing (an unchanged sprite keeps its page and position, so a change touches 1–2 pages plus the table; Part A) and CDC patching (only the changed chunks of a large file move). Records' smaller disk size (−3% anims, −20% art) is the one benefit left, and it is not runtime performance.

Not decided by this measurement, and only relevant if records were reconsidered: on-demand packing into fewer layers (needs bake-time placement or a shader indirection; not built), the art page reload rate under eviction (`ltravel` touched no new art page), and a populated large-scale scene (the spawn was absent).

## Open questions

1. **Patching granularity.** A record store is one file: a manifest-sha patcher downloads the whole 600 MiB anim store for one changed body unless it does a binary delta or the store is split (per body range, or the manifest lists record hashes). Pages with stable packing change 1–2 files.
2. **On-demand packing** (only sprites in use, fewer layers) is not measured: it needs bake-time placement or a sprite indirection in the shader (see Method).
3. Anim startup packing (0.5–0.7 s for 951k frames) could be cached per pack (a runtime-written table cache), which is today's pre-packed table again.
