using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using Xunit;

namespace ClassicUO.UnitTests.Game.Map
{
    /// <summary>
    /// T6b part 2: the readers that moved to <see cref="TileReader"/> (GetMapZ's and CalculateNearZ's scans, the max-Z
    /// decision, the pathfinder, targeting, foliage, sitting, HasNoExternalData, the season change) give the same results from
    /// the merged walk (the list's land, statics and multis plus the DynamicTileIndex) as the pre-T6b readers over the legacy
    /// list order (<see cref="LegacyTileOrder"/>).
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")]
    public sealed class TileReaderTests : IDisposable
    {
        private const ushort Plain = 1, RoofA = 2, RoofB = 3, Chair = 0x0A00;

        private readonly World _world = new World();
        private readonly Chunk _a;
        private readonly StaticTiles[] _savedStaticData;
        private uint _nextSerial = 0x4000_0100, _nextMobile = 0x100;

        public TileReaderTests()
        {
            var data = new StaticTiles[0x10000];
            data[RoofA].Flags = TileFlag.Roof;
            data[RoofB].Flags = TileFlag.Roof;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
            _a = new Chunk(_world) { X = 0, Y = 0 };
            ChairTable.Table[Chair] = new AnimationsLoader.SittingInfoData(Chair, 1, 2, 3, 4, 5, 6, false);
            ChairTable.Table[Chair + 1] = new AnimationsLoader.SittingInfoData(Chair + 1, 1, 2, 3, 4, 5, 6, false);
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            ChairTable.Table.Remove(Chair);
            ChairTable.Table.Remove((ushort)(Chair + 1));
        }

        private static Land NewLand(World world, sbyte z)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);
            land.Z = z;
            return land;
        }

        private Static NewStatic(ushort graphic, sbyte z) => new Static(_world) { Graphic = graphic, Z = z };

        private Multi NewMulti(ushort graphic, sbyte z) => new Multi(_world) { Graphic = graphic, Z = z };

        private Item NewItem(ushort graphic, sbyte z)
        {
            Item item = _world.GetOrCreateItem(_nextSerial++);
            item.Graphic = graphic;
            item.Z = z;
            return item;
        }

        private Mobile NewMobile(sbyte z)
        {
            Mobile m = _world.GetOrCreateMobile(_nextMobile++);
            m.Z = z;
            return m;
        }

        private void Place(GameObject obj, int x, int y)
        {
            obj.X = (ushort)x;
            obj.Y = (ushort)y;
            _a.AddGameObject(obj, x, y);
        }

        private static List<GameObject> Read(Chunk chunk, int cell, GameObject from)
        {
            var seq = new List<GameObject>();

            for (GameObject o = TileReader.Open(chunk, cell, from, out TileReader r); o != null; o = r.Next())
            {
                seq.Add(o);
                Assert.True(seq.Count < 100_000, "reader does not end");
            }

            return seq;
        }

        // the pre-T6b scans' sequence: the legacy list order from the land anchor (on a tile with land) or from its head
        private static IEnumerable<GameObject> OldScan(Chunk chunk, int cx, int cy)
        {
            List<GameObject> legacy = LegacyTileOrder.Of(chunk, cx << 3 | cy);
            int start = chunk.Tiles[cx, cy] is Land land ? legacy.IndexOf(land) : 0;
            Assert.True(start >= 0);

            return legacy.Skip(start);
        }

        // the pre-T6b readers, kept here as the oracle
        private static void OldScanMapZ(Chunk chunk, int cx, int cy, out sbyte groundZ, out sbyte staticZ)
        {
            groundZ = staticZ = 0;

            foreach (GameObject obj in OldScan(chunk, cx, cy))
            {
                if (obj is Land)
                {
                    groundZ = obj.Z;
                }
                else if (staticZ < obj.Z)
                {
                    staticZ = obj.Z;
                }
            }
        }

        private static GameObject OldNearRoof(Chunk chunk, int cx, int cy, int z, StaticTiles[] tiles)
        {
            foreach (GameObject obj in OldScan(chunk, cx, cy))
            {
                if (!(obj is Static) && !(obj is Multi) || obj.Graphic >= tiles.Length)
                {
                    continue;
                }

                if (!tiles[obj.Graphic].IsRoof || Math.Abs(z - obj.Z) > 6)
                {
                    continue;
                }

                return obj;
            }

            return null;
        }

        private static bool OldFindChair(Mobile m, out AnimationsLoader.SittingInfoData data)
        {
            foreach (GameObject o in LegacyTileOrder.Of(m.TileChunk, m.TileCell))
            {
                if ((o is Item || o is Static || o is Multi) && Math.Abs(m.Z - o.Z) <= 1 && ChairTable.Table.TryGetValue(o.Graphic, out data))
                {
                    return true;
                }
            }

            data = AnimationsLoader.SittingInfoData.Empty;

            return false;
        }

        // Fills the chunk with random objects: some tiles landless, items/mobiles at Z below and above the land, multis, statics.
        private List<GameObject> Fill(Random rng, int operations)
        {
            var placed = new List<GameObject>();

            for (int i = 0; i < operations; i++)
            {
                int x = rng.Next(0, 4), y = rng.Next(0, 4);
                sbyte z = (sbyte)rng.Next(-8, 30);
                GameObject obj;

                switch (rng.Next(0, 7))
                {
                    case 0: obj = (x + y) % 3 == 0 ? NewStatic(Plain, z) : NewLand(_world, (sbyte)rng.Next(-2, 6)); break; // a third of the tiles stay landless
                    case 1: obj = NewStatic(rng.Next(0, 2) == 0 ? RoofA : Plain, z); break;
                    case 2: obj = NewMulti(rng.Next(0, 2) == 0 ? RoofB : Plain, z); break;
                    case 3: obj = NewMobile(z); break;
                    case 4: obj = NewItem(rng.Next(0, 2) == 0 ? RoofA : Chair, z); break; // roof items are not roofs to FindNearRoof
                    default: obj = NewItem(Plain, z); break;
                }

                Place(obj, x, y);
                placed.Add(obj);

                if (rng.Next(0, 6) == 0 && placed.Count > 0)
                {
                    GameObject gone = placed[rng.Next(0, placed.Count)];
                    gone.RemoveFromTile();
                    placed.Remove(gone);
                }
            }

            return placed;
        }

        [Fact]
        public void Reader_Sequence_From_The_Head_And_From_Every_Object_Equals_The_Legacy_List()
        {
            Fill(new Random(11), 400);

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    int cell = x << 3 | y;
                    List<GameObject> list = LegacyTileOrder.Of(_a, cell);
                    Assert.Equal(list, Read(_a, cell, null));

                    for (int i = 0; i < list.Count; i++)
                    {
                        Assert.Equal(list.GetRange(i, list.Count - i), Read(_a, cell, list[i]));
                    }
                }
            }
        }

        [Fact]
        public void Reader_On_A_Chunk_Without_An_Index_Falls_Back_To_The_List()
        {
            var plain = new Chunk(_world) { X = 5, Y = 5 };
            Land land = NewLand(_world, 0);
            land.X = 40;
            land.Y = 40;
            plain.AddGameObject(land, 0, 0);
            Assert.Null(plain.Dynamic);
            Assert.Equal(new List<GameObject> { land }, Read(plain, 0, null));
            Assert.Empty(Read(plain, 9, null));
        }

        [Fact]
        public void GetMapZ_And_CalculateNearZ_Scans_Give_The_Same_Result_From_The_List_And_The_Merged_Walk()
        {
            for (int seed = 0; seed < 6; seed++)
            {
                Fill(new Random(seed), 300);

                for (int x = 0; x < 4; x++)
                {
                    for (int y = 0; y < 4; y++)
                    {
                        OldScanMapZ(_a, x, y, out sbyte g0, out sbyte s0);
                        ClassicUO.Game.Map.Map.ScanMapZ(_a, x, y, out sbyte g, out sbyte s);
                        Assert.Equal((g0, s0), (g, s));

                        for (int z = -8; z < 40; z += 3)
                        {
                            Assert.Same(OldNearRoof(_a, x, y, z, Chunk.StaticTileData), ClassicUO.Game.Map.Map.FindNearRoof(_a, x, y, z, Chunk.StaticTileData));
                        }
                    }
                }
            }
        }

        [Fact]
        public void A_Landless_Tile_Of_Only_Dynamics_Scans_Their_Z()
        {
            Place(NewItem(Plain, 9), 1, 1);
            Place(NewMobile(14), 1, 1);

            ClassicUO.Game.Map.Map.ScanMapZ(_a, 1, 1, out sbyte g, out sbyte s);
            Assert.Equal(((sbyte)0, (sbyte)14), (g, s));
        }

        [Fact]
        public void The_Land_Anchor_Skips_Objects_Sorted_Before_The_Land()
        {
            // legacy scan start (T6a): on a tile with land the scan starts at the land, so a lower-priority object before it is not read
            Land land = NewLand(_world, 10);
            Place(land, 2, 2);
            Item below = NewItem(Plain, 5); // priority below the land's: sorted before it
            Place(below, 2, 2);
            Assert.Same(below, LegacyTileOrder.Of(_a, 2 << 3 | 2)[0]);
            OldScanMapZ(_a, 2, 2, out sbyte g0, out sbyte s0);
            Assert.Equal(((sbyte)10, (sbyte)0), (g0, s0));
            ClassicUO.Game.Map.Map.ScanMapZ(_a, 2, 2, out sbyte g, out sbyte s);
            Assert.Equal((g0, s0), (g, s));
        }

        [Fact]
        public void Chair_Search_Is_The_Legacy_One_And_Sees_Items()
        {
            for (int seed = 0; seed < 6; seed++)
            {
                List<GameObject> placed = Fill(new Random(100 + seed), 300);

                foreach (GameObject o in placed)
                {
                    if (o is Mobile m && m.TileChunk != null)
                    {
                        bool a = OldFindChair(m, out var da);
                        bool b = m.FindChair(out var db);
                        Assert.Equal(a, b);
                        Assert.Equal(da, db);
                    }
                }
            }

            Mobile sitter = NewMobile(4);
            Place(sitter, 3, 3);
            Place(NewItem(Chair, 5), 3, 3); // an item chair at the mobile's Z +1, found only if the walk reaches items
            Assert.True(sitter.FindChair(out var found));
            Assert.Equal(Chair, found.Graphic);
        }

        [Fact]
        public void Chair_Search_Takes_The_First_Chair_In_Tile_Order()
        {
            Mobile sitter = NewMobile(4);
            Place(sitter, 3, 3);
            Item first = NewItem(Chair, 4);
            Place(first, 3, 3);
            Place(NewStatic(Chair + 1, 4), 3, 3);

            Assert.True(sitter.FindChair(out var d));
            Assert.Equal(Chair, d.Graphic);
        }

        [Fact]
        public void An_Unplaced_Mobile_Has_No_Chair()
        {
            Mobile loose = NewMobile(0);
            Assert.False(loose.FindChair(out _));
        }

        [Fact]
        public void HasNoExternalData_Is_False_For_A_Chunk_With_An_Item_Or_Mobile()
        {
            Place(NewLand(_world, 0), 0, 0);
            Place(NewStatic(Plain, 0), 0, 0);
            Assert.True(_a.HasNoExternalData());

            Item item = NewItem(Plain, 0);
            Place(item, 0, 0);
            Assert.False(_a.HasNoExternalData());
            item.RemoveFromTile();
            Assert.True(_a.HasNoExternalData());
            Mobile mob = NewMobile(0);
            Place(mob, 7, 7);
            Assert.False(_a.HasNoExternalData());
            mob.RemoveFromTile();
            Assert.True(_a.HasNoExternalData());
        }

        [Fact]
        public void Readers_Allocate_Nothing_With_The_Checker_Off()
        {
            Assert.False(TileOrder.Check, "the unit tests run with CUO_TILE_ORDER_CHECK off");
            Place(NewLand(_world, 3), 1, 1);
            Place(NewStatic(RoofA, 5), 1, 1);
            Place(NewItem(Plain, 6), 1, 1);
            StaticTiles[] tiles = Chunk.StaticTileData;

            void Run()
            {
                for (int i = 0; i < 200; i++)
                {
                    ClassicUO.Game.Map.Map.ScanMapZ(_a, 1, 1, out _, out _);
                    ClassicUO.Game.Map.Map.FindNearRoof(_a, 1, 1, 5, tiles);
                }
            }

            Run();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void Pathfinder_CreateItemList_Allocates_Nothing_With_The_Checker_Off()
        {
            Assert.False(TileOrder.Check);
            World world = ClassicUO.UnitTests.Ecs.WorldWriterMobileTests.BuildPlayer(out _);
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            var pathfinder = new Pathfinder(world);
            Type pathObject = typeof(Pathfinder).GetNestedType("PathObject", BindingFlags.NonPublic)!;
            Func<Pathfinder, int, bool> call = (Func<Pathfinder, int, bool>)typeof(TileReaderTests).GetMethod(nameof(MakeCreateItemListCaller), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(pathObject).Invoke(null, null)!;
            call(pathfinder, 5); // far from the player at 1000,1000: no chunk is loaded, the call returns early

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 200; i++)
            {
                call(pathfinder, 5);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // perf-alloc-5: a walk into a wall runs CanWalk every frame; CalculateNewZ and CalculateMinMaxZ reuse their tile lists
        // and PathObject is a struct, so a call allocates nothing (before: two lists per call and an object per tile entry)
        [Fact]
        public void Pathfinder_CanWalk_Reuses_Its_Tile_Lists()
        {
            Assert.False(TileOrder.Check);
            Assert.True(typeof(Pathfinder).GetNestedType("PathObject", BindingFlags.NonPublic)!.IsValueType);
            World world = ClassicUO.UnitTests.Ecs.WorldWriterMobileTests.BuildPlayer(out _);
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            // the GM body: CalculateNewZ takes the dead-or-GM step state without the race test (Client.Game is not set up here)
            world.Player.Graphic = 0x03DB;
            var pathfinder = new Pathfinder(world);

            // far from the player at 1000,1000: no chunk is loaded, so both lists stay empty and every walk is refused (a straight
            // step runs CalculateNewZ once, a refused diagonal three times); no Assert inside the loop, xUnit's comparers allocate
            int Run()
            {
                int walked = 0;

                for (int i = 0; i < 200; i++)
                {
                    Direction dir = Direction.East;
                    int x = 5, y = 5;
                    sbyte z = 0;
                    walked += pathfinder.CanWalk(ref dir, ref x, ref y, ref z) ? 1 : 0;
                    dir = Direction.Down;
                    walked += pathfinder.CanWalk(ref dir, ref x, ref y, ref z) ? 1 : 0;
                    walked += x != 5 || y != 5 ? 1 : 0;
                }

                return walked;
            }

            Assert.Equal(0, Run());
            long before = GC.GetAllocatedBytesForCurrentThread();
            int walked = Run();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, walked);
            Assert.Equal(0, allocated);
        }

        // a delegate over the private CreateItemList(List<PathObject>, x, y, step), with one list reused
        private static Func<Pathfinder, int, bool> MakeCreateItemListCaller<T>()
        {
            var open = (Func<Pathfinder, List<T>, int, int, int, bool>)Delegate.CreateDelegate(typeof(Func<Pathfinder, List<T>, int, int, int, bool>),
                typeof(Pathfinder).GetMethod("CreateItemList", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(List<T>), typeof(int), typeof(int), typeof(int) }, null)!);
            var list = new List<T>();

            return (p, x) => open(p, list, x, x, 0);
        }
    }
}
