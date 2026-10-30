# What we can use from `modernuo/legacy-main` (Max Kellermann's loader work)

Branch reviewed: `modernuo/legacy-main` @ e64233adb, fork point d15dcbb7a (upstream, 2024-04-27). 155 commits by Max Kellermann (`git log --author="Max Kellermann" d15dcbb7a..modernuo/legacy-main`). Compared against `poc/modern-client` @ e23e9c27d (upstream ee79d7ebc plus our pack work). Read-only review: nothing was built, run or benchmarked. Numbers marked "est." are my estimates from code and file sizes; the others come from commit messages, our docs or a read-only parse of the local 7.0.103 client files.

## Summary

1. Max's branch does one thing at scale: it replaces stateful readers (`DataReader.Seek/Read`) with **stateless pointer access into memory-mapped files** (`PinnedBuffer`, `UOFileIndex.Data`), and it opens files lazily. Upstream went the other way: `FileReader` is a `BinaryReader` over an `UnmanagedMemoryStream`, with a seek position, which copies on every read and isn't thread-safe.
2. For art, texmaps, maps, anims, tiledata, hues, radarcol, multis and lights, **our pack replaces all of it**. Our pack readers already use the stateless, positional pattern (`RandomAccess.Read`, pooled buffers, worker decode).
3. What still runs on MUL/UOP at runtime: gumps, fonts, cliloc, sounds, speech, skills, professions, multimap and string_dictionary. Those loaders are upstream's newer code, and they have the weaknesses Max removed.
4. The concrete wins left in that runtime path: a **21 MB** unicode-font table allocated eagerly; **~246k `string.Format` calls** at startup to build three UOP indexes; gump decodes that copy every entry into fresh arrays (3–4 allocations per gump), and the read path that allows only main-thread decoding; cliloc read in 4 KB syscalls with 117,725 strings materialised eagerly.
5. Network: our `TcpSocketWrapper` has the exact bug Max fixed: a peer close is never noticed while the client is idle. It also reads **at most 4 KB per frame**, about 240 KB/s at 60 Hz VSync. Uo.Net's non-blocking drain loop is the better fix, not Max's `ReadAsync` thread.
6. For the pack runtime there's one small new item: `PackPages.DecodeChunk` opens and closes the page file for every chunk.
7. Rejected: mmap or raw pointers as our main read path (page faults land on the render thread, there are no bounds checks, there's a use-after-free risk, and the files get locked against patching). Also rejected: verdata, dif and UOP removal (the converter needs verdata for shards), compile-time feature flags, refactors and sealing.
8. Max's "300/600 MB saved" numbers are mapped virtual size, not resident memory. d15dcbb7a already memory-mapped every file (`#define USE_MMF`), so the real gain was opens and index parsing at startup.

## Themes

Verdicts: (a) adopt the idea in our pack loader or runtime, (b) port to the MUL/UOP side that still runs, (c) already covered, (d) not useful.

| # | Theme | Max commits (representative; full list below) | What upstream / we have now | Verdict | Benefit, with evidence | Target in our tree |
|---|---|---|---|---|---|---|
| 1 | **Stateless access into mapped files** (`PinnedBuffer`, entry carries an absolute pointer, `DataReader` removed from the class tree) | e4eda7273, eb27e1a39, a920ff154, 01a1929de, 072492e74, 0f68f0401, 1338501fc, 2bf512e34, 6aa00b5c4, 35840c9aa, 7b6aa69f8 | Upstream `UOFile : MMFileReader : FileReader` keeps a `BinaryReader` with a seek position: every read is a virtual call plus a memcpy, and it isn't thread-safe. The converter works around it with a `UOFileManager` per worker (`WorkerReaders`, ArtConvert.cs:13) and `RandomAccess` for anims. The pack path is already stateless (`RandomAccess.Read` + offsets). | **(b) as the idea, not the code.** Add a bounds-checked `ReadOnlySpan<byte>` view of an entry (the safe version of `UOFileIndex.Data`) for the loaders that still run. **(c)** for the pack. | Enables worker decode of gumps; removes one copy per read; lets the converter drop per-thread managers. No timing given in the commits. | `src/ClassicUO.IO/UOFile.cs`, `MMFileReader.cs`, `UOFileIndex.cs` |
| 2 | **Read straight from the mapping, no intermediate copy or reader overhead** | 8b83a09d5 (tiledata 180 → 100 ms), 576ee4c64 ("halves the time"), fcf81dfe9 (cliloc 240 → 52 ms), d38043fdc (radarcol, saves 160 kB), a564271bb / fe6e3bdea / c75e1ac30 (ASCII fonts), f6f83aed3 (UniFont), 06315ff8f (gumps), bb21e03d0 (sounds), dd7ba23e7 / 91c63a488 (anims), f8ae969d7 (map dif) | Tiledata, radarcol, anims, maps: **pack replaces them** (struct-span table reads). Cliloc: upstream now reads the whole file into a `byte[]`, but in 4 KB `Read` calls, and then parses spans. That gets most of the 240 → 52 ms win, but costs 920 syscalls for the 3.77 MB Cliloc.enu. Fonts: upstream copies every glyph into a managed array and eagerly allocates `FontCharacterDataUnicode[20, 0x10000]` (16 B each = **21 MB** LOH). Gumps: `new byte[entry.Length]` copy plus `int[h]` plus `uint[w·h]` per decode. | **(c)** tiledata, radarcol, anims, maps, lights. **(b)** fonts, gumps, cliloc (tasks 2, 4, 5). | Fonts −21 MB at startup (−18 MB or more if only 2–3 fonts are used). Gumps: 2–3 fewer arrays per first-sight gump, about 32 KB average copy avoided (144 MB / 4,456 entries). Cliloc: 920 → 1 read syscall. | `FontsLoader.cs`, `GumpsLoader.cs`, `ClilocLoader.cs` |
| 3 | **Span / stackalloc / `StackDataReader` instead of `DataReader`** | 0d7f79845, 394e193c3, a01eac889, 165b4446f, 1a70785ba, 38dcd556b, cb56e6516, 938c48f7e | Upstream already uses `StackDataReader` over spans in gumps, cliloc and speech. Pack readers use `StructIo` / `MemoryMarshal`. | **(c)** | — | — |
| 4 | **UOP index handling** | 38dcd556b (UOP table parse via `StackDataReader`), fa9b13755, 3bb379693 (ENABLE_UOP flag) | Both branches format a name string and hash it for every candidate index. In ours, `UOFileUop.FillEntries` builds `Math.Max(total, 65535) + 0x4000` = **81,919** entries, each with a `string.Format` + `CreateHash` + dictionary probe, for **gumps, sounds and string_dictionary** at startup (and art, anims and multis in the converter). The `hasExtra` path seeks to each entry to read width and height: in 7.0.103's gumpartLegacyMUL.uop that touches **3,542 distinct 4 KB pages** spread over the 144 MB file at startup (all 4,456 entries are flag 0, uncompressed, measured by parsing the table). `ClearHashes` is commented out, so the dictionary stays resident. | **(b)** our own fix, prompted by the review (task 3). Max's code has the same cost. | Est. 246k strings and about 20 MB of startup garbage removed (~100 B per string), est. 30–60 ms. Entry arrays sized to the real maximum (gumps 0x10000, sounds 0xFFFF) instead of 81,919 × ~40 B. No cold-cache faults across the gump UOP at startup. | `src/ClassicUO.IO/UOFileUop.cs`, `GumpsLoader.cs` |
| 5 | **Lazy / deferred opening** | 91489be5b (sounds, "600 MB"), 86db1e18b (anims, "600 MB"), 295e288db (map, "300 MB"), 6d9a35097 (multimap), cdeeed360 (dif files only when patching) | Anims and maps: pack, which is lazy per page and region and prefetched (**(c)**). Our `UOFileManager.Load` runs **sequentially** (upstream dropped the old `Task.WhenAll`) and loads sounds (UOP parse, Sound.def, music Config.txt), multimap (7 mappings plus `Directory.GetFiles` + Regex) and professions eagerly. The memory figures in the commits are mapped size; d15dcbb7a already memory-mapped every file. | **(c)** anims, maps. **(b)** sounds, multimap, professions (task 6), after measuring. | Startup only: est. a few to tens of ms, fewer mappings. The MB claims don't carry over (virtual, not resident). | `UOFileManager.cs`, `SoundsLoader.cs`, `MultiMapLoader.cs`, `ProfessionLoader.cs` |
| 6 | **Lifetime / Dispose correctness** | 880d7a92f (fonts use-after-free: mappings collected while pointers were in use), 5e3f6eaa1, d61356af9, 62eafaf5f, 03f7ee476, aebf6ba73, 790d696d9, 86f00de40, 9a204d59b, 0358cf787 | Upstream holds the `UOFile`s (`_unicodeFontAddress` is `UOFile[]`), so the crash can't happen; no raw pointers leave `ClassicUO.IO`. The local `fonts` (fonts.mul) in `FontsLoader.Load` is never disposed; its handle and mapping live until finalisation. | **(c)** for the crash. A lesson for task 1: a span view must never outlive its loader. Small **(b)**: dispose fonts.mul after the ASCII copy (inside task 2). | Correctness. | `FontsLoader.cs` |
| 7 | **Verdata / dif / old clients / UltimaLive / compile-time flags** | c090ebf9e, d331a9452, 87f3c3b08, 3bb379693, 92371d81e, d5463e744, 65e5da4b3, bc70e60f7, 1552dbc90, 1b7166f72 | The pack spec never reads patches at runtime (shards merge first). The converter **deliberately applies verdata to anims** (tools/ClassicUO.Assets.Mul/AnimationsLoader.cs:375) and widens pre-7.0.9 tiledata. | **(c)** runtime, **(d)** converter | — | — |
| 8 | **Struct layout / readonly / overlays** | b842efab0, b34d1c3f8, a419965c3, 9fb3cdf10, plus overlay structs in 7b6aa69f8 (`RawIndexEntry`), fcf81dfe9 (`ClilocRecordHeader`), GumpBlock, UniFontCharHeader | The pack is specified as packed, blittable structs read with `StructIo` / `MemoryMarshal`. | **(c)** | — | — |
| 9 | **Allocation removal in hot paths** | 06315ff8f (gumps), f6f83aed3 / a564271bb (glyphs from the mapping), bb21e03d0 (sound) | Gump first sight decodes on the main thread with 3–4 allocations (`Gump.GetGump` → `GumpsLoader.GetGump`, plus a full-image `PixelPicker.Set`). Glyphs: one `byte[]` per unicode glyph; a glyph with no data is re-read from the file on every lookup (no negative cache). Sound: one `byte[]` per play (Max's copies too). | **(b)** gumps and glyphs (tasks 2, 4). Sound: **(d)**, not hot. | See theme 2. No gump-hitch measurement exists yet. | `src/ClassicUO.Renderer/Gumps/Gump.cs`, `GumpsLoader.cs`, `FontsLoader.cs` |
| 10 | **Async I/O, network** | 8f6cc37f9 (call `OnDisconnected` when the socket is already closed), e64233adb (`ReadAsync` loop on a task so a peer close is seen) | `TcpSocketWrapper.Read` has the old code: `if (!IsConnected) return 0;` without raising disconnect, and it reads only `min(4096, Available)`. A FIN gives Available = 0, so a server close is never seen while the client is idle (the stuck "Verifying Account" case in e64233adb). `NetClient._compressedBuffer` is 4096 bytes and `CollectAvailableData` runs once per frame, so receive is capped at 4 KB per frame. Uo.Net (`ClassicClient.Poll`) already does it right: non-blocking `Receive` until `WouldBlock`, and `n == 0` means closed. | **(b)** port Uo.Net's drain loop and close detection into `TcpSocketWrapper` / `NetClient` (task 1). **(d)** for the `ReadAsync` design. | Functional: disconnects are detected. Throughput cap removed: 4 KB × 60 = ~240 KB/s compressed at VSync 60 Hz (est.; not measured). Idle syscalls stay at about 2 per frame either way. | `src/ClassicUO.Client/Network/Socket/TcpSocketWrapper.cs`, `NetClient.cs`, `GameController.cs:501` |
| 11 | **Caching** | none (Max removed copies; he added no caches) | Pack: chunk LRU, palette LRU, region LRU (done). | **(c)** | — | — |
| 12 | **Texture upload** | none | — | n/a | — | — |
| 13 | **Micro-optimisations in loaders the pack replaced** | 57d2933b5, fee371761, b0ab50b62 (lights), 0fbeb9a90 (hues), 717d59950 | Pack replaces lights and hues. | **(c)** | — | — |
| 14 | **Pure refactor / cleanup** | 451131664 … 4e5bf062a (UOFileLoader API), 7f9217b62, a03f14473, 935b653e1, sealing (8914f844e, 22d444822, 7abf5d5b2, 242a3a1d8, 5ce004815), dead code (29d7a83fd, 5bdb3b7ff, 62ad9d237, eb58abdd0, 99a83ae13, b5133f121, 20104734e, 038b92fe5, 8577445cc, 34121f45b, a73cab77f, 3d2d36781) | Upstream rewrote the loader hierarchy (instance-based `UOFileManager`, no singletons). | **(d)** churn | — | — |

andreakarasho's commits on the branch don't touch file loading. The one near it, 6e377de74 "gump: read the whole chunk", changes `StackDataReader`; upstream already has the equivalent. The others are gameplay and rendering fixes, also in newer upstream.

### Pack-runtime check (new, found while comparing)

- `PackAssets.cs` `PackPages.DecodeChunk` calls `File.OpenHandle(PackConst.PagePath(_dir, page))` for **every chunk decode**, then closes it: CreateFile + CloseHandle per chunk, filtered by antivirus on Windows. `AnimationsLoader` already keeps `_paletteFile` open, which is the right pattern. Max's equivalent: open once, keep it (UOFile lifetime). → task 7, verdict (a).
- Pack tiledata: `TileDataLoader.Load` turns every name in `tiledata_ext.bin` into a `string` at startup (land 16,384 plus all statics). The name table is already UTF-8 with offsets, so names could decode on first use. → task 8 (optional), verdict (a).

## Proposed tasks

Each item is bounded and independent unless noted. "Measure first" means taking the number before changing code, with the existing FrameStats and the log line `Files loaded in: N ms`.

1. **TCP receive: drain until WouldBlock and detect peer close** (Sonnet)
   - Goal: port Uo.Net's `Poll` loop into ClassicUO's TCP path, so a server close raises `Disconnected` and one frame can take more than 4 KB.
   - Where: `TcpSocketWrapper.Read` (non-blocking `Receive`; `0` → `InvokeOnDisconnected`; errors → `InvokeOnError`), `NetClient.CollectAvailableData` (loop in 4 KB slices, since Huffman output is capped at the 64 KB `_uncompressedBuffer`), and the call at `GameController.cs:501`.
   - Win: fixes the hang Max describes in e64233adb. Removes the ~240 KB/s cap at VSync 60 Hz (est.).
   - Risk: the `packets` phase grows during bursts. Cap each frame (for example 64 KB or 1 ms) and keep the remainder in the socket. Plugins (Razor) must still see every packet once, in order. Measure first: log `Available` after `[go` into the large-scale scene with VSync on.
2. **Unicode fonts: no 21 MB table; dispose fonts.mul** (Sonnet)
   - Goal: allocate a font's 65,536-glyph row on its first use (`FontCharacterDataUnicode[20][]`); cache "no glyph" so missing glyphs aren't re-read; dispose the `fonts` UOFileMul after the ASCII copy.
   - Where: `FontsLoader.cs`: the two indexers at lines 223 and 256, `Load` at 115–210.
   - Win: −21 MB LOH at startup (16 B × 20 × 65,536), about 1 MB per font actually used; one mapping and handle fewer.
   - Risk: low; only two indexers. Optional, not recommended now: read glyphs from a span view (Max's `UniFont`), but only with bounds checks (his code indexes `table[c]` unchecked).
3. **UOP index: no per-index strings; right-sized entries; lazy gump extras** (Sonnet)
   - Goal: `CreateHash(ReadOnlySpan<char>)` over a `stackalloc` formatted name; size `Entries` to the caller's maximum instead of `max(total, 65535) + 0x4000`; special-case constant patterns (string_dictionary); for `hasExtra`, store the extra's offset and read width and height on first decode.
   - Where: `src/ClassicUO.IO/UOFileUop.cs`, `GumpsLoader.GetGump` (and `gump.def` aliasing, which copies entries before their extras are read).
   - Win (est.): ~246k strings and about 20 MB of startup garbage removed, 30–60 ms; about 4 MB smaller entry arrays; no startup faults over 3,542 pages of the 144 MB gump UOP (cold cache: real disk reads). The converter gets the same benefit for art, anims and multis.
   - Risk: hash parity. Add a unit test that compares old and new hashes for every index of every pattern in use, and that aliased gumps still get the right size and hue.
4. **Gump decode without copies; reads safe for workers** (Sonnet for 4a; Opus for 4b)
   - 4a. Goal: decode a gump from a bounds-checked span of its entry (the uncompressed MUL/UOP case, which is all 4,456 entries in 7.0.103), keep zlib/BWT for flagged entries, and use pooled row-lookup, decompress and pixel buffers (the atlas copies the pixels). Also switch `Gump.GetGump` to `PixelPicker.SetTrimmed` / `VisibleBounds`, as art already does.
     - Where: `GumpsLoader.cs`, `Gump.cs`, plus the span accessor on `UOFile` (theme 1).
     - Win: 3–4 allocations per first-sight gump (an average 32 KB copy plus `w·h·4`) → 0 once warm.
     - Risk: low; a parity test on every gump id against the current decoder.
   - 4b. Goal: prefetch a gump's art on a worker when its packet (0xB0/0xDD) or its gump class is created, using the `FirstSightDecoder` pattern. Not async on demand: gump controls take their size from the decoded image.
     - Win: removes main-thread decode from gump opens.
     - Risk: medium; layout depends on sizes. Measure first: gump-open frames (paperdoll, spellbook, bank) have no FrameStats numbers yet. **Skip 4b if gumps move into the pack soon**; the same prefetch would then target pack pages.
5. **Cliloc: one read; lazy strings** (Sonnet)
   - Goal: read Cliloc.enu with one `RandomAccess.Read` (now 920 × 4 KB calls), and keep the UTF-8 blob plus a number → (offset, length) index, decoding a string on first `GetString` (cache it; ours-file overrides stay as strings).
   - Where: `ClilocLoader.cs` (`ReadCliloc`, `GetString`).
   - Win: 117,725 strings no longer made at startup: est. 8–9 MB of strings plus about 3 MB of dictionary, and their `string.Intern` cost. 920 → 1 syscall.
   - Risk: thread safety of the cache (plugins can call it from other threads), so use a concurrent cache. Callers that compare by reference (interning) need checking. This is also the natural shape for a future pack cliloc table.
6. **Startup: measure, then lazy-load sounds, multimap and professions** (Sonnet)
   - Goal: time each loader in `UOFileManager.Load`. Then open sounds (UOP, Sound.def, music config) on first use or on a background task after login, open multimap and its facet files when the world map first opens (Max's 91489be5b, 6d9a35097), and load professions at character creation.
   - Where: `UOFileManager.cs`, `SoundsLoader.cs`, `MultiMapLoader.cs`, `ProfessionLoader.cs`.
   - Win: est. a few to tens of ms of startup, and 8+ fewer mappings; exact numbers come from the measurement. Do task 3 first: it removes most of the sounds cost.
   - Risk: low. The first sound must not hitch, so prefer a background task after login over loading on first use.
7. **Pack pages: keep file handles open** (Sonnet)
   - Goal: a bounded handle cache (for example the 64 most recently used `SafeFileHandle`s) in `PackPages` instead of `File.OpenHandle` per chunk decode.
   - Where: `src/ClassicUO.Assets/PackAssets.cs` (`DecodeChunk`, `GetPage`); check Uo.Render's `PageResidency` for the same pattern.
   - Win: 2 syscalls and an AV-filtered open per chunk (est. 20–100 µs, about 1–3% of a 2.6 ms chunk decode; more on machines with heavy AV).
   - Risk: low. Open handles block replacing page files while the client runs (patching happens before launch). Close everything on pack dispose.
8. **Optional: lazy tile names from the pack name table** (Sonnet)
   - Goal: keep `tiledata_ext.bin`'s UTF-8 table and decode `Name` on first access.
   - Where: `TileDataLoader.cs` and the callers of `LandTiles.Name` / `StaticTiles.Name`.
   - Win: est. 80k strings (a few MB) and a few ms at startup.
   - Risk: an API change on two structs used widely; check the hot callers first. Low priority.
9. **Optional: span views in the converter** (Sonnet, after 4a)
   - Goal: use the span accessor from 4a in `tools/ClassicUO.Assets.Mul`, so `WorkerReaders` (a `UOFileManager` per thread) can go.
   - Win: simpler code, less per-thread memory. The converter already runs in about 4 s, so the time win is small.
   - Risk: low.

Suggested order: 1, 2, 3, 7 (small and independent), then 5 and 6, then 4a. 4b and 8 only if measurements justify them and gumps aren't moving into the pack first.

## Deliberately rejected

- **mmap plus raw pointers as the main read path (`PinnedBuffer` everywhere).**
  - Faults on mapped pages happen wherever a pointer is dereferenced, often on the render thread, and can't be budgeted the way our worker `RandomAccess` reads are.
  - Max's readers trust file data: no bounds checks in GumpsLoader, UniFont (`table[c]`) or cliloc. 880d7a92f shows the use-after-free class this invites.
  - On Windows, mapped files can't be replaced, which hurts patchers.
  - Pack pages and regions are Brotli-compressed, so a mapping saves nothing over one positional read.
  - Kept only as a safe span view for the legacy MUL loaders (task 4a).
- **Lazy loading of anims and maps, the map-dif and verdata code paths, and the map loader rework** (295e288db, 86db1e18b, cdeeed360, f8ae969d7 and others). The pack replaces them. The 300/600 MB claims are mapped virtual size.
- **Removing verdata and old-client support, and the ENABLE_UOP / dif / UOAssist compile-time flags.** The converter needs verdata for shards and widens old tiledata on purpose. Compile flags fork the build and test matrix for no runtime gain once the pack is the only art path.
- **`NetClient` `ReadAsync` as written** (e64233adb): an extra task, a lock and an extra copy per receive. Uo.Net's non-blocking drain is simpler and already proven. Only the close detection is taken (task 1).
- **Pointer-based tiledata, hues, radarcol, lights, multis, texmaps and art loaders** (8b83a09d5 180 → 100 ms, 576ee4c64, d38043fdc 160 kB, the lights commits): the pack replaces these loaders.
- **Loader-hierarchy refactors, sealing, the Dispose pattern rewrite, UltimaLive deletion and dead-code cleanups:** upstream restructured differently, so porting is churn without a measurable win.
- **Sound byte copy per play:** not a hot path; Max's version copies too.

## Commit groups (all 155)

- Stateless access / DataReader → PinnedBuffer (theme 1): 282d69221 86b7226e4 c4cc3e879 4e5bf062a 153b78878 773d66e3b 889dee456 fa9b13755 33bbbd5dd da48da929 29a7286e2 71e338fd9 dfa48f217 7f9217b62 a03f14473 ead7988c3 935b653e1 c65d4df45 27f940f4e 147565dd3 0973c0351 0b8aea85a 572fcfeeb 2d8769534 619ff4a13 65f9297a4 653eff2b1 1f0375b96 94c939b75 8ea559c0a 35840c9aa 66211b89b 072492e74 06315ff8f fcc24b804 995b9c420 362723d8a c773f99ab bb21e03d0 440f1077c 362464f2c 2bf512e34 29d76bdc2 6aa00b5c4 717d59950 1338501fc 0f68f0401 d74bf5d9b 34121f45b a73cab77f 3d2d36781 e4eda7273 a920ff154 eb27e1a39 80ec34413 a01eac889 01a1929de
- Direct reads, copies removed (theme 2): 7b6aa69f8 576ee4c64 8b83a09d5 d38043fdc fcf81dfe9 1961050c2 a564271bb 9fb3cdf10 c75e1ac30 fe6e3bdea 950c0d925 74c3db396 938c7059b dd7ba23e7 91c63a488 f8ae969d7 938c48f7e cb56e6516
- StackDataReader / spans (theme 3): 0d7f79845 394e193c3 165b4446f 1a70785ba 38dcd556b
- Lazy loading (theme 5): 91489be5b 17ae43075 86db1e18b b6d078b83 295e288db e31f19b9e b4f9a0223 6d9a35097 c55f9dd91 e1dbe51cc 36025b1ce 65f9f89ae cdeeed360
- Lifetime / Dispose (theme 6): 880d7a92f 5e3f6eaa1 d61356af9 5ae3398df 790d696d9 62eafaf5f b7a42004c 03f7ee476 11d854113 aebf6ba73 cbc57a4cf 9a204d59b a426436c3 99181b69b 86f00de40 0358cf787 6cda6614e
- Verdata / UOP / dif / old clients / flags (theme 7): c090ebf9e d331a9452 87f3c3b08 3bb379693 92371d81e d5463e744 65e5da4b3 bc70e60f7 1552dbc90 1b7166f72 b6d6f2bdb
- Struct layout / sealing / encapsulation (themes 8, 14): b842efab0 b34d1c3f8 8914f844e a419965c3 22d444822 5ccea8481 97fc4d5fd f6f83aed3 7abf5d5b2 242a3a1d8 0d6ba70e6 5ce004815 6caffdcdc 22400065c 0fbeb9a90
- Lights and tiledata micro (theme 13): 57d2933b5 fee371761 b0ab50b62 8577445cc
- Network (theme 10): 8f6cc37f9 e64233adb
- Cleanup (theme 14): 451131664 a871434ef 4c3277c7f 29d7a83fd 5bdb3b7ff 62ad9d237 eb58abdd0 5dcda55a9 99a83ae13 b5133f121 14f6cee33 20104734e 038b92fe5
