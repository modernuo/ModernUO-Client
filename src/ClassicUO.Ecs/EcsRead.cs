// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    /// <summary>
    /// Component reads that go straight to the archetype's component array: <see cref="Get{T}"/> for
    /// <c>Entity.GetComponent</c>, <see cref="TryGet{T}"/> for <c>Entity.TryGetComponent</c>, <see cref="Row"/> for
    /// <c>Entity.Data</c>. Same answers, refs to the same storage, the same exceptions (a deleted entity or a missing
    /// component throws <see cref="NullReferenceException"/>).
    /// </summary>
    /// <remarks>
    /// Why: in Friflo.Engine.ECS 4.0.0-preview.2 <c>GetComponent</c> and <c>Data</c> are calls the JIT does not inline, and
    /// every component read casts the archetype's heap to the abstract <c>StructHeap&lt;T&gt;</c> (the preview's SoA
    /// storage) and makes a virtual <c>GetComponentRef</c> call. These reads are inlined and index the <c>T[]</c> directly;
    /// docs/dev/ecs-friflo.md has the measured costs. We stay on the preview and wait for a stable Friflo release (owner,
    /// 2026-11-02); Friflo's main branch has already gone back to a sealed heap read as <c>components[compIndex]</c>.
    /// Migration once a stable release is in: measure <c>GetComponent</c> against this (EcsReadCostBench); if it is as
    /// fast, delete this file and turn the call sites back into <c>GetComponent</c> / <c>TryGetComponent</c> / <c>Data</c>
    /// (<see cref="EcsRow"/> has <c>EntityData</c>'s members); otherwise keep the API and adapt <see cref="Layout"/>.
    /// <para>
    /// Internals read (no reflection per read: UnsafeAccessor binds at JIT or AOT compile time and is AOT-safe):
    /// <c>Entity.store</c> (by the struct's layout), <c>EntityStore.nodes</c>, <c>EntityNode.archetype/compIndex/revision</c>,
    /// <c>StructHeapGen&lt;T&gt;.components</c>, and <c>Archetype.heapMap</c>, whose type (<c>StructHeap[]</c>) is internal
    /// so no accessor can name it: it is read at a field offset found and checked once against what <c>Entity.Data</c>
    /// holds (<see cref="Layout"/>). If a Friflo upgrade changes them, the first read throws
    /// <see cref="InvalidOperationException"/> (and EcsReadTests fail).
    /// </para>
    /// <para>
    /// Heap kinds: only <c>StructHeapGen&lt;T&gt;</c> (a plain <c>T[]</c>) is read directly. Preview.2 keeps a struct
    /// whose only field is a float, Vector2, Vector4 or Quaternion named Value (and, with [SoA] or [AoSoA], a Vector2..4)
    /// in an aligned or SoA heap instead; reads of such a type go through Friflo's own <c>GetComponent</c>,
    /// <c>TryGetComponent</c> and <c>EntityData</c>, so they give Friflo's answers, refs and exceptions (an SoA heap's
    /// <c>GetComponent</c> throws <see cref="InvalidOperationException"/>). The choice is made once per type at its first
    /// read (<see cref="Slot{T}"/>); a direct type's read keeps the one branch it had. Every mirror component is a
    /// direct type (EcsReadTests).
    /// </para>
    /// <para>
    /// Validity: nothing is cached per entity or per archetype. Kept per process: the heap map offset and each component
    /// type's schema index (or -1 for another heap kind). A returned <c>ref</c> or <see cref="EcsRow"/> is valid until the
    /// next structural change of the store (a component or tag added or removed, an entity created or deleted: they move entities between archetypes and
    /// grow component arrays), exactly like Friflo's own <c>ref</c>s and <c>EntityData</c>. Read again after one.
    /// </para>
    /// </remarks>
    public static class EcsRead
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Get<T>(Entity e) where T : struct, IComponent
        {
            int slot = Slot<T>.Value;

            if (slot <= 0)
            {
                return ref Slot<T>.Get(Layout.Store(e), e.Id, e.Revision);
            }

            ref EntityNode node = ref Layout.Node(e);
            Archetype archetype = Layout.NodeArchetype(ref node);

            if (archetype == null || Layout.NodeRevision(ref node) != e.Revision)
            {
                ThrowDeleted();
            }

            // a missing component is a null heap: NullReferenceException, as GetComponent
            return ref Access<T>.Components(Layout.HeapMap(archetype)[slot - 1])[Layout.NodeCompIndex(ref node)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGet<T>(Entity e, out T value) where T : struct, IComponent
        {
            int slot = Slot<T>.Value;

            if (slot <= 0)
            {
                ref T found = ref Slot<T>.TryGet(Layout.Store(e), e.Id, e.Revision);

                if (Unsafe.IsNullRef(ref found))
                {
                    value = default;

                    return false;
                }

                value = found;

                return true;
            }

            ref EntityNode node = ref Layout.Node(e);
            Archetype archetype = Layout.NodeArchetype(ref node);

            if (archetype == null || Layout.NodeRevision(ref node) != e.Revision)
            {
                ThrowDeleted();
            }

            object heap = Layout.HeapMap(archetype)[slot - 1];

            if (heap == null)
            {
                value = default;

                return false;
            }

            value = Access<T>.Components(heap)[Layout.NodeCompIndex(ref node)];

            return true;
        }

        /// <summary><c>Entity.Data</c>: one node lookup for several component reads of the same entity. A deleted entity
        /// gives a row with <see cref="EcsRow.IsNull"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EcsRow Row(Entity e)
        {
            if (Layout.HeapMapOffset.Value <= 0)
            {
                Layout.Init();
            }

            ref EntityNode node = ref Layout.Node(e);
            Archetype archetype = Layout.NodeArchetype(ref node);

            if (archetype == null || Layout.NodeRevision(ref node) != e.Revision)
            {
                return new EcsRow(e.Id, null, null, 0);
            }

            return new EcsRow(e.Id, archetype, Layout.HeapMap(archetype), Layout.NodeCompIndex(ref node));
        }

        /// <summary>
        /// Software prefetch of <paramref name="e"/>'s node, the first read of <see cref="Row"/>: the first stage of a
        /// prefetch for a loop that reads the entity's row a few iterations later (<see cref="PrefetchRow{T1, T2, T3, T4}"/>
        /// is the second). Reads nothing but the store's node array; no effect without SSE or for an id out of range.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void PrefetchNode(Entity e)
        {
            EntityStore store = Layout.Store(e);

            if (!System.Runtime.Intrinsics.X86.Sse.IsSupported || store == null)
            {
                return;
            }

            EntityNode[] nodes = Layout.Nodes(store);

            if ((uint)e.Id < (uint)nodes.Length)
            {
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(ref nodes[e.Id]));
            }
        }

        /// <summary>
        /// The second prefetch stage: reads <paramref name="e"/>'s node (prefetched by <see cref="PrefetchNode"/>) and
        /// prefetches the row's <typeparamref name="T1"/>..<typeparamref name="T4"/> (types Friflo keeps in a plain array
        /// and already read once; others are skipped). A stale or deleted entity prefetches nothing or a wrong slot, which is
        /// harmless: nothing is read through the prefetched address.
        /// </summary>
        public static void PrefetchRow<T1, T2, T3, T4>(Entity e)
            where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent
        {
            EntityStore store = Layout.Store(e);

            if (!System.Runtime.Intrinsics.X86.Sse.IsSupported || store == null || Layout.HeapMapOffset.Value <= 0)
            {
                return;
            }

            EntityNode[] nodes = Layout.Nodes(store);

            if ((uint)e.Id >= (uint)nodes.Length)
            {
                return;
            }

            ref EntityNode node = ref nodes[e.Id];
            Archetype archetype = Layout.NodeArchetype(ref node);

            if (archetype == null)
            {
                return;
            }

            object[] heapMap = Layout.HeapMap(archetype);
            int compIndex = Layout.NodeCompIndex(ref node);
            Prefetch<T1>(heapMap, compIndex);
            Prefetch<T2>(heapMap, compIndex);
            Prefetch<T3>(heapMap, compIndex);
            Prefetch<T4>(heapMap, compIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Prefetch<T>(object[] heapMap, int compIndex) where T : struct, IComponent
        {
            int slot = Slot<T>.Value;

            if (slot <= 0 || slot > heapMap.Length)
            {
                return;
            }

            object heap = heapMap[slot - 1];

            if (heap == null)
            {
                return;
            }

            T[] components = Access<T>.Components(heap);

            if ((uint)compIndex < (uint)components.Length)
            {
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(ref components[compIndex]));
            }
        }

        /// <summary>The Friflo internals read here are what this code expects (false: every read throws).</summary>
        public static bool Supported => Layout.Supported;

        // not NoInlining: the JIT sees that it never returns and moves the call out of the hot path
        private static void ThrowDeleted() => throw new NullReferenceException("the entity is deleted");

        // The hot paths read only statics of classes without a static constructor (Slot<T>, Layout.HeapMapOffset) and call
        // out of line only for the first read of a type and for types in another heap kind, with no struct argument or
        // result, so neither the JIT nor NativeAOT adds class-initialisation checks and the entity and the row stay in
        // registers.

        /// <summary>The component type's schema index + 1 (the heap map index) if Friflo keeps it in a plain array
        /// (<c>StructHeapGen&lt;T&gt;</c>), read directly; -1 if it keeps it in another heap kind, read through Friflo's
        /// own <c>GetComponent</c> / <c>TryGetComponent</c> / <c>EntityData</c> on every read; 0 until the first read,
        /// which also goes through Friflo. The hot paths test only <c>Value &lt;= 0</c>, the branch they had before.</summary>
        internal static class Slot<T> where T : struct, IComponent
        {
            public static int Value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void Init()
            {
                Layout.Init();
                int index = EntityStore.GetEntitySchema().GetComponentType<T>().StructIndex;
                Value = InPlainArray() ? index + 1 : -1;
            }

            /// <summary>Friflo 4.0.0-preview.2's choice of heap (<c>ComponentType&lt;T&gt;.CreateHeap</c> with
            /// <c>SimdUtils.GetLayout</c>): a type with a public float, Vector2, Vector3, Vector4 or Quaternion field named
            /// Value (or value) that is the whole struct gets an aligned or SoA heap, except a Vector3 without [SoA] or
            /// [AoSoA]; every other type gets <c>StructHeapGen&lt;T&gt;</c>. EcsReadTests checks this against the heap
            /// Friflo creates, for every mirror component and every special kind.</summary>
            internal static bool InPlainArray()
            {
                int fields = SimdInfo<T>.FieldCountSoA;

                if (fields == 0)
                {
                    return true;
                }

                if (typeof(T).IsDefined(typeof(SoAAttribute), false) || typeof(T).IsDefined(typeof(AoSoAAttribute), false))
                {
                    return false;
                }

                return fields == 3;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static ref T Get(EntityStore store, int id, short revision)
            {
                if (Value == 0)
                {
                    Init();
                }

                return ref Layout.MakeEntity(store, id, revision).GetComponent<T>();
            }

            // The TryGet fallbacks return a ref to this thread's copy of the value, or a null ref for "not found": an out
            // argument (the caller's value or a bool) whose address went to a call would live on the stack, and be
            // stored to on every read, in the direct path too.
            [ThreadStatic]
            private static T _found;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static ref T TryGet(EntityStore store, int id, short revision)
            {
                if (Value == 0)
                {
                    Init();
                }

                if (Layout.MakeEntity(store, id, revision).TryGetComponent(out T value))
                {
                    _found = value;

                    return ref _found;
                }

                return ref Unsafe.NullRef<T>();
            }

            // a row's fallbacks take its four fields, not the row: a struct argument would keep the row in memory
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static bool Has(object[] heapMap, Archetype archetype, int id, int compIndex)
            {
                if (Value == 0)
                {
                    Init();
                }

                return Layout.MakeData(heapMap, archetype, id, compIndex).Has<T>();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static ref T Get(object[] heapMap, Archetype archetype, int id, int compIndex)
            {
                if (Value == 0)
                {
                    Init();
                }

                return ref Layout.MakeData(heapMap, archetype, id, compIndex).Get<T>();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static ref T TryGet(object[] heapMap, Archetype archetype, int id, int compIndex)
            {
                if (Value == 0)
                {
                    Init();
                }

                if (Layout.MakeData(heapMap, archetype, id, compIndex).TryGet(out T value))
                {
                    _found = value;

                    return ref _found;
                }

                return ref Unsafe.NullRef<T>();
            }
        }

        internal static class Access<T> where T : struct, IComponent
        {
            // the accessor checks the heap's class (a sealed-class compare); only types whose Slot is positive (plain array
            // heaps) get here, so the check never fails unless Slot.InPlainArray is wrong (then InvalidCastException, never
            // a wrong read)
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "components")]
            public static extern ref T[] Components([UnsafeAccessorType("Friflo.Engine.ECS.StructHeapGen`1[[!0]], Friflo.Engine.ECS")] object heap);
        }

        /// <summary>The Friflo 4.0.0-preview.2 internals, and the check that they are what this code expects.</summary>
        internal static class Layout
        {
            /// <summary><c>Archetype.heapMap</c>'s offset from the first field + 1; 0 until checked, -1 if not found.</summary>
            internal static class HeapMapOffset
            {
                public static int Value;
            }

            /// <summary>Why <see cref="Supported"/> is false, for the test and the exception.</summary>
            public static string ProbeError;

            public static readonly bool Supported = Probe();

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "nodes")]
            public static extern ref EntityNode[] Nodes(EntityStore store);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "archetype")]
            public static extern ref Archetype NodeArchetype(ref EntityNode node);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "compIndex")]
            public static extern ref int NodeCompIndex(ref EntityNode node);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "revision")]
            public static extern ref short NodeRevision(ref EntityNode node);

            /// <summary><c>Entity</c>'s fields (<c>store</c> at 0, <c>Id</c> at 8, <c>Revision</c> at 12), read by value
            /// (Unsafe.BitCast) so the entity stays in registers; an accessor would need it by <c>ref</c>. The probe checks
            /// it.</summary>
            private struct EntityLayout
            {
                public EntityStore Store;
                public int Id;
                public short Revision;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ref EntityNode Node(Entity e) => ref Nodes(Unsafe.BitCast<Entity, EntityLayout>(e).Store)[e.Id];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static EntityStore Store(Entity e) => Unsafe.BitCast<Entity, EntityLayout>(e).Store;

            /// <summary>The entity from its fields (the fallbacks take them as scalars, see <see cref="Slot{T}"/>).</summary>
            public static Entity MakeEntity(EntityStore store, int id, short revision) =>
                Unsafe.BitCast<EntityLayout, Entity>(new EntityLayout { Store = store, Id = id, Revision = revision });

            /// <summary><c>EntityData</c> from an <see cref="EcsRow"/>'s fields (the probe checks the layout).</summary>
            public static EntityData MakeData(object[] heapMap, Archetype archetype, int id, int compIndex)
            {
                var d = new DataLayout { HeapMap = heapMap, Archetype = archetype, Id = id, CompIndex = compIndex };

                return Unsafe.As<DataLayout, EntityData>(ref d);
            }

            /// <summary><c>Archetype.heapMap</c> (a <c>StructHeap[]</c>, so an <c>object[]</c> by array covariance), read at
            /// the offset <see cref="Probe"/> found. A field's offset is fixed for the process.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static object[] HeapMap(Archetype archetype) =>
                Unsafe.As<byte, object[]>(ref Unsafe.Add(ref Unsafe.As<RawObject>(archetype).Data, HeapMapOffset.Value - 1));

            /// <summary>Runs the probe once; throws when the internals do not match.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Init()
            {
                if (!Supported)
                {
                    throw new InvalidOperationException(
                        $"EcsRead: the Friflo.Engine.ECS internals are not those of 4.0.0-preview.2 ({ProbeError}); adapt or remove EcsRead (docs/dev/ecs-friflo.md)");
                }
            }

            private sealed class RawObject
            {
                public byte Data;
            }

            /// <summary><c>EntityData</c>'s fields in 4.0.0-preview.2, in order (the probe's reference for the heap map;
            /// same field types in the same order, so the runtime lays both out alike, and the probe checks it).</summary>
            private struct DataLayout
            {
                public object[] HeapMap;
                public Archetype Archetype;
                public int Id;
                public int CompIndex;
            }

            private static bool Probe()
            {
                HeapMapOffset.Value = -1;

                if (Unsafe.SizeOf<Entity>() != Unsafe.SizeOf<EntityLayout>() || Unsafe.SizeOf<EntityData>() != Unsafe.SizeOf<DataLayout>())
                {
                    ProbeError = $"Entity is {Unsafe.SizeOf<Entity>()} bytes, EntityData {Unsafe.SizeOf<EntityData>()}";

                    return false;
                }

                try
                {
                    // two stores, three archetypes; the second entity of an archetype has a non-zero component index
                    var a = new EntityStore();
                    var b = new EntityStore();
                    a.CreateEntity(new Serial { Value = 1 });
                    Entity[] probes =
                    {
                        a.CreateEntity(new Serial { Value = 2 }), a.CreateEntity(new Serial { Value = 3 }, new Hue { Value = 4 }),
                        b.CreateEntity(new Hue { Value = 5 }), b.CreateEntity(new Hue { Value = 6 })
                    };

                    if (!FindHeapMapOffset(probes[1]))
                    {
                        ProbeError = "Archetype.heapMap not found";

                        return false;
                    }

                    int hue = EntityStore.GetEntitySchema().GetComponentType<Hue>().StructIndex;

                    foreach (Entity e in probes)
                    {
                        EntityLayout el = Unsafe.BitCast<Entity, EntityLayout>(e);
                        EntityData data = e.Data;
                        DataLayout d = Unsafe.As<EntityData, DataLayout>(ref data);
                        ref EntityNode node = ref Node(e);

                        if (!ReferenceEquals(el.Store, e.Store) || el.Id != e.Id || el.Revision != e.Revision ||
                            d.Id != e.Id || !ReferenceEquals(d.Archetype, e.Archetype) || d.HeapMap == null ||
                            !ReferenceEquals(NodeArchetype(ref node), d.Archetype) || NodeCompIndex(ref node) != d.CompIndex ||
                            NodeRevision(ref node) != e.Revision || !ReferenceEquals(HeapMap(d.Archetype), d.HeapMap) ||
                            e.Archetype.EntityIds[d.CompIndex] != e.Id ||
                            e.HasComponent<Hue>() && !Unsafe.AreSame(ref Access<Hue>.Components(d.HeapMap[hue])[d.CompIndex], ref e.GetComponent<Hue>()))
                        {
                            ProbeError = "the entity node, the archetype or the heap does not match Entity.Data";
                            HeapMapOffset.Value = -1;

                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    ProbeError = ex.Message;
                    HeapMapOffset.Value = -1;

                    return false;
                }
            }

            // Looks for the heap map reference among the archetype's first pointer-sized slots, comparing addresses as
            // integers (never loading a non-reference slot as a reference). A GC that moves the map between two reads only
            // makes a slot miss, so it retries; the match is then checked with a typed read.
            private static bool FindHeapMapOffset(Entity e)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    EntityData data = e.Data;
                    DataLayout d = Unsafe.As<EntityData, DataLayout>(ref data);
                    Archetype archetype = d.Archetype;
                    object[] map = d.HeapMap;
                    ref byte raw = ref Unsafe.As<RawObject>(archetype).Data;

                    // the CLR lays a class's reference fields out first, and Archetype has fewer than 16 of them (and over
                    // 200 bytes of fields)
                    for (int off = 0; off < 128; off += IntPtr.Size)
                    {
                        if (Unsafe.As<byte, nint>(ref Unsafe.Add(ref raw, off)) == Unsafe.As<object[], nint>(ref map))
                        {
                            HeapMapOffset.Value = off + 1;

                            if (ReferenceEquals(HeapMap(archetype), map))
                            {
                                return true;
                            }

                            HeapMapOffset.Value = -1;
                        }
                    }
                }

                return false;
            }
        }
    }

    /// <summary><c>EntityData</c> through <see cref="EcsRead"/>: the entity's heap map and component index, read once.
    /// Valid until the next structural change of the store (see <see cref="EcsRead"/>).</summary>
    public readonly ref struct EcsRow
    {
        // four fields, as EntityData: the JIT keeps a struct of up to four fields in registers
        private readonly object[] _heapMap;
        private readonly Archetype _archetype;
        private readonly int _id;
        private readonly int _compIndex;

        internal EcsRow(int id, Archetype archetype, object[] heapMap, int compIndex)
        {
            _id = id;
            _archetype = archetype;
            _heapMap = heapMap;
            _compIndex = compIndex;
        }

        /// <summary>The entity was deleted (or the row is default).</summary>
        public bool IsNull => _archetype == null;

        /// <summary>The entity's id, as <c>EntityData.Id</c>.</summary>
        public int Id => _id;

        public Archetype Archetype => _archetype;

        public ref readonly Tags Tags => ref _archetype.Tags;

        // the row exists only after EcsRead.Row, which set the heap map offset; a component type's slot may still be 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has<T>() where T : struct, IComponent
        {
            int slot = EcsRead.Slot<T>.Value;

            if (slot <= 0)
            {
                return EcsRead.Slot<T>.Has(_heapMap, _archetype, _id, _compIndex);
            }

            // a deleted entity's row has no heap map: NullReferenceException, as EntityData.Has
            return _heapMap[slot - 1] != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get<T>() where T : struct, IComponent
        {
            int slot = EcsRead.Slot<T>.Value;

            if (slot <= 0)
            {
                return ref EcsRead.Slot<T>.Get(_heapMap, _archetype, _id, _compIndex);
            }

            return ref EcsRead.Access<T>.Components(_heapMap[slot - 1])[_compIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet<T>(out T value) where T : struct, IComponent
        {
            int slot = EcsRead.Slot<T>.Value;

            if (slot <= 0)
            {
                ref T found = ref EcsRead.Slot<T>.TryGet(_heapMap, _archetype, _id, _compIndex);

                if (Unsafe.IsNullRef(ref found))
                {
                    value = default;

                    return false;
                }

                value = found;

                return true;
            }

            object heap = _heapMap[slot - 1];

            if (heap == null)
            {
                value = default;

                return false;
            }

            value = EcsRead.Access<T>.Components(heap)[_compIndex];

            return true;
        }
    }
}
