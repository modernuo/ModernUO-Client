using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using Friflo.Engine.ECS;
using Xunit;
using Position = ClassicUO.Ecs.Position;

namespace ClassicUO.UnitTests.Ecs
{
    // EcsRead (the direct component read) gives the answers Friflo's GetComponent / TryGetComponent / Data give, as refs
    // to the same storage, through the structural changes that move or reallocate component storage.
    public class EcsReadTests
    {
        private struct Spare : IComponent
        {
            public int Value;
        }

        private struct Marker : ITag { }

        private static Entity Make(EntityStore store, uint serial, bool withHue)
        {
            Entity e = store.CreateEntity(new Serial { Value = serial }, new Graphic { Value = (ushort)(serial * 7) },
                new Position { X = (ushort)serial, Y = (ushort)(serial >> 3), Z = (sbyte)(serial % 50) });

            if (withHue)
            {
                e.AddComponent(new Hue { Value = (ushort)(serial ^ 0x5A5A) });
            }

            return e;
        }

        private static void AssertSame(Entity e)
        {
            Assert.True(Unsafe.AreSame(ref EcsRead.Get<Serial>(e), ref e.GetComponent<Serial>()));
            Assert.True(Unsafe.AreSame(ref EcsRead.Get<Graphic>(e), ref e.GetComponent<Graphic>()));
            Assert.True(Unsafe.AreSame(ref EcsRead.Get<Position>(e), ref e.GetComponent<Position>()));

            EcsRow row = EcsRead.Row(e);
            EntityData data = e.Data;
            Assert.False(row.IsNull);
            Assert.True(Unsafe.AreSame(ref row.Get<Serial>(), ref data.Get<Serial>()));
            Assert.True(Unsafe.AreSame(ref row.Get<Position>(), ref data.Get<Position>()));
            Assert.Equal(data.Tags, row.Tags);
            Assert.Equal(data.Has<Hue>(), row.Has<Hue>());
            Assert.Same(e.Archetype, row.Archetype);

            Assert.Equal(e.TryGetComponent(out Hue a), EcsRead.TryGet(e, out Hue b));
            Assert.Equal(a.Value, b.Value);
            Assert.Equal(data.TryGet(out Hue c), row.TryGet(out Hue d));
            Assert.Equal(c.Value, d.Value);

            if (e.HasComponent<Hue>())
            {
                Assert.True(Unsafe.AreSame(ref EcsRead.Get<Hue>(e), ref e.GetComponent<Hue>()));
                Assert.True(Unsafe.AreSame(ref row.Get<Hue>(), ref data.Get<Hue>()));
            }
        }

        [Fact]
        public void The_Friflo_Internals_Are_Found()
        {
            Assert.True(EcsRead.Supported, EcsRead.Layout.ProbeError);
        }

        // every component type of the mirror has the plain array heap, so every read after the first is direct
        [Fact]
        public void Every_Mirror_Component_Reads_Directly()
        {
            MethodInfo check = typeof(EcsReadTests).GetMethod(nameof(ReadsDirectly), BindingFlags.NonPublic | BindingFlags.Static);
            Type[] types = typeof(Serial).Assembly.GetTypes()
                .Where(t => t.IsValueType && !t.IsGenericType && typeof(IComponent).IsAssignableFrom(t)).ToArray();
            Assert.NotEmpty(types);

            foreach (Type t in types)
            {
                Assert.True((bool)check.MakeGenericMethod(t).Invoke(null, null), t.Name);
            }
        }

        private static bool ReadsDirectly<T>() where T : struct, IComponent
        {
            var store = new EntityStore();
            Entity e = store.CreateEntity();
            e.AddComponent(default(T));
            bool same = Unsafe.AreSame(ref EcsRead.Get<T>(e), ref e.GetComponent<T>()) &&
                Unsafe.AreSame(ref EcsRead.Get<T>(e), ref e.GetComponent<T>());

            return same && EcsRead.Slot<T>.Value > 0 && HeapClass<T>(e).StartsWith("StructHeapGen", StringComparison.Ordinal);
        }

        // the class of the heap Friflo created for T in e's archetype
        private static string HeapClass<T>(Entity e) where T : struct, IComponent
        {
            var map = (object[])typeof(Archetype).GetField("heapMap", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(e.Archetype);

            return map[EntityStore.GetEntitySchema().GetComponentType<T>().StructIndex].GetType().Name;
        }

        [Fact]
        public void Reads_Match_GetComponent_Over_Mixed_Archetypes()
        {
            var store = new EntityStore();
            var rnd = new Random(5);
            var all = new List<Entity>();

            for (uint i = 1; i <= 3000; i++)
            {
                Entity e = Make(store, i, rnd.Next(2) == 0);

                if (rnd.Next(3) == 0)
                {
                    e.AddTag<OnGround>();
                }

                all.Add(e);
            }

            foreach (Entity e in all)
            {
                AssertSame(e);
            }
        }

        [Fact]
        public void A_Write_Through_The_Helper_Is_The_Components_Value()
        {
            var store = new EntityStore();
            Entity e = Make(store, 9, true);
            EcsRead.Get<Hue>(e).Value = 0x1234;
            Assert.Equal(0x1234, e.GetComponent<Hue>().Value);
            EcsRead.Row(e).Get<Position>().X = 77;
            Assert.Equal(77, e.GetComponent<Position>().X);
        }

        // the validity rule: nothing is cached across structural changes; a read after one finds the entity's new place
        [Fact]
        public void Reads_Follow_Archetype_Moves()
        {
            var store = new EntityStore();
            var list = new List<Entity>();

            for (uint i = 1; i <= 50; i++)
            {
                list.Add(Make(store, i, false));
            }

            Entity e = list[10];
            Archetype before = EcsRead.Row(e).Archetype;
            e.AddComponent(new Hue { Value = 3 });
            Assert.NotSame(before, EcsRead.Row(e).Archetype);
            Assert.Equal(3, EcsRead.Get<Hue>(e).Value);
            AssertSame(e);

            e.AddTag<Marker>();
            AssertSame(e);
            e.RemoveComponent<Hue>();
            Assert.False(EcsRead.TryGet(e, out Hue _));
            Assert.False(EcsRead.Row(e).Has<Hue>());
            AssertSame(e);

            // the entity that filled the hole the move left in the old archetype
            foreach (Entity o in list)
            {
                AssertSame(o);
                Assert.Equal(o.GetComponent<Serial>().Value, EcsRead.Get<Serial>(o).Value);
            }
        }

        [Fact]
        public void Reads_Follow_Deletes_And_Throw_For_The_Deleted()
        {
            var store = new EntityStore();
            var list = new List<Entity>();

            for (uint i = 1; i <= 100; i++)
            {
                list.Add(Make(store, i, true));
            }

            Entity gone = list[3];
            gone.DeleteEntity();
            list.RemoveAt(3);

            Assert.Throws<NullReferenceException>(() => gone.GetComponent<Serial>());
            Assert.Throws<NullReferenceException>(() => EcsRead.Get<Serial>(gone));
            Assert.Throws<NullReferenceException>(() => gone.TryGetComponent(out Serial _));
            Assert.Throws<NullReferenceException>(() => EcsRead.TryGet(gone, out Serial _));
            Assert.True(gone.Data.IsNull);
            Assert.True(EcsRead.Row(gone).IsNull);

            // a new entity reusing the id has a new revision: the old handle stays dead
            Entity reuse = Make(store, 999, true);

            if (reuse.Id == gone.Id)
            {
                Assert.Throws<NullReferenceException>(() => EcsRead.Get<Serial>(gone));
            }

            foreach (Entity o in list)
            {
                AssertSame(o);
            }

            AssertSame(reuse);
        }

        [Fact]
        public void A_Missing_Component_Throws_Like_GetComponent()
        {
            var store = new EntityStore();
            Entity e = Make(store, 4, false);
            Assert.Throws<NullReferenceException>(() => e.GetComponent<Hue>());
            Assert.Throws<NullReferenceException>(() => EcsRead.Get<Hue>(e));
            Assert.Throws<NullReferenceException>(() => EcsRead.Row(e).Get<Hue>());
            Assert.False(EcsRead.TryGet(e, out Hue h));
            Assert.Equal(0, h.Value);
        }

        // growth past an archetype's capacity replaces its component arrays: a read after it reads the new array
        [Fact]
        public void Reads_Follow_Storage_Growth()
        {
            var store = new EntityStore();
            Entity first = Make(store, 1, true);
            ref Hue old = ref EcsRead.Get<Hue>(first);
            old.Value = 0x77;

            var list = new List<Entity> { first };

            for (uint i = 2; i <= 5000; i++)
            {
                list.Add(Make(store, i, true));
            }

            // the old ref may point into the replaced array (the documented rule: re-read after a structural change)
            Assert.Equal(0x77, EcsRead.Get<Hue>(first).Value);
            Assert.True(Unsafe.AreSame(ref EcsRead.Get<Hue>(first), ref first.GetComponent<Hue>()));

            foreach (Entity o in list)
            {
                AssertSame(o);
            }
        }

        [Fact]
        public void Reads_Work_Across_Stores()
        {
            var a = new EntityStore();
            var b = new EntityStore();
            Entity ea = Make(a, 1, true);
            Entity eb = Make(b, 2, true);
            Assert.Equal(1u, EcsRead.Get<Serial>(ea).Value);
            Assert.Equal(2u, EcsRead.Get<Serial>(eb).Value);
            AssertSame(ea);
            AssertSame(eb);
        }

        [Fact]
        public void A_Test_Component_Reads_Directly()
        {
            var store = new EntityStore();
            Entity e = Make(store, 1, false);
            Assert.Throws<NullReferenceException>(() => EcsRead.Get<Spare>(e));
            Assert.False(EcsRead.TryGet(e, out Spare _));
            e.AddComponent(new Spare { Value = 42 });
            Assert.Equal(42, EcsRead.Get<Spare>(e).Value);
            Assert.True(Unsafe.AreSame(ref EcsRead.Get<Spare>(e), ref e.GetComponent<Spare>()));
        }

        // preview.2's other heap kinds (ComponentType<T>.CreateHeap): a struct that is one float, Vector2, Vector4 or
        // Quaternion field named Value (or value) gets an aligned heap (StructAoSFloat/Vector2/Vector4); with [SoA] or
        // [AoSoA], one Vector2..4 field gets an SoA or AoSoA heap. EcsRead reads them through Friflo's own calls.
        private struct AoSFloat : IComponent { public float Value; }

        private struct AoSFloatLower : IComponent { public float value; }

        private struct AoSVector2 : IComponent { public Vector2 Value; }

        private struct AoSVector4 : IComponent { public Vector4 Value; }

        private struct AoSQuaternion : IComponent { public Quaternion Value; }

        [SoA] private struct SoAVector2 : IComponent { public Vector2 Value; }

        [SoA] private struct SoAVector3 : IComponent { public Vector3 Value; }

        [SoA] private struct SoAVector4 : IComponent { public Vector4 Value; }

        [AoSoA] private struct AoSoAVector2 : IComponent { public Vector2 Value; }

        [AoSoA] private struct AoSoAVector3 : IComponent { public Vector3 Value; }

        [AoSoA] private struct AoSoAVector4 : IComponent { public Vector4 Value; }

        // near misses that stay in a plain array: a Vector3 without an attribute, a Value with another field, another name
        private struct PlainVector3 : IComponent { public Vector3 Value; }

        private struct PlainFloatPair : IComponent { public float Value; public int Other; }

        private struct PlainSpeed : IComponent { public float Speed; }

        [Fact]
        public void Aligned_Heap_Components_Read_Like_Friflo()
        {
            ReadsLikeFriflo(i => new AoSFloat { Value = i * 1.5f }, false, "StructAoSFloat");
            ReadsLikeFriflo(i => new AoSFloatLower { value = i * 2.5f }, false, "StructAoSFloat");
            ReadsLikeFriflo(i => new AoSVector2 { Value = new Vector2(i, -i) }, false, "StructAoSVector2");
            ReadsLikeFriflo(i => new AoSVector4 { Value = new Vector4(i, 2 * i, 3 * i, 4 * i) }, false, "StructAoSVector4");
            ReadsLikeFriflo(i => new AoSQuaternion { Value = new Quaternion(i, 0, -i, 1) }, false, "StructAoSVector4");
        }

        [Fact]
        public void SoA_Heap_Components_Read_Like_Friflo()
        {
            ReadsLikeFriflo(i => new SoAVector2 { Value = new Vector2(i, -i) }, false, "StructSoAVector2");
            ReadsLikeFriflo(i => new SoAVector3 { Value = new Vector3(i, -i, 2 * i) }, false, "StructSoAVector3");
            ReadsLikeFriflo(i => new SoAVector4 { Value = new Vector4(i, -i, 2 * i, 3) }, false, "StructSoAVector4");
        }

        [Fact]
        public void AoSoA_Heap_Components_Read_Like_Friflo()
        {
            ReadsLikeFriflo(i => new AoSoAVector2 { Value = new Vector2(i, -i) }, false, "StructAoSoAVector2");
            ReadsLikeFriflo(i => new AoSoAVector3 { Value = new Vector3(i, -i, 2 * i) }, false, "StructAoSoAVector3");
            ReadsLikeFriflo(i => new AoSoAVector4 { Value = new Vector4(i, -i, 2 * i, 3) }, false, "StructAoSoAVector4");
        }

        [Fact]
        public void Near_Miss_Components_Read_Directly()
        {
            ReadsLikeFriflo(i => new PlainVector3 { Value = new Vector3(i, -i, 2 * i) }, true, "StructHeapGen");
            ReadsLikeFriflo(i => new PlainFloatPair { Value = i, Other = -i }, true, "StructHeapGen");
            ReadsLikeFriflo(i => new PlainSpeed { Speed = i * 0.5f }, true, "StructHeapGen");
        }

        // Entity, row, Get, TryGet, Has: the same values, refs and exceptions as Friflo, for entities with and without the
        // component (over several archetypes) and a deleted one, at the type's first read (which sets its slot) and after
        private static void ReadsLikeFriflo<T>(Func<int, T> make, bool direct, string heapClass) where T : struct, IComponent
        {
            var store = new EntityStore();
            var with = new List<Entity>();
            var without = new List<Entity>();

            for (int i = 1; i <= 300; i++)
            {
                Entity e = Make(store, (uint)i, i % 2 == 0);

                if (i % 3 != 0)
                {
                    e.AddComponent(make(i));
                    with.Add(e);
                }
                else
                {
                    without.Add(e);
                }
            }

            Entity gone = with[5];
            gone.DeleteEntity();
            with.RemoveAt(5);

            Assert.Equal(0, EcsRead.Slot<T>.Value);
            Assert.StartsWith(heapClass, HeapClass<T>(with[0]));
            Assert.Equal(direct, EcsRead.Slot<T>.InPlainArray());

            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Entity e in with.Concat(without).Append(gone))
                {
                    // the helper's own deleted-entity exception has another message (its type is Friflo's)
                    bool sameMessage = !direct || !e.IsNull;
                    Entity x = e;

                    bool got = SameOutcome(() => x.GetComponent<T>(), () => EcsRead.Get<T>(x), sameMessage);
                    SameOutcome(() => x.Data.Get<T>(), () => EcsRead.Row(x).Get<T>(), true);
                    SameOutcome(() => (x.TryGetComponent(out T v), v), () => (EcsRead.TryGet(x, out T v), v), sameMessage);
                    SameOutcome(() => (x.Data.TryGet(out T v), v), () => (EcsRead.Row(x).TryGet(out T v), v), true);
                    SameOutcome(() => x.Data.Has<T>(), () => EcsRead.Row(x).Has<T>(), true);

                    if (got)
                    {
                        Assert.True(Unsafe.AreSame(ref EcsRead.Get<T>(e), ref e.GetComponent<T>()));
                        Assert.True(Unsafe.AreSame(ref EcsRead.Row(e).Get<T>(), ref e.Data.Get<T>()));
                    }
                }

                Assert.Equal(direct ? EntityStore.GetEntitySchema().GetComponentType<T>().StructIndex + 1 : -1, EcsRead.Slot<T>.Value);
            }

            // a write through the helper's ref is the component's value (kinds whose GetComponent gives a ref)
            Entity w = with[0];

            if (!heapClass.StartsWith("StructSoA", StringComparison.Ordinal) && !heapClass.StartsWith("StructAoSoA", StringComparison.Ordinal))
            {
                T value = make(77);
                EcsRead.Get<T>(w) = value;
                Assert.Equal(value, w.GetComponent<T>());
                EcsRead.Row(w).Get<T>() = make(78);
                Assert.Equal(make(78), w.GetComponent<T>());
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => EcsRead.Get<T>(w));
            }
        }

        // both calls throw the same exception type (and message, if asked) or both return the same value; true if they
        // returned
        private static bool SameOutcome<R>(Func<R> friflo, Func<R> helper, bool sameMessage)
        {
            Exception fe = null, he = null;
            R fr = default, hr = default;

            try
            {
                fr = friflo();
            }
            catch (Exception ex)
            {
                fe = ex;
            }

            try
            {
                hr = helper();
            }
            catch (Exception ex)
            {
                he = ex;
            }

            Assert.Equal(fe?.GetType(), he?.GetType());

            if (fe != null)
            {
                if (sameMessage)
                {
                    Assert.Equal(fe.Message, he.Message);
                }

                return false;
            }

            Assert.Equal(fr, hr);

            return true;
        }

        [Fact]
        public void A_Default_Row_Is_Null()
        {
            EcsRow row = default;
            Assert.True(row.IsNull);
        }
    }
}
