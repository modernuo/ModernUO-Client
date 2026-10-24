# ECS Benchmarks: TinyEcs vs Friflo.Engine.ECS vs Arch

Harness: `research/ecs-bench/` — a custom harness (not BenchmarkDotNet), so the same binary can also be NativeAOT-published.

| Library | Commit | Notes |
|---|---|---|
| TinyEcs | `7b8c2a0` (2026-10-23) | core `World` + `QueryBuilder` APIs |
| Friflo.Engine.ECS | `c30bb3f8` (2026-10-16) | `CreateEntity<…>`, `Query<…>().Chunks`, `QueryJob.RunParallel` |
| Arch | `cbe716f` (2026-10-17) | Release config; `World.Create<…>`, chunk iteration, `InlineParallelChunkQuery` |

## Workload (UO-shaped)

The world has 300k land tiles, 200k statics, 50k items (40k on the ground), 5k mobiles (250 hidden), and transient effects.

| Benchmark | One op = |
|---|---|
| RenderExtract | iterate all Pos+Graphic+Depth (555k): compute isometric depth, write it, cull against a 64×64 view |
| RenderExtractParallel | same, multi-threaded (native API where one exists; TinyEcs hand-rolled `Parallel.For`) |
| MobileTick | 5k mobiles: advance animation + motion → position |
| PacketLookups | 20k random serial → entity get/set (simulates packet handlers) |
| ChurnFrame | toggle a tag on 50 mobiles + 500 items, spawn 200 effects, age and despawn expired ones, run 6 small queries |
| ChurnManyQueries | same churn, 120 distinct live queries (each copy has a unique `Without<Nk>` so nothing shares a cache) |
| BlockStream | spawn 16 map blocks (~2.6k entities), despawn the 16 spawned two ops earlier |
| DirtyTag | change 500 item hues, flag with a `Dirty` tag, consume, clear |
| DirtyTicks | same via per-row change ticks (TinyEcs only) |
| Snapshot | copy 5k mobiles' Pos/Motion/Anim out and back (rollback snapshot) |

All three libraries produced **identical checksums on every benchmark in every round**, so the adapters do equivalent work.

## Conditions

- 24 logical cores, .NET 10.0.13, JIT (tiered PGO), workstation non-concurrent GC.
- **The machine was under background load** (Docker plus another agent session). To compensate:
  - each library ran in its own process at High priority;
  - 3 rounds, with the library order rotated each round;
  - the table reports the **best median** of the 3 rounds (noise only ever adds time).
- Expect about ±10% on absolute values. Parallel numbers are indicative only; all were capped at 4 threads (`BENCH_THREADS=4`).
- Raw output: `research/ecs-bench/results-rounds.md`.

## Results (µs per op, best-of-3 medians; lower is better)

| Benchmark | TinyEcs | Friflo | Arch |
|---|---:|---:|---:|
| RenderExtract | 805 | **736** | 832 |
| RenderExtractParallel (4 threads) | 311 (11.6 KB/op alloc) | **236** | 890 |
| MobileTick | 11.7 | 11.1 | 11.2 |
| PacketLookups | 654 | **433** | 503 |
| ChurnFrame | 161 | 157 | 156 |
| ChurnManyQueries | 115 | 67 | 112 |
| BlockStream | 154 | **89** | 138 |
| DirtyTag | 94 | 75 | 73 |
| DirtyTicks | **56** | n/a | n/a |
| Snapshot | 6.6 | 6.7 | 7.6 |

| | TinyEcs | Friflo | Arch |
|---|---:|---:|---:|
| Populate 555k entities (ms) | 163 | 240 | 153 |
| Retained heap (MB) | 72 | 44 | 33 |
| Steady-state alloc | 0 (13–19 B/op on structural ops) | 0 | 0 (294–952 B/op on structural ops) |
| NativeAOT analysis warnings | 0 | 16 (13 in Friflo.Engine.ECS: reflection schema, `Enum.GetValues`; 3 from Friflo.Json.Fliox) | 0 |

### NativeAOT (µs per op, best-of-3 medians; same conditions)

| Benchmark | TinyEcs | Friflo | Arch |
|---|---:|---:|---:|
| RenderExtract | 1302 | **648** | 712 |
| RenderExtractParallel (4 threads) | 273 (11.5 KB/op) | **157** (38 B/op) | 359 |
| MobileTick | 7.5 | 6.7 | 7.1 |
| PacketLookups | 807 | **400** | 503 |
| ChurnFrame | 112 | **70** | 102 |
| ChurnManyQueries | 131 | **74** | 109 |
| BlockStream | 205 | **148** | 181 |
| DirtyTag | 103 | 73 | 72 |
| DirtyTicks | 48 | n/a | n/a |
| Snapshot | 4.1 | 3.8 | 4.1 |
| Populate 555k entities (ms) | 55 | 45 | 41 |

Checksums under AOT match the JIT runs exactly. Raw output: `research/ecs-bench/results-aot-*.md`.

- **Most workloads get faster under AOT**, mainly because nothing runs as unoptimized tier-0 code during warm-up. BlockStream is slower for all three (structural code without profile-guided optimization).
- **TinyEcs regresses under AOT** on RenderExtract (805 → 1,302 µs) and PacketLookups (654 → 807). Cause not investigated; likely codegen differences without PGO.
- **Friflo leads every AOT benchmark.** It needs explicit type registration at startup:
  - `new NativeAOT()`, then `RegisterComponent<T>()`/`RegisterTag<T>()` for every type, then `CreateSchema()`, all **before** the first `EntityStore` is created (see `FrifloAdapter.cs`);
  - without it, the first tag lookup throws `ITag type not found`;
  - its 16 AOT warnings are covered by this registration step.

## Findings

- **Raw iteration is a tie.** About 1.4 ns per entity in all three, limited by memory bandwidth.
- **Friflo wins structural changes, lookups and parallel iteration.**
  - It has the only native intra-query parallelism that scales (3–4×).
  - Hazard: its workers busy-spin with no backoff (`ParallelJobRunner.cs:219`). With thread count ≥ logical cores, the main thread starved and a pass took about 34 ms. Cap it below the core count.
- **Arch's parallel query barely beats single-threaded**, because it waits on a barrier after every archetype.
- **TinyEcs is unique in per-row change detection.** It's the cheapest dirty path, with no structural change at all. It's slowest at lookups and spawning and uses the most memory.
- **Query count barely matters at about 25 archetypes in any library.** ChurnManyQueries came out *faster* than ChurnFrame for all three, which looks like JIT tiering warm-up order, not real.
- **Thread sweep for RenderExtractParallel (µs):**

  | Threads | Friflo | Arch |
  |---:|---:|---:|
  | 4 | 232 | 1335 |
  | 8 | 273 | 975 |
  | 12 | 244 | 823 |
  | 16 | 213 | 844 |
  | 23 | 180 | 812 |

  Memory bandwidth caps the gain at 3–4×.
- **Scale check:** every per-frame workload here fits in about 2 ms total. At 144 fps the frame budget is 6.9 ms. **ECS choice is not what will make or break frame time.**

## Library ranking (for the dynamic-object world)

1. **Friflo:** best numbers (and the best under NativeAOT) and most mature (command buffers, serialization, indexes, relations, parallel queries). It works under NativeAOT with explicit schema registration at startup.
2. **TinyEcs (forked and patched):** if we want its Bevy app layer, UI and per-row change detection. See [tinyecs-evaluation.md](tinyecs-evaluation.md).
3. **Arch:** lean memory, but weak parallelism and allocates on structural changes.

Pick inside a vertical-slice prototype, not on these numbers alone.

## Could we use a native (C/Rust) ECS instead?

Yes, it's possible, but not worth it.

- **What works:** the native library hands C# raw column pointers per table, and C# iterates `Span<T>` over native memory. That's as fast as the C# libraries above.
- **What doesn't:** native code calling back into C# per entity costs roughly 10–30 ns per call, so 5–15 ms per 555k-entity pass.
- **Candidates:**
  - **flecs** (C99) is the only realistic one: mature, with relationships, prefabs, JSON serialization, a web-based explorer, and Flecs.NET bindings.
  - **Bevy ECS** (Rust) and **EnTT** (C++ templates) have no C ABI.
- **Why not:** the C# libraries already run at the memory-bandwidth limit, so native buys features, not speed. It costs native builds for every target (including emscripten and mobile toolchains), debugging in two languages, and native crashes instead of exceptions. Components would have to be unmanaged, which is fine.
- **Option:** add Flecs.NET to this harness if we want hard numbers.
