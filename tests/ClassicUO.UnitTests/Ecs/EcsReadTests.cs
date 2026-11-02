using System;
using System.Collections.Generic;
using System.Linq;
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

        // every component type of the mirror has the plain array heap, so no read takes the GetComponent fallback
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
            bool same = Unsafe.AreSame(ref EcsRead.Get<T>(e), ref e.GetComponent<T>());

            return same;
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

        // preview.2 stores a component whose only field is a float named Value in an aligned heap (StructAoSFloat), not
        // a plain array: the helper refuses to read it (the accessor's class check), it never reads the wrong memory
        private struct Fallen : IComponent
        {
            public float Value;
        }

        [Fact]
        public void A_Component_In_Another_Heap_Kind_Is_Refused()
        {
            var store = new EntityStore();
            Entity e = Make(store, 1, false);
            Entity f = Make(store, 2, false);
            f.AddComponent(new Fallen { Value = 8 });

            Assert.Equal(8, f.GetComponent<Fallen>().Value);
            Assert.Throws<InvalidCastException>(() => EcsRead.Get<Fallen>(f));
            Assert.Throws<InvalidCastException>(() => EcsRead.Row(f).Get<Fallen>());
            Assert.Throws<InvalidCastException>(() => EcsRead.TryGet(f, out Fallen _));
            Assert.False(EcsRead.TryGet(e, out Fallen _));
            Assert.False(EcsRead.Row(e).TryGet(out Fallen _));
        }

        [Fact]
        public void A_Default_Row_Is_Null()
        {
            EcsRow row = default;
            Assert.True(row.IsNull);
        }
    }
}
