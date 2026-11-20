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
3. `LargeBufferPool` keeps at most its budget of free buffers (112 MiB per client), in four size classes per octave, at most 32
   per class; above the budget it evicts the least recently returned. It does not cap what is rented out (decode budgets and
   caches do that). `TrimIdle(180 s)` runs every 5 s from `GameController.Update` and never goes below a 16 MiB floor of free buffers (classes that never had a hit go first); `Trim()` drops everything.
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
