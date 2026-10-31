# ECS Decision

**Status:** adopted. Friflo.Engine.ECS is in use (`src/ClassicUO.Ecs`); where the ECS goes next is [ecs-transition-plan.md](ecs-transition-plan.md). The final Flecs.NET timing rounds were never run; the comparison below is from single runs.
**Recommendation: Friflo.Engine.ECS**, used only for dynamic objects (items, mobiles, effects, lights, emitters), single-threaded by default with explicit parallel jobs. The static world stays out of the ECS (chunk arrays plus GPU buffers), as described in [architecture-recommendations.md](architecture-recommendations.md#1-world-storage-split-static-from-dynamic).

## Candidates

| | Friflo.Engine.ECS | TinyEcs | Flecs.NET (flecs C) | Arch (reference) |
|---|---|---|---|---|
| Iteration | Best or tied | Tied under JIT, **60% slower under AOT** | Tied | Tied |
| Lookups and structural changes | **Best** | Slowest under AOT | 1.5–2× slower than Friflo (one P/Invoke each) | Middle; allocates 300–950 B per op |
| Native parallel queries | Yes (3–4×; busy-spins, so cap threads) | No | Yes (worker pipeline via `Progress()`) | Weak (barrier per archetype) |
| Per-row change detection | No (tags or events) | **Yes** | Table-level only | No |
| NativeAOT | Works; needs an explicit `NativeAOT` schema registration | Clean | Clean; ships `flecs.dll` (static link possible) | Clean |
| Serialization, relations, indexes | Yes | Relations only | Yes (most features of all) | Partial |
| Maintenance | Active; established project | Active; **single author**, tuned for his own client | Very mature C core plus a C# binding layer | Active |
| Ergonomics for ECS newcomers | **Easiest** (`foreach (var (a, b, _) in q.Chunks)`) | Bevy-style app layer, more concepts | Hardest (field indices, several iteration styles, uninitialized fast path) | Easy |
| Known defects found | Spin-wait oversubscription hazard | Confirmed scheduler ordering bug; `Commands` serializes systems | None found | Allocation on structural ops |
| Extra platform cost | None (pure C#) | None (pure C#) | A native library per platform (incl. WASM and mobile) | None |

Measurements: [ecs-benchmarks.md](ecs-benchmarks.md). TinyEcs review: [tinyecs-evaluation.md](tinyecs-evaluation.md).

## Why Friflo

1. **Fastest where a UO client actually spends ECS time.** Packet-driven lookups, entities appearing and disappearing, tag churn, and chunk stream-in. Under NativeAOT, our shipping configuration, it leads every benchmark.
2. **Pure C#.** It keeps the browser (Mono-WASM) and mobile paths open with no native ECS library to build per platform. That rules out Flecs.NET as the default, even though Flecs is the most capable.
3. **Lowest learning curve** for a team new to ECS. Plain struct components, span-based chunk iteration, command buffers for deferred changes.
4. **Mature features we'll want:** command buffers, JSON serialization (save/debug dumps), component indexes (a serial → entity lookup without a side dictionary), relations (containers, equipment), parallel query jobs.

## What we give up, and how we cover it

- **Per-row change detection (TinyEcs's strength):** use explicit dirty tags or Friflo's component events where needed (UI bindings, render cache invalidation). Packet handlers already know what changed.
- **A Bevy-style app and scheduler:** write a thin scheduler ourselves. Ordered stages, single-threaded by default, explicit jobs. That's simpler and safer than auto-batching, which is where TinyEcs's ordering bug lives.
- **TinyEcs's UI layer:** the UI will be a retained widget tree anyway (classic gumps plus server-driven UI); we may still borrow design ideas from it.

## Conditions and caveats

- **Register every component and tag type with `NativeAOT` before creating any `EntityStore`.** Otherwise the first tag lookup throws under AOT.
- **Cap `ParallelJobRunner` threads below the logical core count.** Its workers busy-spin and starve the main thread otherwise.
- **Reassess if the vertical slice turns up a blocker.** TinyEcs (forked and patched) is the fallback.
