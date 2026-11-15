# Friflo ECS: version, read costs and the `EcsRead` helper

The ECS world state (`src/ClassicUO.Ecs`) uses **Friflo.Engine.ECS 4.0.0-preview.2** (NuGet, pinned in `ClassicUO.Ecs.csproj`). We stay on this preview until Friflo publishes a stable release (owner decision, 2026-11-02). Until then, hot component reads go through our own helper, `EcsRead` (`src/ClassicUO.Ecs/EcsRead.cs`).

## Why a helper

In preview.2, components live behind an abstract `StructHeap<T>` (the preview added SoA and aligned storage kinds). So every `Entity.GetComponent<T>()` and `EntityData.Get<T>()`:

- is a call the JIT does not inline;
- casts the archetype's heap to the non-sealed `StructHeap<T>` (a class-hierarchy cast);
- makes a virtual `GetComponentRef` call.

`EcsRead` inlines the node lookup and indexes the heap's `T[]` directly.

## Measured costs

`EcsReadCostBench` (tests, manual) runs every reader over the same shuffled entities across 6 archetypes. Settings: `DOTNET_TieredCompilation=0`, 201 interleaved rounds. The table gives the median ns per entity, with p10–p90 in brackets.

The machine was busy, so the numbers are noisy. Friflo's own rows spread widely: `GetComponent x5` has a p90 two to three times its median. The helper's rows are tight.

| Reader | 2,000 entities | 25,000 entities |
|---|---|---|
| `GetComponent`, 1 read | 5.9 (5.7–6.5) | 9.1 (8.9–11.3) |
| `TryGetComponent`, 1 read | 6.4 (6.1–8.1) | 8.8 (8.5–12.5) |
| `Data.Get`, 1 read | 6.0 (5.7–6.6) | 8.6 (8.3–10.3) |
| `EcsRead.Get`, 1 read | 3.8 (3.7–4.4) | 5.3 (5.1–6.4) |
| `EcsRead.TryGet`, 1 read | 3.9 (3.9–4.5) | 5.3 (5.2–6.1) |
| `GetComponent`, 5 reads | 37 (27–85) | 51 (37–108) |
| `Data` + 5 `Get` | 24 (20–61) | 34 (27–86) |
| `EcsRead.Get`, 5 reads | 16 (15–17) | 21 (21–26) |
| `EcsRead.Row` + 5 `Get` | 12.6 (12.3–14.7) | 18.8 (17.9–22.5) |
| `Data` + `Get` + 2 tags | 7.0 (6.5–8.4) | 9.9 (9.3–14.1) |
| `EcsRead.Row` + `Get` + 2 tags | 5.2 (5.1–5.9) | 6.8 (6.7–7.9) |

The loop itself costs 0.5 ns per entity.

`GatherCostBench` measures the Gpu item extract's record read (`ItemSpriteInput.ReadRecord`) against the plain field reads it replaced in T7b part 1. The cost of the record read over the field reads went from 30–40 ns to 16–22 ns per item through `EntityData`. At 25,000 shuffled items it went from 74 to 60 ns.

The earlier "about 25 ns per `GetComponent`" came from a read inside a larger per-item record. A single read in a tight loop costs 6–9 ns. Either way, the helper roughly halves the cost of a multi-component read.

## The helper

- `EcsRead.Get<T>(entity)` replaces `entity.GetComponent<T>()`: it returns a `ref T` to the same storage.
- `EcsRead.TryGet<T>(entity, out value)` replaces `entity.TryGetComponent(out value)`.
- `EcsRead.Row(entity)` replaces `entity.Data`. It returns an `EcsRow` with `IsNull`, `Id`, `Archetype`, `Tags`, `Has<T>`, `Get<T>` and `TryGet<T>`.

The answers and exceptions are Friflo's. A deleted entity or a missing component throws `NullReferenceException`; a deleted entity's row has `IsNull`.

The helper reads these Friflo internals:

- `Entity`'s fields, by the struct's layout (`Unsafe.BitCast`);
- `EntityStore.nodes`;
- `EntityNode.archetype`, `compIndex` and `revision`;
- `StructHeapGen<T>.components`;
- `Archetype.heapMap`.

All but `heapMap` go through `[UnsafeAccessor]`, which is bound at compile time and works in NativeAOT. `heapMap`'s type (`StructHeap[]`) is internal, and .NET 10 cannot name it in a field accessor (`UnsafeAccessorType` is not supported on field return types). So it is read at a field offset, which a probe finds once. The probe compares the archetype's pointer-sized slots, as integers, with the heap map that `Entity.Data` holds. It then checks the match with a typed read, and checks the node, archetype and heap reads against Friflo's own answers on two stores and three archetypes.

If the probe fails (a changed Friflo), the first read throws `InvalidOperationException` that names the reason.

### Heap kinds

Only `StructHeapGen<T>` (a plain `T[]`) is read directly. Preview.2 picks another heap kind (`ComponentType<T>.CreateHeap`, `SimdUtils.GetLayout`) for a struct whose public field named `Value` (or `value`) is a `float`, `Vector2`, `Vector3`, `Vector4` or `Quaternion` and makes up the whole struct:

| type | heap |
|---|---|
| `float`, `Vector2`, `Vector4` or `Quaternion` `Value` | `StructAoSFloat`, `StructAoSVector2`, `StructAoSVector4` (aligned arrays) |
| `Vector2`..`Vector4` `Value` with `[SoA]` | `StructSoAVector2..4` |
| `Vector2`..`Vector4` `Value` with `[AoSoA]` | `StructAoSoAVector2..4` |
| `Vector3` `Value` without an attribute, or anything else | `StructHeapGen<T>` |

`EcsRead.Slot<T>` decides once per type, at the type's first read, with that rule (`Slot<T>.InPlainArray`, from `SimdInfo<T>.FieldCountSoA` and the attributes). `Slot<T>.Value` is then the heap map index + 1 for a direct type, or -1 for any other. Reads of a direct type index the array. Reads of any other type go through Friflo's own `GetComponent`, `TryGetComponent`, `EntityData.Has/Get/TryGet`, so they give Friflo's answers, refs and exceptions; `GetComponent` on an SoA or AoSoA heap throws Friflo's `InvalidOperationException`. The first read of every type also goes through Friflo. The hot path tests `Slot<T>.Value <= 0`, the branch it already had for the first read, so a direct type pays nothing more. If the rule were wrong for some type, the accessor's class check would throw `InvalidCastException`; it never reads the wrong memory. `EcsReadTests` checks the rule against the heap Friflo creates for every mirror component and for each special kind, and compares `Get`, `TryGet` and `Row` with Friflo for each.

The hot path reads only statics of classes without a static constructor. Its only out-of-line calls are the first read of a type and the reads of other heap kinds, and they take no struct argument and return no struct. So neither the JIT nor NativeAOT adds initialisation checks, and the entity and the row (four fields, like `EntityData`) stay in registers. Keep it that way when editing the file: a struct argument to a cold call, or a fifth `EcsRow` field, cost 2–6 ns per read in the measurements. For the same reason the `TryGet` fallbacks return a `ref` to a thread-static copy of the value (or a null ref) instead of taking an `out` argument: an `out` address passed to a cold call puts the caller's value on the stack and adds a store on the direct path too.

## Validity rule

Nothing is cached per entity or per archetype. The process keeps only the heap map offset and each component type's schema index (or -1 for another heap kind).

A `ref` from `Get`, or an `EcsRow`, is valid until the next structural change of the store: a component or tag added or removed, or an entity created or deleted. Those move entities between archetypes and grow or replace component arrays. This is the same rule as for Friflo's own `ref`s and `EntityData`, so read again after a structural change. For example, `MobileAnimationSystem.TakeDirty` removes a tag, so the mobile draw takes its row after it.

`EcsReadTests` covers the rule: reads after archetype moves, after deletes (the moved last entity, and the dead handle throwing), after storage growth past the archetype capacity, across stores, and writes through the returned `ref`.

## Converted sites

These per-frame reads use the helper:

- The Gpu item extract's record read: `ItemSpriteInput.ReadRecord`.
- The mobile draw on both renderers: `GpuWorldRenderer`, `LegacySpriteSink`, `MobileAnimationSystem.Update`/`UpdateInPlace` and `MobileSpriteExtract`, with `EcsRow` replacing `EntityData`.
- The T4 cold scalar reads in `EcsScalars`.
- The systems' per-entity reads: step, mobile and item animation advance, effects, and equipment-slot rebuild.

Packet-rate code (`WorldMirror`, `WorldWriter`) still calls Friflo directly.

## When a stable Friflo ships

Friflo's main branch (checked at c30bb3f8, 13 commits after the `engine-v4.0.0-preview.2` tag) reverts the SoA storage experiment:

- `StructHeap<T>` is sealed again and holds `components` directly. The `StructHeapGen<T>` and the AoS/SoA/AoSoA heap classes are gone.
- `GetComponent`, `TryGetComponent`, `EntityData.Get/TryGet` and `Archetype.Components<T>()` read `components[compIndex]`, with no virtual call.
- `Entity.GetSoA` and the `Chunk<T>` SoA accessors are removed.
- `CommandBuffer.AddComponent`/`SetComponent` gain overloads with a default value and return a reference to the added component.

`Entity`, `EntityNode` and `Archetype.heapMap` are unchanged in that diff. Re-check them against the release that ships.

Steps after upgrading the package:

1. Run `EcsReadTests`. With `StructHeapGen<T>` gone, `The_Friflo_Internals_Are_Found` fails or reads throw, because the probe checks the heap class.
2. Run `EcsReadCostBench` (`DOTNET_TieredCompilation=0 CUO_ECS_READ_BENCH=<file> dotnet test tests/ClassicUO.UnitTests -c Release --filter EcsReadCostBench`). Do the same for `GatherCostBench`. Compare Friflo's rows with the helper's.
3. If `GetComponent` and `Data` are within about 1 ns of the helper, delete `EcsRead.cs`, `EcsReadTests` and `EcsReadCostBench`. Then revert the call sites:
   - `EcsRead.Get<T>(e)` becomes `e.GetComponent<T>()`;
   - `EcsRead.TryGet(e, out v)` becomes `e.TryGetComponent(out v)`;
   - `EcsRead.Row(e)` / `EcsRow` become `e.Data` / `EntityData`.

   `grep -rn "EcsRead\.\|EcsRow" src tests` lists them all.
4. If Friflo is still slower (its calls stay non-inlined), keep the API and adapt `EcsRead.Layout` and `Access<T>` to the new internals: the heap class name, `heapMap`'s type, if it became nameable, and `Slot<T>.InPlainArray` (with main's single sealed heap class it is always true). Keep the probe checks.
