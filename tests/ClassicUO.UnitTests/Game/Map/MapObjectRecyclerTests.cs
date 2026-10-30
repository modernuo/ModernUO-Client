using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using Xunit;

namespace ClassicUO.UnitTests.Game.Map
{
    public class MapObjectRecyclerTests
    {
        private static Land NewLand(World world) =>
            (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);

        private static Static NewStatic(World world) => new Static(world);

        private static IEnumerable<FieldInfo> AllFields(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return f;
                }
            }
        }

        private static bool IsWorld(FieldInfo f) => f.FieldType == typeof(World);

        private static bool IsIncarnation(FieldInfo f) => f.Name == nameof(GameObject.Incarnation);

        // a value different from the type's default (and from the constructor's usual initialisers)
        private static object Dirty(Type t, object current, int salt)
        {
            if (t == typeof(bool)) return !(bool)current;
            if (t == typeof(byte)) return (byte)((byte)current + 7 + salt);
            if (t == typeof(sbyte)) return (sbyte)((sbyte)current + 7 + salt);
            if (t == typeof(short)) return (short)((short)current + 7 + salt);
            if (t == typeof(ushort)) return (ushort)((ushort)current + 7 + salt);
            if (t == typeof(int)) return (int)current + 7 + salt;
            if (t == typeof(uint)) return (uint)current + 7u + (uint)salt;
            if (t == typeof(long)) return (long)current + 7 + salt;
            if (t == typeof(ulong)) return (ulong)current + 7ul + (ulong)salt;
            if (t == typeof(float)) return (float)current + 7.5f + salt;
            if (t == typeof(double)) return (double)current + 7.5 + salt;
            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(current) + 1);

            if (t.IsValueType)
            {
                object boxed = RuntimeHelpers.GetObjectValue(current); // a copy: the caller compares against current
                int i = 0;

                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    f.SetValue(boxed, Dirty(f.FieldType, f.GetValue(boxed), salt + i++));
                }

                return boxed;
            }

            // a reference: always a new, non-null instance, so a field ResetForReuse leaves out can't pass by staying null
            if (t == typeof(string))
            {
                return "dirty" + salt;
            }

            if (t.IsArray)
            {
                return Array.CreateInstance(t.GetElementType(), 1 + salt % 3);
            }

            if (typeof(Delegate).IsAssignableFrom(t))
            {
                throw new NotSupportedException($"no sentinel for delegate type {t}: teach Dirty about it");
            }

            if (t.IsAbstract || t.IsInterface)
            {
                // GameObject-typed fields (TNext, Previous...) get a Static; any other abstract type needs a case here
                if (t.IsAssignableFrom(typeof(Static)))
                {
                    return RuntimeHelpers.GetUninitializedObject(typeof(Static));
                }

                throw new NotSupportedException($"no sentinel for abstract type {t}: teach Dirty about it");
            }

            return RuntimeHelpers.GetUninitializedObject(t);
        }

        private static void DirtyAll(GameObject obj)
        {
            int salt = 0;

            foreach (FieldInfo f in AllFields(obj.GetType()))
            {
                if (IsWorld(f))
                {
                    continue;
                }

                object before = f.GetValue(obj);
                object after = Dirty(f.FieldType, before, salt++);
                f.SetValue(obj, after);
                object stored = f.GetValue(obj);

                // every field must really change, or the reset check could not see a field ResetForReuse misses
                Assert.True(
                    f.FieldType.IsValueType ? !Equals(before, stored) : stored != null && !ReferenceEquals(before, stored),
                    $"could not dirty {f.DeclaringType.Name}.{f.Name}"
                );
            }
        }

        private static void AssertSameState(GameObject expected, GameObject actual, uint expectedIncarnation)
        {
            foreach (FieldInfo f in AllFields(expected.GetType()))
            {
                if (IsWorld(f))
                {
                    Assert.Same(f.GetValue(expected), f.GetValue(actual));

                    continue;
                }

                if (IsIncarnation(f))
                {
                    Assert.Equal(expectedIncarnation, (uint)f.GetValue(actual));

                    continue;
                }

                object e = f.GetValue(expected), a = f.GetValue(actual);

                Assert.True(f.FieldType.IsValueType ? Equals(e, a) : ReferenceEquals(e, a),
                    $"{f.DeclaringType.Name}.{f.Name} is not reset by ResetForReuse: expected {e ?? "null"}, got {a ?? "null"}");
            }
        }

        [Fact]
        public void ResetForReuse_Puts_Every_Land_Field_Back_To_The_Constructors_Value()
        {
            var world = new World();
            Land fresh = NewLand(world);
            Land reused = NewLand(world);
            DirtyAll(reused);
            uint incarnation = reused.Incarnation;

            reused.ResetForReuse();

            AssertSameState(fresh, reused, incarnation + 1);
        }

        [Fact]
        public void ResetForReuse_Puts_Every_Static_Field_Back_To_The_Constructors_Value()
        {
            var world = new World();
            Static fresh = NewStatic(world);
            Static reused = NewStatic(world);
            DirtyAll(reused);
            uint incarnation = reused.Incarnation;

            reused.ResetForReuse();

            AssertSameState(fresh, reused, incarnation + 1);
        }

        [Fact]
        public void The_Field_Check_Catches_A_Field_ResetForReuse_Misses()
        {
            // the check itself: a dirtied object that is not reset differs from a fresh one in some field
            var world = new World();
            Static fresh = NewStatic(world);
            Static dirty = NewStatic(world);
            DirtyAll(dirty);

            Assert.ThrowsAny<Exception>(() => AssertSameState(fresh, dirty, dirty.Incarnation));
        }

        [Fact]
        public void The_Field_Check_Catches_A_Reference_Field_Left_Out_Of_The_Reset()
        {
            // a nullable reference cache that ResetForReuse forgot: simulated by dirtying it again after the reset
            var world = new World();
            Static fresh = NewStatic(world);
            Static reused = NewStatic(world);
            DirtyAll(reused);
            uint incarnation = reused.Incarnation;
            reused.ResetForReuse();
            FieldInfo cache = typeof(GameObject).GetField("_textExpiredContainer", BindingFlags.Instance | BindingFlags.NonPublic);
            cache.SetValue(reused, Dirty(cache.FieldType, null, 0));

            Assert.ThrowsAny<Exception>(() => AssertSameState(fresh, reused, incarnation + 1));
        }

        private static Land RetiredLand(World world)
        {
            Land land = NewLand(world);
            land.Destroy();

            return land;
        }

        [Fact]
        public void Retired_Objects_Come_Back_Only_After_The_Quarantine_Oldest_First()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 1000);
            Land a = RetiredLand(world), b = RetiredLand(world);

            recycler.Retire(a, 5000);
            recycler.Retire(b, 5600);

            Assert.True(a.InRecycler);
            Assert.Null(recycler.TakeLand(5000));
            Assert.Null(recycler.TakeLand(5999));
            Assert.Same(a, recycler.TakeLand(6000));
            Assert.Null(recycler.TakeLand(6000));
            Assert.Same(b, recycler.TakeLand(6600));
            Assert.Null(recycler.TakeStatic(10000));
        }

        [Fact]
        public void Quarantine_Survives_Time_Wrapping()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 1000);
            Land a = RetiredLand(world);

            recycler.Retire(a, uint.MaxValue - 100);

            Assert.Null(recycler.TakeLand(uint.MaxValue));
            Assert.Null(recycler.TakeLand(800));
            Assert.Same(a, recycler.TakeLand(900));
        }

        [Fact]
        public void Live_Or_Linked_Objects_And_Other_Types_Are_Never_Pooled()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 0);

            Land live = NewLand(world); // not destroyed
            recycler.Retire(live, 0);

            Land linked = RetiredLand(world);
            linked.TNext = NewLand(world); // still in a tile list
            recycler.Retire(linked, 0);

            var item = ClassicUO.Game.GameObjects.Item.Create(world, 0x40000001);
            // destroyed without Entity.Destroy's network side (no client in tests)
            typeof(GameObject).GetProperty(nameof(GameObject.IsDestroyed)).SetValue(item, true);
            recycler.Retire(item, 0);

            Assert.False(live.InRecycler);
            Assert.False(linked.InRecycler);
            Assert.Equal(0, recycler.PooledLands);
            Assert.Equal(0, recycler.PooledStatics);
            Assert.Null(recycler.TakeLand(100));
            world.Clear();
        }

        [Fact]
        public void Disabled_Recycler_Marks_But_Never_Reuses()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(false, 0);
            Land a = RetiredLand(world);

            recycler.Retire(a, 0);

            Assert.True(a.InRecycler);
            Assert.Null(recycler.TakeLand(100000));
        }

        [Fact]
        public void Prefilled_Objects_Are_Retired_And_Handed_Out_At_Once_Before_Later_Retirees()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 1000);

            recycler.Prefill(world, 3, 2, 5000);
            Land later = RetiredLand(world);
            recycler.Retire(later, 5000);

            Assert.Equal(4, recycler.PooledLands);
            Assert.Equal(2, recycler.PooledStatics);

            for (int i = 0; i < 3; i++)
            {
                // no quarantine wait: they were never in a tile list
                Land land = recycler.TakeLand(5000);
                Assert.NotNull(land);
                Assert.NotSame(later, land);
                Assert.True(land.IsDestroyed);
                Assert.True(land.InRecycler);
                Assert.Null(land.TileChunk);
                Assert.Null(land.TNext);
                Assert.Null(land.TPrevious);
            }

            Assert.Null(recycler.TakeLand(5999));
            Assert.Same(later, recycler.TakeLand(6000));

            Static st = recycler.TakeStatic(5000);
            Assert.NotNull(st);
            Assert.True(st.IsDestroyed && st.InRecycler);
            Assert.NotNull(recycler.TakeStatic(5000));
            Assert.Null(recycler.TakeStatic(5000));
        }

        [Fact]
        public void Prefill_Is_Capped_At_A_Full_Pool_And_Tops_Up_Only()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 0);

            recycler.Prefill(world, 2, 0, 0);
            recycler.Prefill(world, 5, 0, 0);
            Assert.Equal(5, recycler.PooledLands);

            recycler.Prefill(world, MapObjectRecycler.MaxLands + 10, 0, 0);
            Assert.Equal(MapObjectRecycler.MaxLands, recycler.PooledLands);
        }

        [Fact]
        public void Disabled_Recycler_Is_Not_Prefilled()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(false, 0);

            recycler.Prefill(world, 10, 10, 0);

            Assert.Equal(0, recycler.PooledLands);
            Assert.Equal(0, recycler.PooledStatics);
        }

        [Fact]
        public void A_Prefilled_Object_Reset_For_Reuse_Is_Like_A_New_One()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 0);
            recycler.Prefill(world, 1, 1, 0);

            Land land = recycler.TakeLand(0);
            uint landIncarnation = land.Incarnation;
            land.ResetForReuse();
            AssertSameState(NewLand(world), land, landIncarnation + 1);

            Static st = recycler.TakeStatic(0);
            uint staticIncarnation = st.Incarnation;
            st.ResetForReuse();
            AssertSameState(NewStatic(world), st, staticIncarnation + 1);
        }

        [Fact]
        public void Prefilled_Chunks_Come_From_The_Pool_With_A_Presized_Statics_Mesh()
        {
            var world = new World();
            Chunk.Prefill(world, 2, 128);

            Assert.True(Chunk.PooledCount >= 2);

            Chunk c = Chunk.Create(world, 3, 4);
            Assert.False(c.IsDestroyed);
            Assert.Equal(3, c.X);
            Assert.Equal(4, c.Y);
            Assert.True(c.Mesh.Statics.Vertices.Length >= 128);
            Assert.Equal(0, c.Mesh.Statics.Count);
            Assert.Null(c.Node);
        }

        [Fact]
        public void A_Reused_Land_Is_Not_Destroyed_And_Has_A_New_Incarnation()
        {
            var world = new World();
            var recycler = new MapObjectRecycler(true, 0);
            Land a = RetiredLand(world);
            uint inc = a.Incarnation;
            recycler.Retire(a, 0);

            Land again = recycler.TakeLand(0);
            again.ResetForReuse();

            Assert.Same(a, again);
            Assert.False(again.IsDestroyed);
            Assert.False(again.InRecycler);
            Assert.Equal(inc + 1, again.Incarnation);
        }
    }
}
