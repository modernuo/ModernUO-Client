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
