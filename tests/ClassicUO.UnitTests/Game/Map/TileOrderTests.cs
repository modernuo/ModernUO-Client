using ClassicUO.UnitTests.Ecs;
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
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide (MaxDrawZDecisionTests sets it too)
    public sealed class TileOrderTests : IDisposable
    {
        // tiledata for the default branch: plain, tall (+1), background (-1), multi-movable (+1), tall background (0)
        private const ushort Plain = 1, Tall = 2, Background = 3, MultiMovable = 4, TallBackground = 5, Roof = 6;

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
            data[Roof].Flags = TileFlag.Roof;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
            _chunk = new Chunk(_world);
        }

        private readonly StaticTiles[] _savedStaticData;

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
        }

        private static Land NewLand(World world, sbyte z)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);
            land.Z = z;
            return land;
        }

        private Static NewStatic(ushort graphic, sbyte z) => new Static(_world) { Graphic = graphic, Z = z };

        private Multi NewMulti(ushort graphic, sbyte z, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS state = 0) =>
            new Multi(_world) { Graphic = graphic, Z = z, State = state };

        // the chunk is chunk (0, 0), so cell (x, y) is world tile (x, y); objects live at their X/Y (the checker's misplaced test)
        private void Add(GameObject obj, int x = 0, int y = 0)
        {
            obj.X = (ushort)x;
            obj.Y = (ushort)y;
            _chunk.AddGameObject(obj, x, y);
        }

        // the tile's objects in walk order: the list (land, statics, multis) merged with the DynamicTileIndex (T6b part 3)
        private List<GameObject> List(int x = 0, int y = 0)
        {
            var list = new List<GameObject>();

            for (GameObject o = TileWalk.Begin(_chunk, x << 3 | y, out TileWalk w); o != null; o = w.Next())
            {
                list.Add(o);
                Assert.True(list.Count < 100_000, "merged walk does not end");
            }

            return list;
        }

        private List<GameObject> RealList(int x = 0, int y = 0)
        {
            var list = new List<GameObject>();

            for (TileListObject o = _chunk.GetHeadObject(x, y); o != null; o = o.TNext)
            {
                list.Add(o);
                Assert.True(o.TNext == null || o.TNext.TPrevious == o, "broken TPrevious link");
            }

            return list;
        }

        // the expected orders are the legacy list's (the list that held everything until T9)
        private void AssertOrder(params GameObject[] expected)
        {
            List<GameObject> actual = List();
            Assert.Equal(expected, actual);
            Assert.Equal(expected, LegacyTileOrder.Of(_chunk, 0));
            AssertKeySorted();
        }

        private void AssertKeySorted(int x = 0, int y = 0)
        {
            List<GameObject> actual = List(x, y);
            List<GameObject> sorted = actual.OrderBy(TileOrder.Key).ToList(); // OrderBy is stable
            Assert.Equal(sorted, actual);
            Assert.Equal(actual.Where(o => !DynamicTileIndex.Holds(o)).ToList(), RealList(x, y));
            TileOrder.TileCheck r = TileOrder.CheckTile(_chunk, x, y);
            Assert.False(r.Failed, $"out of order {r.OutOfOrder}, cycle {r.Cycle}, broken {r.BrokenLinks}, foreign {r.Foreign}");
            Assert.Equal(RealList(x, y).Count, r.Objects);
            Assert.False(TileOrder.CheckIndex(_chunk, x, y).Failed);
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

            ulong before = a.TileSeq;
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
            ulong seqC = c.TileSeq;
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
            Assert.False(TileOrder.CheckTile(_chunk, 0, 0).Failed);

            // swap a and b by hand: same priority, but a is older, so it must come first
            land.TNext = b; b.TPrevious = land; b.TNext = a; a.TPrevious = b; a.TNext = null;
            TileOrder.TileCheck r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.Equal(1, r.OutOfOrder);
            Assert.Equal(2, r.FirstDescent);
            Assert.Equal(3, r.Objects);
            Assert.True(r.Failed);
        }

        // ---- the sequence and the comparator ----

        [Fact]
        public void Sequence_Crosses_The_32_Bit_Boundary_Without_Breaking_The_Order()
        {
            ulong saved = TileOrder.NextSeq;

            try
            {
                TileOrder.NextSeq = uint.MaxValue - 2;
                Land land = NewLand(_world, 12); // PriorityZ 10
                Add(land);
                Static a = NewStatic(Plain, 10), b = NewStatic(Plain, 10);
                Multi m1 = NewMulti(Plain, 10), m2 = NewMulti(Plain, 10);
                Add(a); // uint.MaxValue
                Add(m1);
                Add(b); // past 2^32: a 32-bit stamp would wrap to 0 here and sort before a
                Add(m2);
                Assert.True(a.TileSeq == uint.MaxValue && b.TileSeq > uint.MaxValue);
                AssertOrder(land, m2, m1, a, b);
            }
            finally
            {
                TileOrder.NextSeq = Math.Max(saved, TileOrder.NextSeq);
            }
        }

        [Fact]
        public void Key_Compares_Priority_Then_Rank_Then_Sequence_By_Rank()
        {
            static int Cmp(short p1, byte r1, ulong s1, short p2, byte r2, ulong s2) =>
                Math.Sign(new TileOrderKey(p1, r1, s1).CompareTo(new TileOrderKey(p2, r2, s2)));

            Assert.Equal(-1, Cmp(-5, 2, 1, 4, 0, 9));                          // priority first
            Assert.Equal(-1, Cmp(short.MinValue, 2, 0, short.MaxValue, 0, 0));
            Assert.Equal(-1, Cmp(3, 0, 1, 3, 1, ulong.MaxValue));              // then rank
            Assert.Equal(-1, Cmp(3, 1, 1, 3, 2, 0));
            Assert.Equal(-1, Cmp(3, 2, 7, 3, 2, 8));                           // rank 2: oldest first
            Assert.Equal(-1, Cmp(3, 2, uint.MaxValue, 3, 2, (ulong)uint.MaxValue + 1));
            Assert.Equal(1, Cmp(3, 1, uint.MaxValue, 3, 1, (ulong)uint.MaxValue + 1)); // ranks 0, 1: newest first
            Assert.Equal(-1, Cmp(3, 0, ulong.MaxValue, 3, 0, ulong.MaxValue - 1));
            Assert.Equal(0, Cmp(3, 2, 5, 3, 2, 5));
        }

        // ---- the checker's traversal ----

        [Fact]
        public void Checker_Reports_A_Cycle_And_Never_Calls_It_Clean()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5);
            Add(a); Add(b);

            b.TNext = a; // a TNext cycle a -> b -> a
            TileOrder.TileCheck r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.True(r.Cycle);
            Assert.True(r.Failed);
            Assert.False(TileOrder.IsReachable(NewStaticOn(_chunk, 0, 0)));

            b.TNext = null;
            a.TPrevious = b; // a TPrevious cycle: no head
            Assert.Null(TileOrder.FindHead(a, out bool cycle));
            Assert.True(cycle);
            Assert.True(TileOrder.CheckTile(_chunk, 0, 0).Cycle || TileOrder.CheckTile(_chunk, 0, 0).Failed);
        }

        private Static NewStaticOn(Chunk chunk, int x, int y) => new Static(_world) { TileChunk = chunk, TileCell = (byte)((x << 3) | y) };

        [Fact]
        public void Checker_Reports_Broken_Links_And_Foreign_Members()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5), c = NewStatic(Plain, 5);
            Add(a); Add(b); Add(c, 1, 1);

            b.TPrevious = land; // a.TNext is b, but b points back at land
            TileOrder.TileCheck r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.Equal(1, r.BrokenLinks);
            Assert.True(r.Failed);
            b.TPrevious = a;

            b.TileCell = (1 << 3) | 1; // says it is on another cell
            r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.Equal(1, r.Foreign);
            Assert.False(TileOrder.IsReachable(b));
            b.TileCell = 0;
            Assert.True(TileOrder.IsReachable(b));
            Assert.True(TileOrder.IsReachable(c));
        }

        [Fact]
        public void Checker_Traverses_Long_Lists_In_Full()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            var all = new List<Static>();

            for (int i = 0; i < 12000; i++)
            {
                Static s = NewStatic(Plain, 5);
                Add(s); // equal priority: appended, so each insertion walks the list once
                all.Add(s);
            }

            TileOrder.TileCheck r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.Equal(12001, r.Objects);
            Assert.False(r.Failed);

            // a descent past the old 10,000 cap
            Static x = all[11000], y = all[11001];
            (x.TileSeq, y.TileSeq) = (y.TileSeq, x.TileSeq);
            r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.Equal(1, r.OutOfOrder);
            Assert.Equal(11002, r.FirstDescent);
            Assert.True(TileOrder.IsReachable(all[11999]));
        }

        // ---- movers re-add on placement ownership, not on list neighbours ----

        [Fact]
        public void Lone_Mobile_On_A_Landless_Tile_Is_Placed_And_A_Step_Moves_It()
        {
            Mobile m = _world.GetOrCreateMobile(0x2000);
            Add(m, 2, 2);
            Assert.True(m.IsPlaced);

            // a finished step: coordinates first, then the re-add (Mobile.ProcessSteps; the Map half of AddToTile is not
            // reachable in a unit test, so this calls the chunk directly, as AddToTile does for a loaded chunk)
            m.X = 3;
            Assert.True(_chunk.Dynamic.Contains(m, (2 << 3) | 2)); // still filed under its old tile until the re-add

            if (m.IsPlaced)
            {
                _chunk.AddGameObject(m, 3, 2);
            }

            Assert.Empty(List(2, 2));
            Assert.Null(_chunk.Tiles[2, 2]);
            Assert.Equal(new GameObject[] { m }, List(3, 2));
            Assert.Equal((3 << 3) | 2, m.TileCell);
            Assert.False(TileOrder.CheckTile(_chunk, 3, 2).Failed);
        }

        [Fact]
        public void Parked_Mover_Is_Placed_Unplaced_Is_Not()
        {
            Mobile m = _world.GetOrCreateMobile(0x2001);
            Assert.False(m.IsPlaced);

            m.PendingBlock = 1234; // Map.Park: off every tile list, waiting for its chunk
            Assert.Null(m.TileChunk);
            Assert.True(m.IsPlaced); // so a step re-adds it, which re-parks it at the new tile

            Add(m, 1, 1); // the chunk loaded: placed for real, PendingBlock cleared by RemoveFromTile
            Assert.Equal(-1, m.PendingBlock);
            Assert.True(m.IsPlaced);

            m.RemoveFromTile();
            Assert.False(m.IsPlaced);
        }

        [Fact]
        public void Boat_Carried_Entities_Alone_On_Landless_Tiles_Follow_The_Boat()
        {
            Item cargo = _world.GetOrCreateItem(0x4000_1000);
            cargo.TestSetGraphic(Plain);
            Mobile rider = _world.GetOrCreateMobile(0x2002);
            Add(cargo, 4, 4);
            Add(rider, 5, 4);

            // BoatMovingManager: each carried entity's X/Y moves with the boat, then it is re-added if placed
            foreach (GameObject e in new GameObject[] { cargo, rider })
            {
                Assert.True(e.IsPlaced);
                e.Y = 5;
                _chunk.AddGameObject(e, e.X, e.Y);
            }

            Assert.Empty(List(4, 4));
            Assert.Empty(List(5, 4));
            Assert.Equal(new GameObject[] { cargo }, List(4, 5));
            Assert.Equal(new GameObject[] { rider }, List(5, 5));

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    Assert.False(TileOrder.CheckTile(_chunk, x, y).Failed);
                }
            }
        }

        [Fact]
        public void Checker_Fails_A_Tile_Whose_Anchor_Is_Not_In_The_Walked_List()
        {
            Land land = NewLand(_world, 0);
            Add(land);
            Static s = NewStatic(Plain, 5);
            s.TileChunk = _chunk; // a stray node: the anchor's TPrevious points at it, it points nowhere
            land.TPrevious = s;
            s.TNext = null;

            TileOrder.TileCheck r = TileOrder.CheckTile(_chunk, 0, 0);
            Assert.True(r.AnchorMissed);
            Assert.Equal(1, r.BrokenLinks);
            Assert.True(r.Failed);
        }

        // ---- forward-only scans start at the head, not at Tiles[x, y] ----

        [Fact]
        public void Near_Roof_And_Map_Z_Scans_See_Objects_Before_An_Interior_Anchor()
        {
            // landless tile: the first object placed (Z 50) is the anchor; a roof at Z 20 and a roof multi at Z 40 sort before it
            Static top = NewStatic(Plain, 50);
            Add(top, 6, 6);
            Static roof = NewStatic(Roof, 20);
            Add(roof, 6, 6);
            Multi roofMulti = NewMulti(Roof, 40);
            Add(roofMulti, 6, 6);
            Assert.Same(top, _chunk.Tiles[6, 6]);
            Assert.Equal(new GameObject[] { roof, roofMulti, top }, List(6, 6));

            // CalculateNearZ at z 24: the roof at 20 (from Tiles[x, y] the scan saw only the anchor and found nothing)
            Assert.Same(roof, ClassicUO.Game.Map.Map.FindNearRoof(_chunk, 6, 6, 24, Chunk.StaticTileData));
            Assert.Same(roofMulti, ClassicUO.Game.Map.Map.FindNearRoof(_chunk, 6, 6, 42, Chunk.StaticTileData));
            Assert.Null(ClassicUO.Game.Map.Map.FindNearRoof(_chunk, 6, 6, 30, Chunk.StaticTileData));

            ClassicUO.Game.Map.Map.ScanMapZ(_chunk, 6, 6, out sbyte groundZ, out sbyte staticZ);
            Assert.Equal(0, groundZ);
            Assert.Equal(50, staticZ);
        }

        [Fact]
        public void On_A_Tile_With_Land_The_Scans_Still_Start_At_The_Land()
        {
            // legacy behaviour, kept: objects sorted before the land are invisible to GetMapZ and the roof cut-off
            Land land = NewLand(_world, 10); // PriorityZ 8, the anchor
            Add(land, 7, 7);
            Static roofBelow = NewStatic(Roof, 5); // PriorityZ 5: sorted before the land
            Add(roofBelow, 7, 7);
            Assert.Same(land, _chunk.Tiles[7, 7]);
            Assert.Equal(new GameObject[] { roofBelow, land }, List(7, 7));

            Assert.Same(land, ClassicUO.Game.Map.Map.ScanStart(_chunk, 7, 7));
            Assert.Null(ClassicUO.Game.Map.Map.FindNearRoof(_chunk, 7, 7, 6, Chunk.StaticTileData)); // a head start would find it

            ClassicUO.Game.Map.Map.ScanMapZ(_chunk, 7, 7, out sbyte groundZ, out sbyte staticZ);
            Assert.Equal(10, groundZ);
            Assert.Equal(0, staticZ); // the Z 5 static before the land is not counted

            // a roof after the land is found as before
            Static roofAbove = NewStatic(Roof, 12);
            Add(roofAbove, 7, 7);
            Assert.Same(roofAbove, ClassicUO.Game.Map.Map.FindNearRoof(_chunk, 7, 7, 14, Chunk.StaticTileData));
        }

        // ---- membership on a landless tile (Chunk.Load leaves a tile empty when the block has no map data) ----

        [Fact]
        public void Landless_Tile_Anchor_Removal_Keeps_The_Rest_Reachable()
        {
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5), c = NewStatic(Plain, 5);
            Add(a); // a is the anchor (Tiles[0, 0])
            Add(b);
            Add(c);
            Assert.Same(a, _chunk.Tiles[0, 0]);

            a.RemoveFromTile();
            Assert.Equal(new GameObject[] { b, c }, List());
            Assert.NotSame(a, _chunk.Tiles[0, 0]);
            Assert.Null(a.TileChunk);

            b.RemoveFromTile();
            c.RemoveFromTile();
            Assert.Null(_chunk.Tiles[0, 0]);
            Assert.Empty(List());
        }

        [Fact]
        public void Landless_Tile_Anchor_Reinsert_And_Move_Back_Stay_In_Their_Own_Tile()
        {
            Static a = NewStatic(Plain, 5), b = NewStatic(Plain, 5), other = NewStatic(Plain, 5);
            Add(other, 4, 4);
            Add(a);
            Add(b);

            // linked-object (2026-11-07): a Static has no container links any more, so the stale-link case (the old "o == obj"
            // branch followed one into another tile's list) cannot be set up; the plain reinsert stays
            Add(a);
            Assert.Equal(new GameObject[] { b, a }, List());
            Assert.Equal(new GameObject[] { other }, List(4, 4));
            AssertKeySorted();

            // move the anchor away and back
            GameObject anchor = _chunk.Tiles[0, 0];
            Add(anchor, 2, 2);
            Assert.Equal(new GameObject[] { anchor }, List(2, 2));
            Assert.Single(List());
            Add(anchor);
            Assert.Equal(2, List().Count);
            Assert.Empty(List(2, 2));
            Assert.Null(_chunk.Tiles[2, 2]);
            AssertKeySorted();

            foreach (GameObject o in List())
            {
                Assert.Same(_chunk, o.TileChunk);
                Assert.Equal(0, o.TileCell);
            }
        }

        [Fact]
        public void TileCell_Is_The_Insertion_Cell_Even_After_X_Y_Change()
        {
            Land land = NewLand(_world, 0);
            Add(land, 3, 5);
            Static s = NewStatic(Plain, 5);
            Add(s, 3, 5);
            Assert.Equal((3 << 3) | 5, s.TileCell);

            s.X = 100; // SetInWorldTile changes X/Y before AddToTile removes the object from its old tile
            s.Y = 200;
            Add(s, 1, 1);
            Assert.Equal(new GameObject[] { land }, List(3, 5));
            Assert.Equal(new GameObject[] { s }, List(1, 1));
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
                    Add(obj, rng.Next(3), 0);
                }
                else if (op < 7)
                {
                    GameObject obj = objects[rng.Next(objects.Count)];
                    obj.Z = (sbyte)rng.Next(0, 6);
                    Add(obj, rng.Next(3), 0);
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
                    objects[rng.Next(objects.Count)].TestSetGraphic((ushort)rng.Next(Plain, TallBackground + 1));
                }

                // cells 0 and 1 have land, cell 2 is landless
                var listed = new HashSet<GameObject>();

                for (int x = 0; x < 3; x++)
                {
                    AssertKeySorted(x, 0);

                    foreach (GameObject o in List(x, 0))
                    {
                        Assert.Same(_chunk, o.TileChunk);
                        Assert.Equal(x << 3, o.TileCell);
                        listed.Add(o);
                    }
                }

                foreach (GameObject o in objects)
                {
                    Assert.Equal(o.TileChunk != null, listed.Contains(o));
                }
            }
        }
    }
}
