# TinyEcs Evaluation

Repository: `andreakarasho/TinyEcs`, reviewed at commit `7b8c2a0` (2026-10-23). One author, very active (53 commits since July 2026).
About 33k lines of library code: core `TinyEcs` 6.4k, `TinyEcs.Bevy` 11.6k, `TinyEcs.Bevy.UI` 9.3k, `TinyEcs.Bevy.Modding` 5.8k, `TinyEcs.Bevy.Input` 0.6k.
The core test suite passes: **410/410** on net10.0. The submodules (`external/Clay.NET`, `external/wasmtime-dotnet`) must be initialized before the UI and modding projects build.

The library is being tuned against a real UO client. Code comments cite "the client's steady-state allocation", gumps and drag items. The modding guest assembly was renamed `cuo-guest-core` → `cuo`.

> Note: `ClassicUO-ECS` (the "Mecs-Client" fork) does **not** use TinyEcs. It has its own `Dictionary<Type, IComponentStorage>` ECS running alongside the legacy objects. Nothing from it informs this evaluation.

## Core (`src/TinyEcs`)

**Good:**
- Standard archetype storage: one `T[]` per component (`Column<T>`), with per-row `ChangedTicks`/`AddedTicks` arrays.
- Bitmask query matching, zero-allocation `ref struct` iterators.
- Bevy-correct wrapping change ticks (`ChangeTick.cs`).
- No reflection; AOT analysis is clean (0 warnings).

**Weaknesses:**
1. Every component add or remove bumps `_structuralChangeVersion` (`World.cs:281,367`). Every query then re-walks the whole archetype graph on its next `Iter()` (`Query.cs:172-184`). *Measured impact at about 25 archetypes is below noise* (see [ecs-benchmarks.md](ecs-benchmarks.md)). It grows with archetype count, and the fix is small: match empty archetypes too and skip them during iteration.
2. No fixed-size chunks. Columns grow by doubling with `Array.Resize`, so a large archetype (e.g. 200k statics) creates large-object-heap (LOH) garbage on growth.
3. `SetDeferred` boxes every component value (`World.Deferred.cs:57`).
4. No serialization, snapshot or world cloning.
5. Uses about 2× the memory of Friflo or Arch for the same world (72 MB vs 44/33 MB), mostly from the per-row tick arrays.

## Bevy layer (`src/TinyEcs.Bevy`)

**Good:**
- Steady state allocates nothing: params are built once and the batch plan is cached.
- A custom persistent thread pool, at most 4 workers (`ParallelSystemExecutor.cs`).

**Weaknesses:**
- **Confirmed ordering bug.** `BuildOneBatch`/`TryAddToBatch` (`BevyApp.cs:1481-1561`) only checks `Before`/`After` against systems already in the current batch, never against skipped systems still pending. With a chain A→B→C, where B conflicts with A and C doesn't, multi-threaded mode runs **A, C, B**. Repro: `research/SchedulerBatchOrderingRepro.cs`. Existing ordering tests use single-threaded mode, so they miss it.
- Parallelism is system-level only. There is no intra-query parallel iteration.
- `Commands` declares a write on `typeof(Commands)` (`BevySystemParams.cs:447`). Every system that uses `Commands` therefore serializes against every other, which removes most parallelism in practice.
- Allocates in places:
  - observer trigger structs are boxed (`BevyObservers.cs:442,464`);
  - `EventReader` copies all events into a list on every fetch;
  - `AddChildAt`/`RemoveChild` commands aren't pooled;
  - every `Res`/`ResMut` fetch goes through a `ConcurrentDictionary` lookup.
- Events live about one frame. A reader gated out by `RunIf` for 2+ frames silently misses events.
- Raw `Action<World>` systems are exclusive only against their own type, so they can run alongside parameterized systems.
- Absent entirely: fixed timestep, sub-apps or multiple worlds, assets/handles, async jobs.
- Generated arity limits: 16 system params, 16 query data/filter terms, 15 observer params.

## UI (`src/TinyEcs.Bevy.UI`)

- **Built from ECS entities.** UI nodes are entities with `Node` plus components. Layout comes from Clay.NET.
- **Renderer-agnostic.** It emits `RenderCommand`s into a resource, and the host draws them.
- **Widgets:** windows (drag, bring-to-front, right-click close), scroll, text editing (single-line, multiline, masked), focus/tab, buttons, sliders, lists, menus, popovers, pixel-perfect hit testing. Several opt-outs (`UiNoWindowDrag`, `UiDragYield`) look designed for gumps.
- **Text is the gap.** There is no rasterizer, shaper or glyph atlas; the host supplies `ITextMeasurer`, and the sample's measurer allocates a string per call. No IME, bidi or shaping.
- **Performance:** relayout is gated on `Changed<T>` ticks, with a per-root declaration cache. In-place `.Ref` writes need a manual `SetChanged`, otherwise the UI goes stale.

## Modding (`src/TinyEcs.Bevy.Modding`)

- Mods are WASM component-model modules (wasm32-wasip2) on a **forked** wasmtime-dotnet. The WIT package is `tinyecs:modding@0.1.0`.
- Mods register systems and queries, and can observe packets with pass/block/replace verdicts.
- No fuel, epoch or memory limits: a mod can hang the frame. The manifest's permissions section is an empty placeholder.
- Hot reload is supported. The only guest example is a Rust test fixture; there is no SDK for mod authors.
- **Relevance:** the project owner has ruled out third-party scripting and plugins, so this layer is not a reason to choose TinyEcs.

## Verdict

TinyEcs is well built and UO-aware. It's a viable base only as a **pinned fork** we own, with the ordering bug, the `Commands` serialization, deferred boxing and query invalidation fixed. Its unique strengths are per-row change detection and the ready-made app layer and UI. Its risks are a single upstream author whose roadmap is his own client, API churn, and the highest memory use of the three.
