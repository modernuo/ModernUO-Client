# Buffer pools

Pooled arrays come from one place: `src/Uo.Buffers` (no dependencies; Uo.Pack, Uo.Render and ClassicUO.Utility reference it,
so every project sees it). Do not call `ArrayPool<T>.Shared` or keep a private free list for byte, char or pixel buffers.

## Families

`BufferPool` picks the pool by size and by whether the array leaves its thread.

| Call | Size | Pool | Use |
|---|---|---|---|
| `Rent<T>` / `Return<T>` | up to 64 KiB | the calling thread's `STArrayPool<T>` | scoped scratch rented and returned on one thread; ref structs |
| `RentShared<T>` / `ReturnShared<T>` | up to 64 KiB | `ArrayPool<T>.Shared` | arrays another thread returns (worker decodes, main returns; code after an `await`) |
| either family | above 64 KiB | `LargeBufferPool` | everything LOH-sized, any thread |

Rules:

1. An array that crosses threads uses the shared family. Return with the call that matches the rent (`Rent` with `Return`,
   `RentShared` with `ReturnShared`); the return routes by length, so a caller does not track the size class.
2. A per-thread pool never holds an array above 64 KiB, so its retention per thread stays small (27 buckets of up to 33
   arrays, each below the LOH).
3. `LargeBufferPool` keeps free buffers per use (`LargeUse`), in four size classes per octave, at most 32 per class; a return
   over its use's cap evicts that use's least recently returned buffer. It does not cap what is rented out (decode budgets
   and caches do that). The client derives the caps at startup (`LargePoolCaps`, logged as `[bufpools] caps`):
   - `General` (scoped scratch, worker decode outputs): DecodeBudget.Shared (16 MiB), plus DecodeBudget.Gumps (32 MiB) with
     `gump_decode_budget` on, plus 1 MiB of compressed input per scratch thread (main 2, Uo.Render workers, MapLoader
     MaxPending prefetches + 1 sync load): 35 MiB on a 24-core box (67 MiB with the gump budget).
   - `Region` (logical region buffers, rented with `RentShared(n, LargeUse.Region)` by `RegionFile.DecompressInto(pooled)`,
     returned by `Region.ReleaseData` and `MapLoader`): one teleport's swap, 2 caches x 4 regions (each cache's loaded area
     spans at most 2 x 2 regions of 512 tiles) = 8 buffers, at most 32 MiB. A region rent may take a free buffer up to two
     classes larger. Both caches load before they evict, so these are the buffers the next teleport reuses.
   - `Page` (PackPages chunk buffers): 3 lanes x 2 decodes in flight = 6 chunks (6 MiB).

   `TrimIdle(180 s)` runs every 5 s from `GameController.Update` and drops idle `General` and `Page` buffers; `Region` buffers
   stay (their cap is the floor). `Trim()` drops everything. `[bufpools]` lines print each use's rents, hits, misses,
   outstanding and free counts, high-waters and cap, also at each teleport and 5 s after it.
4. Return only arrays the pool rented. Arrays allocated with `new` (an initial field array, for example) are left to the GC.
5. `SharedFamilyPool<T>.Instance` and `LocalFamilyPool<T>.Instance` wrap the two families as an `ArrayPool<T>` for code that
   takes a pool instance (animation frame buffers; the ref collections).

## Large-array element types

The large pool has eleven lifetime element-type slots shared by both families and every `LargeUse`. They cover the
current runtime inventory, including optional measurement paths and growable ref collections:

- `byte`, `uint`: pack, region, page, codec, pixel and animation buffers.
- `char`: `ValueStringBuilder` growth.
- `ulong`, `GameObject`: chunk-light flags and handles; `GameObject` also holds light-search candidates.
- `LightSearchMeasure.Search`: the optional light-search measurement arrays.
- `GpuItemCache.Entry`, `SpriteInstance`: growable GPU chunk item arrays.
- `WebLinkRect`, `HTMLDataInfo`, `Multi`: growable font HTML and house-piece ref lists.

`ChunkDraw` tables use ordinary allocated arrays here and spend no slot. Bounded chunk-light `int`, `uint`, `ushort`
and `sbyte` columns, light-search chunk references and mobile-wall scans stay at or below 64 KiB. Frame-light fade callers
use sixteen ranks, within their stack buffer. New runtime consumers that can exceed 64 KiB must update the inventory,
slot count and `LargePoolTypeRegistrationTests` together. A type beyond the slot limit still rents, but drops every return.
`ResetForTesting` clears free buffers and counters while preserving lifetime registrations; registry tests load fresh
copies of the real pool assembly to avoid depending on the rest of the test process.

Struct element sizes need not divide a byte class. Rent rounds the class down to whole elements; Return accepts that
length, and eviction accounts for the actual array payload. For example, the 80 KiB class holds 27,306 three-byte elements
(81,918 bytes). Payload byte/count caps remain shared per use, rather than growing with the number of element types.

On x64, the four-to-eleven-slot expansion and element-size table add about 16.9 KiB of immediate metadata. Each newly
visited type/class/use bucket lazily allocates about 560 bytes of stack metadata (at most another 0.46 MiB across all
additional buckets). These stacks remain after trimming free arrays. No arrays are prefilled; the default free payload
budget remains 70 MiB, and host-derived caps are unchanged. More types can retain arrays within those caps. Gameplay
allocation, GC and timing benefits require a paired measurement; deterministic reuse tests establish pooling behavior.

## Ref collections

`PooledRefList<T>`, `PooledRefQueue<T>` and `ValueStringBuilder` are ModernUO copies (see `THIRD-PARTY-NOTICES.md`). They rent
through the facade: `Create(capacity)` uses the per-thread family, `CreateMT` the shared one. Dispose them (`using var`).
`ValueStringBuilder.ToString()` does not dispose the builder (the client's earlier builder did): call `Dispose()` after it.
`Replace(span, span)` replaces the first occurrence only.

## Gump decode budget

`gump_decode_budget` (settings.json, default `false`) counts gump worker decodes in their own 32 MiB in-flight budget
(`DecodeBudget.Gumps`), so a burst of large gumps waits instead of growing without a bound. Off: gump decodes are not counted.

## Diagnostics

| Variable | Meaning |
|---|---|
| `CUO_POOL_AUDIT=1` | Dev. Log `[pool-audit]` lines with the stack for a double return, a return of an array the pool never rented, a per-thread rent or return on another thread, and arrays still rented at shutdown |
| `CUO_GUMP_BUDGET_CHECK=1` | Dev. Compare the bytes gump decodes hold with the bytes the gump budget admitted, log waits; periodic summary |
| `CUO_GUMP_DECODE_BUDGET=1\|0` | Dev. Override `gump_decode_budget` |

While in game the client log carries, every 20 s for the first two minutes and then every minute, a `[bufpools]` line (large pool rents, hits, misses, allocated, outstanding,
high-water and free MB; per-thread pools; RenderedText pool; dropped region prefetches; trimmed bake jobs) and an `[objpools]`
line (item pool, chunk pool, land and static recycler, mobile frame and worn caches, tile index entry arrays: pooled count or
bytes against the cap, misses and drops).

The tile index entry arrays (`DynamicTileIndex.EntryPool`) keep at most 4 MiB in all.
