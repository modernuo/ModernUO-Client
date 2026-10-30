using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using Xunit;

namespace ClassicUO.UnitTests.Game.Map
{
    /// <summary>
    /// T6a: Chunk.AddGameObject's insertion rules, and that TileOrder.Key reproduces the resulting tile-list order.
    /// </summary>
    public sealed class TileOrderTests : IDisposable
    {
        // tiledata for the default branch: plain, tall (+1), background (-1), multi-movable (+1), tall background (0)
        private const ushort Plain = 1, Tall = 2, Background = 3, MultiMovable = 4, TallBackground = 5;

        private readonly World _world = new World();
        private readonly Chunk _chunk;

        public TileOrderTests()
        {
            var data = new StaticTiles[0x10000];
            data[Tall].Height = 5;
            data[Background].Flags = TileFlag.Background;
            data[MultiMovable].Flags = TileFlag.MultiMovable;
            data[TallBackground].Height = 3;
            data[TallBackground].Flags = TileFlag.Background;
            Chunk.StaticDataForTests = data;
            _chunk = new Chunk(_world);
        }

        public void Dispose() => Chunk.StaticDataForTests = null;

        private static Land NewLand(World world, sbyte z)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);
            land.Z = z;
            return land;
        }

        private Static NewStatic(ushort graphic, sbyte z) => new Static(_world) { Graphic = graphic, Z = z };

        private Multi NewMulti(ushort graphic, sbyte z, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS state = 0) =>
            new Multi(_world) { Graphic = graphic, Z = z, State = state };

        private void Add(GameObject obj, int x = 0, int y = 0) => _chunk.AddGameObject(obj, x, y);

        private List<GameObject> List(int x = 0, int y = 0)
        {
            var list = new List<GameObject>();

            for (GameObject o = _chunk.GetHeadObject(x, y); o != null; o = o.TNext)
            {
                list.Add(o);
                Assert.True(o.TNext == null || o.TNext.TPrevious == o, "broken TPrevious link");
            }

            return list;
        }

        private void AssertOrder(params GameObject[] expected)
        {
            List<GameObject> actual = List();
            Assert.Equal(expected, actual);
            AssertKeySorted();
        }

        private void AssertKeySorted(int x = 0, int y = 0)
        {
            List<GameObject> actual = List(x, y);
            List<GameObject> sorted = actual.OrderBy(TileOrder.Key).ToList(); // OrderBy is stable
            Assert.Equal(sorted, actual);
            Assert.Equal(0, TileOrder.CountOutOfOrder(_chunk.GetHeadObject(x, y), out int n));
            Assert.Equal(actual.Count, n);
        }

        [Fact]
        public void Priorities_Are_The_Ones_AddGameObject_Computes()
        {
            Land land = NewLand(_world, 10);
            Add(land);
            Assert.Equal(8, land.PriorityZ); // Z - 2

            Land stretched = NewLand(_world, 10);
            stretched.IsStretched = true;
            stretched.AverageZ = 4;
            Add(stretched, 1);
            Assert.Equal(2, stretched.PriorityZ); // AverageZ - 2

            Static plain = NewStatic(Plain, 10), tall = NewStatic(Tall, 10), bg = NewStatic(Background, 10), mm = NewStatic(MultiMovable, 10);
            Add(plain); Add(tall); Add(bg); Add(mm);
            Assert.Equal(new short[] { 10, 11, 9, 11 }, new[] { plain.PriorityZ, tall.PriorityZ, bg.PriorityZ, mm.PriorityZ });

            Multi generic = NewMulti(Tall, 10, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL);
            Multi preview = NewMulti(Plain, 10, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW);
            Multi normal = NewMulti(Tall, 10);
            Add(generic); Add(preview); Add(normal);
            Assert.Equal(9, generic.PriorityZ); // Z - 1, no tiledata terms
            Assert.Equal(11, preview.PriorityZ); // Z + 1 + tiledata
            Assert.Equal(11, normal.PriorityZ);

            AssertKeySorted();
            AssertKeySorted(1);
        }

        [Fact]
        public void Land_Goes_Before_Every_Equal_Newest_First()
        {
            // a land at Z 12 has PriorityZ 10, like a plain static or a plain multi at Z 10
            Static s = NewStatic(Plain, 10);
            Multi m = NewMulti(Plain, 10);
            Land older = NewLand(_world, 12);
            Add(older);
            Add(s);
            Add(m);
            AssertOrder(older, m, s);

            Land newer = NewLand(_world, 12); // never two lands on one tile in the client, but the rule is general
            Add(newer);
            AssertOrder(newer, older, m, s);
        }

        [Fact]
        public void Multi_Goes_After_Equal_Lands_And_Before_Equal_Others_Newest_First()
        {
            Land land = NewLand(_world, 12); // PriorityZ 10
            Add(land);
            Static s1 = NewStatic(Plain, 10), s2 = NewStatic(Plain, 10);
            Add(s1);
            Multi m1 = NewMulti(Plain, 10);
            Add(m1);
            Add(s2);
            Multi m2 = NewMulti(Plain, 10);
            Add(m2);
            AssertOrder(land, m2, m1, s1, s2);

            // a generic-internal multi is state 1 too, even with the preview flag
            Multi g = NewMulti(Plain, 11, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW);
            Add(g);
            Assert.Equal(10, g.PriorityZ);
            Assert.Equal(1, TileOrder.Rank(g));
            AssertOrder(land, g, m2, m1, s1, s2);
        }

        [Fact]
        public void Preview_Multi_Goes_After_Every_Equal_Oldest_First()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static tall = NewStatic(Tall, 10); // PriorityZ 11
            Add(tall);
            Multi p1 = NewMulti(Plain, 10, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW); // PriorityZ 11
            Add(p1);
            Multi normal = NewMulti(Tall, 10); // PriorityZ 11, rank 1
            Add(normal);
            Multi p2 = NewMulti(Plain, 10, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW);
            Add(p2);
            Assert.Equal(2, TileOrder.Rank(p1));
            AssertOrder(land, normal, tall, p1, p2);
        }

        [Fact]
        public void Equal_Priorities_Keep_Insertion_Order_And_Lower_Priority_Goes_First()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Background, 6), c = NewStatic(Plain, 5), low = NewStatic(Plain, 3), high = NewStatic(Tall, 5);
            Add(a); Add(b); Add(c); Add(low); Add(high);
            // a, b (6 - 1) and c share PriorityZ 5
            AssertOrder(land, low, a, b, c, high);
        }

        [Fact]
        public void Reinsert_Restamps_And_Moves_Behind_Its_Equals()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5);
            Multi m1 = NewMulti(Plain, 5), m2 = NewMulti(Plain, 5);
            Add(a); Add(b); Add(m1); Add(m2);
            AssertOrder(land, m2, m1, a, b);

            uint before = a.TileSeq;
            Add(a); // a move onto the same tile (AddToTile -> RemoveFromTile + AddGameObject)
            Assert.True(a.TileSeq > before);
            AssertOrder(land, m2, m1, b, a);

            Add(m1); // a re-inserted multi becomes the newest, so the first
            AssertOrder(land, m1, m2, b, a);

            // a move to another tile and back
            Add(b, 3, 3);
            AssertOrder(land, m1, m2, a);
            Add(b);
            AssertOrder(land, m1, m2, a, b);
        }

        [Fact]
        public void Graphic_Change_Without_Reinsert_Keeps_The_Stored_Priority()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5), c = NewStatic(Tall, 5);
            Add(a); Add(b); Add(c);
            AssertOrder(land, a, b, c);

            a.Graphic = Tall; // AddGameObject would give 6 now; nothing re-inserts
            Assert.Equal(5, a.PriorityZ);
            AssertOrder(land, a, b, c);

            Add(a); // the re-insert picks up the new priority
            Assert.Equal(6, a.PriorityZ);
            AssertOrder(land, b, c, a);
        }

        [Fact]
        public void Removal_Only_Unlinks()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5), c = NewStatic(Plain, 5);
            Add(a); Add(b); Add(c);
            uint seqC = c.TileSeq;
            b.RemoveFromTile();
            AssertOrder(land, a, c);
            Assert.Equal(seqC, c.TileSeq);
        }

        [Fact]
        public void Checker_Flags_A_List_That_Is_Not_Key_Sorted()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5);
            Add(a); Add(b);
            Assert.Equal(0, TileOrder.CountOutOfOrder(_chunk.GetHeadObject(0, 0), out _));

            // swap a and b by hand: same priority, but a is older, so it must come first
            land.TNext = b; b.TPrevious = land; b.TNext = a; a.TPrevious = b; a.TNext = null;
            Assert.Equal(1, TileOrder.CountOutOfOrder(_chunk.GetHeadObject(0, 0), out int n));
            Assert.Equal(3, n);
        }

        [Fact]
        public void Random_Inserts_And_Moves_Always_Match_The_Key()
        {
            var rng = new Random(1234);
            var objects = new List<GameObject>();

            for (int x = 0; x < 2; x++)
            {
                Add(NewLand(_world, (sbyte)rng.Next(-2, 8)), x, 0);
            }

            for (int i = 0; i < 3000; i++)
            {
                int op = rng.Next(10);

                if (op < 4 || objects.Count == 0)
                {
                    sbyte z = (sbyte)rng.Next(0, 6);
                    ushort g = (ushort)rng.Next(Plain, TallBackground + 1);
                    GameObject obj = rng.Next(5) switch
                    {
                        0 => NewMulti(g, z),
                        1 => NewMulti(g, z, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW),
                        2 => NewMulti(g, z, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL),
                        _ => NewStatic(g, z)
                    };
                    objects.Add(obj);
                    Add(obj, rng.Next(2), 0);
                }
                else if (op < 7)
                {
                    GameObject obj = objects[rng.Next(objects.Count)];
                    obj.Z = (sbyte)rng.Next(0, 6);
                    Add(obj, rng.Next(2), 0);
                }
                else if (op < 8)
                {
                    GameObject obj = objects[rng.Next(objects.Count)];
                    obj.RemoveFromTile();
                    objects.Remove(obj);
                }
                else
                {
                    // graphic change without a re-insert
                    objects[rng.Next(objects.Count)].Graphic = (ushort)rng.Next(Plain, TallBackground + 1);
                }

                AssertKeySorted(0, 0);
                AssertKeySorted(1, 0);
            }
        }
    }
}
