using System;
using System.Collections.Generic;
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
    /// the tile list and from the merged walk (the list's other objects plus the DynamicTileIndex).
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")]
    public sealed class TileReaderTests : IDisposable
    {
        private const ushort Plain = 1, RoofA = 2, RoofB = 3, Chair = 0x0A00;

        private readonly World _world = new World();
        private readonly Chunk _a;
        private readonly StaticTiles[] _savedStaticData;
        private readonly bool _savedMaintain;
        private uint _nextSerial = 0x4000_0100, _nextMobile = 0x100;

        public TileReaderTests()
        {
            var data = new StaticTiles[0x10000];
            data[RoofA].Flags = TileFlag.Roof;
            data[RoofB].Flags = TileFlag.Roof;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
            _savedMaintain = DynamicTileIndex.Maintain;
            DynamicTileIndex.Maintain = true;
            _a = new Chunk(_world) { X = 0, Y = 0 };
            ChairTable.Table[Chair] = new AnimationsLoader.SittingInfoData(Chair, 1, 2, 3, 4, 5, 6, false);
            ChairTable.Table[Chair + 1] = new AnimationsLoader.SittingInfoData(Chair + 1, 1, 2, 3, 4, 5, 6, false);
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            DynamicTileIndex.Maintain = _savedMaintain;
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

        private static List<GameObject> Read(Chunk chunk, int cell, bool merged, GameObject from)
        {
            var seq = new List<GameObject>();

            for (GameObject o = TileReader.Open(chunk, cell, merged, from, out TileReader r); o != null; o = r.Next())
            {
                seq.Add(o);
                Assert.True(seq.Count < 100_000, "reader does not end");
            }

            return seq;
        }

        // the pre-T6b readers, kept here as the oracle
        private static void OldScanMapZ(Chunk chunk, int cx, int cy, out sbyte groundZ, out sbyte staticZ)
        {
            groundZ = staticZ = 0;

            for (GameObject obj = ClassicUO.Game.Map.Map.ScanStart(chunk, cx, cy); obj != null; obj = obj.TNext)
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
            for (GameObject obj = ClassicUO.Game.Map.Map.ScanStart(chunk, cx, cy); obj != null; obj = obj.TNext)
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
        public void Reader_Sequence_From_The_Head_And_From_Every_Object_Equals_The_List()
        {
            Fill(new Random(11), 400);

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    int cell = x << 3 | y;
                    List<GameObject> list = Read(_a, cell, false, null);
                    Assert.Equal(list, Read(_a, cell, true, null));

                    for (int i = 0; i < list.Count; i++)
                    {
                        Assert.Equal(list.GetRange(i, list.Count - i), Read(_a, cell, false, list[i]));
                        Assert.Equal(list.GetRange(i, list.Count - i), Read(_a, cell, true, list[i]));
                    }
                }
            }
        }

        [Fact]
        public void Reader_On_A_Chunk_Without_An_Index_Falls_Back_To_The_List()
        {
            var plain = new Chunk(_world) { X = 5, Y = 5 };
            DynamicTileIndex.Maintain = false;
            Land land = NewLand(_world, 0);
            land.X = 40;
            land.Y = 40;
            plain.AddGameObject(land, 0, 0);
            Assert.Null(plain.Dynamic);
            Assert.Equal(new List<GameObject> { land }, Read(plain, 0, true, null));
            Assert.Empty(Read(plain, 9, true, null));
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

                        foreach (bool merged in new[] { false, true })
                        {
                            ClassicUO.Game.Map.Map.ScanMapZ(_a, x, y, merged, out sbyte g, out sbyte s);
                            Assert.Equal((g0, s0), (g, s));

                            for (int z = -8; z < 40; z += 3)
                            {
                                Assert.Same(OldNearRoof(_a, x, y, z, Chunk.StaticTileData), ClassicUO.Game.Map.Map.FindNearRoof(_a, x, y, z, Chunk.StaticTileData, merged));
                            }
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

            foreach (bool merged in new[] { false, true })
            {
                ClassicUO.Game.Map.Map.ScanMapZ(_a, 1, 1, merged, out sbyte g, out sbyte s);
                Assert.Equal(((sbyte)0, (sbyte)14), (g, s));
            }
        }

        [Fact]
        public void The_Land_Anchor_Skips_Objects_Sorted_Before_The_Land_In_Both_Walks()
        {
            // legacy scan start (T6a): on a tile with land the scan starts at the land, so a lower-priority object before it is not read
            Land land = NewLand(_world, 10);
            Place(land, 2, 2);
            Item below = NewItem(Plain, 5); // priority below the land's: sorted before it
            Place(below, 2, 2);
            Assert.Same(below, _a.GetHeadObject(2, 2));
            OldScanMapZ(_a, 2, 2, out sbyte g0, out sbyte s0);
            Assert.Equal(((sbyte)10, (sbyte)0), (g0, s0));

            foreach (bool merged in new[] { false, true })
            {
                ClassicUO.Game.Map.Map.ScanMapZ(_a, 2, 2, merged, out sbyte g, out sbyte s);
                Assert.Equal((g0, s0), (g, s));
            }
        }

        [Fact]
        public void Chair_Search_Is_The_Same_In_Both_Walks_And_Sees_Items()
        {
            for (int seed = 0; seed < 6; seed++)
            {
                List<GameObject> placed = Fill(new Random(100 + seed), 300);

                foreach (GameObject o in placed)
                {
                    if (o is Mobile m && m.TileChunk != null)
                    {
                        bool a = m.FindChair(false, out var da);
                        bool b = m.FindChair(true, out var db);
                        Assert.Equal(a, b);
                        Assert.Equal(da, db);
                    }
                }
            }

            Mobile sitter = NewMobile(4);
            Place(sitter, 3, 3);
            Place(NewItem(Chair, 5), 3, 3); // an item chair at the mobile's Z +1, found only if the walk reaches items
            Assert.True(sitter.FindChair(true, out var found));
            Assert.Equal(Chair, found.Graphic);
            Assert.True(sitter.FindChair(false, out found));
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

            foreach (bool merged in new[] { false, true })
            {
                Assert.True(sitter.FindChair(merged, out var d));
                Assert.Equal(Chair, d.Graphic);
            }
        }

        [Fact]
        public void An_Unplaced_Mobile_Has_No_Chair_In_Either_Walk()
        {
            Mobile loose = NewMobile(0);
            Assert.False(loose.FindChair(false, out _));
            Assert.False(loose.FindChair(true, out _));
        }

        [Fact]
        public void HasNoExternalData_Is_False_For_A_Chunk_With_An_Item_Or_Mobile_In_Both_Walk_Settings()
        {
            Place(NewLand(_world, 0), 0, 0);
            Place(NewStatic(Plain, 0), 0, 0);
            Assert.True(_a.HasNoExternalData());

            Item item = NewItem(Plain, 0);
            Place(item, 0, 0);
            Assert.False(_a.HasNoExternalData());
#if CUO_DEVTOOLS
            bool saved = DynamicTileIndex.WalkOn;
            DynamicTileIndex.WalkOn = true;

            try
            {
                Assert.False(_a.HasNoExternalData());
                item.RemoveFromTile();
                Assert.True(_a.HasNoExternalData());
                Mobile mob = NewMobile(0);
                Place(mob, 7, 7);
                Assert.False(_a.HasNoExternalData());
                mob.RemoveFromTile();
                Assert.True(_a.HasNoExternalData());
            }
            finally
            {
                DynamicTileIndex.WalkOn = saved;
            }
#endif
        }
    }
}
