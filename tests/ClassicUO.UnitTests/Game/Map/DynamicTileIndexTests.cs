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
    /// T6b: the per-tile dynamic index (items, mobiles) that Chunk.AddGameObject and RemoveFromTile maintain beside the tile
    /// list, and the merged order TileWalk reads (the list's other objects merged by key with the index), which must equal the
    /// list while both are maintained.
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData and DynamicTileIndex.Maintain are process-wide
    public sealed class DynamicTileIndexTests : IDisposable
    {
        private const ushort Plain = 1, Tall = 2, Background = 3, MultiMovable = 4, TallBackground = 5;

        private readonly World _world = new World();
        private readonly Chunk _a, _b;
        private readonly StaticTiles[] _savedStaticData;
        private readonly bool _savedMaintain, _savedEffectsHeld;
        private uint _nextSerial = 0x4000_0100, _nextMobile = 0x100;

        public DynamicTileIndexTests()
        {
            var data = new StaticTiles[0x10000];
            data[Tall].Height = 5;
            data[Background].Flags = TileFlag.Background;
            data[MultiMovable].Flags = TileFlag.MultiMovable;
            data[TallBackground].Height = 3;
            data[TallBackground].Flags = TileFlag.Background;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
            _savedMaintain = DynamicTileIndex.Maintain;
            _savedEffectsHeld = DynamicTileIndex.EffectsHeld;
            DynamicTileIndex.Maintain = true;
            _a = new Chunk(_world) { X = 0, Y = 0 };
            _b = new Chunk(_world) { X = 1, Y = 0 };
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            DynamicTileIndex.Maintain = _savedMaintain;
            DynamicTileIndex.EffectsHeld = _savedEffectsHeld;
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

        // chunk _a is chunk (0, 0), _b chunk (1, 0): cell (x, y) of _b is world tile (8 + x, y)
        private static void Place(Chunk chunk, GameObject obj, int x, int y)
        {
            obj.X = (ushort)(chunk.X * 8 + x);
            obj.Y = (ushort)(chunk.Y * 8 + y);
            chunk.AddGameObject(obj, x, y);
        }

        private static List<GameObject> List(Chunk chunk, int x, int y)
        {
            var list = new List<GameObject>();

            for (GameObject o = chunk.GetHeadObject(x, y); o != null; o = o.TNext)
            {
                list.Add(o);
            }

            return list;
        }

        private static List<GameObject> Merged(Chunk chunk, int x, int y)
        {
            var list = new List<GameObject>();

            for (GameObject o = TileWalk.Begin(chunk, x << 3 | y, out TileWalk w); o != null; o = w.Next())
            {
                list.Add(o);
                Assert.True(list.Count < 100_000, "merged walk does not end");
            }

            return list;
        }

        private static List<GameObject> Indexed(Chunk chunk, int x, int y) =>
            chunk.Dynamic == null ? new List<GameObject>() : chunk.Dynamic.Cell(x << 3 | y).ToArray().Select(e => e.Obj).ToList();

        // the merged order is the list; the index holds exactly the list's items and mobiles, in list order; the checker agrees
        private static void AssertMerged(Chunk chunk, int x, int y)
        {
            List<GameObject> list = List(chunk, x, y);
            Assert.Equal(list, Merged(chunk, x, y));
            Assert.Equal(list.Where(DynamicTileIndex.Holds).ToList(), Indexed(chunk, x, y));
            TileOrder.IndexCheck r = TileOrder.CheckIndex(chunk, x, y);
            Assert.False(r.Failed, $"order {r.OrderMismatches}, bad entries {r.BadEntries}, count {r.CountMismatch}");
            Assert.False(TileOrder.CheckTile(chunk, x, y).Failed);
        }

        private void AssertAll()
        {
            foreach (Chunk c in new[] { _a, _b })
            {
                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        AssertMerged(c, x, y);
                    }
                }
            }
        }

        // The render-list walk's start (AddTileToRenderList calls TileWalk.Start). The walk itself needs a scene; this is the
        // entry point it uses for the whole-tile / single-object decision.
        private static List<GameObject> WalkFrom(GameObject first, Chunk chunk, bool single, bool walkOn)
        {
            var seen = new List<GameObject>();
            GameObject stopAt = single ? first.TNext : null;

            for (GameObject o = TileWalk.Start(first, chunk, single, walkOn, out TileWalk w, out bool merged); o != stopAt; o = merged ? w.Next() : o.TNext)
            {
                seen.Add(o);
                Assert.True(seen.Count < 100_000, "walk does not end");
            }

            return seen;
        }

        [Fact]
        public void Single_Object_Walk_Of_A_Tail_Object_Does_Not_Restart_At_The_Tile_Head()
        {
            // Codex P2: the tile's last object has TNext == null, the same value a whole-tile call passes as "no stop"
            Place(_a, NewLand(_world, 0), 2, 2);
            Item item = NewItem(Plain, 0);
            Place(_a, item, 2, 2);
            Mobile m1 = NewMobile(0), m2 = NewMobile(0);
            Place(_a, m1, 2, 2);
            Place(_a, m2, 2, 2);
            List<GameObject> list = List(_a, 2, 2);
            Assert.Null(list[^1].TNext);

            foreach (bool walkOn in new[] { true, false })
            {
                foreach (GameObject o in list)
                {
                    Assert.Equal(new[] { o }, WalkFrom(o, _a, true, walkOn));
                }

                Assert.Equal(list, WalkFrom(list[0], _a, false, walkOn));
            }
        }

        [Fact]
        public void Whole_Tile_Walk_From_A_Tail_Start_Is_The_Merged_Order()
        {
            Place(_a, NewStatic(Plain, 0), 1, 1);
            Place(_a, NewItem(Plain, 0), 1, 1);
            Place(_a, NewMobile(0), 1, 1);
            List<GameObject> list = List(_a, 1, 1);
            Assert.Equal(list, WalkFrom(list[0], _a, false, true));
            Assert.Equal(list, WalkFrom(list[0], _a, false, false));
        }

        [Fact]
        public void Holds_Items_And_Mobiles_Only()
        {
            Assert.True(DynamicTileIndex.Holds(NewItem(Plain, 0)));
            Assert.True(DynamicTileIndex.Holds(NewMobile(0)));
            Assert.False(DynamicTileIndex.Holds(NewStatic(Plain, 0)));
            Assert.False(DynamicTileIndex.Holds(NewMulti(Plain, 0)));
            Assert.False(DynamicTileIndex.Holds(NewLand(_world, 0)));
        }

        [Fact]
        public void Equal_Keys_Keep_Insertion_Order_Among_Statics_Items_And_Mobiles()
        {
            Land land = NewLand(_world, 0);
            Place(_a, land, 1, 1);
            Item i1 = NewItem(Plain, 0);
            Static s1 = NewStatic(Plain, 0);
            Item i2 = NewItem(Plain, 0);
            Mobile m = NewMobile(-1); // priority Z + 1 = 0, the items' and statics' priority
            Static s2 = NewStatic(Plain, 0);
            Item i3 = NewItem(Plain, 0);

            foreach (GameObject o in new GameObject[] { i1, s1, i2, m, s2, i3 })
            {
                Place(_a, o, 1, 1);
            }

            Assert.Equal(new GameObject[] { i1, i2, m, i3 }, Indexed(_a, 1, 1));
            Assert.Equal(new GameObject[] { land, i1, s1, i2, m, s2, i3 }, Merged(_a, 1, 1));
            AssertMerged(_a, 1, 1);
        }

        [Fact]
        public void Priorities_Interleave_Statics_Multis_And_Dynamics()
        {
            Place(_a, NewLand(_world, 5), 0, 0);
            Item corpse = NewItem(0x2006, 0); // corpse: Z + 1
            Item tall = NewItem(Tall, 0);      // Z + 1
            Item back = NewItem(Background, 2); // Z - 1
            Mobile m = NewMobile(3);           // Z + 1
            Multi multi = NewMulti(Plain, 1);  // rank 1 at its priority
            Multi preview = NewMulti(Plain, 1, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW);
            Multi inner = NewMulti(Plain, 2, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL);

            foreach (GameObject o in new GameObject[] { corpse, NewStatic(MultiMovable, 0), tall, back, m, multi, preview, inner, NewStatic(TallBackground, 1), NewItem(Plain, 1) })
            {
                Place(_a, o, 0, 0);
                AssertMerged(_a, 0, 0);
            }

            Assert.Equal(1, corpse.PriorityZ);
            Assert.Equal(4, m.PriorityZ);
        }

        [Fact]
        public void Removal_And_Reinsert_Follow_The_List()
        {
            Place(_a, NewLand(_world, 0), 2, 2);
            Item a = NewItem(Plain, 0), b = NewItem(Plain, 0), c = NewItem(Plain, 0);
            Place(_a, a, 2, 2);
            Place(_a, b, 2, 2);
            Place(_a, c, 2, 2);

            b.RemoveFromTile();
            Assert.Equal(new GameObject[] { a, c }, Indexed(_a, 2, 2));
            AssertMerged(_a, 2, 2);

            Place(_a, a, 2, 2); // re-add: restamped, behind its equals
            Assert.Equal(new GameObject[] { c, a }, Indexed(_a, 2, 2));
            AssertMerged(_a, 2, 2);

            a.RemoveFromTile();
            c.RemoveFromTile();
            Assert.Equal(0, _a.Dynamic.Count);
            Assert.Null(_a.Dynamic.RawCell(2 << 3 | 2, out _)); // the array went back to the pool
            AssertMerged(_a, 2, 2);
        }

        [Fact]
        public void Moves_Across_Chunks_And_Mobile_Steps_Refile_Every_Placed_Object()
        {
            for (int x = 0; x < 8; x++)
            {
                Place(_a, NewLand(_world, 0), x, 0);
                Place(_b, NewLand(_world, 0), x, 0);
            }

            Mobile m = NewMobile(0);
            Item item = NewItem(Plain, 0);
            Place(_a, m, 6, 0);
            Place(_a, item, 7, 0);

            // a mobile stepping east tile by tile into the next chunk (Mobile.ProcessSteps: X first, then the re-add)
            for (int wx = 7; wx < 11; wx++)
            {
                Chunk to = wx < 8 ? _a : _b;
                Assert.True(m.IsPlaced);
                m.X = (ushort)wx;
                to.AddGameObject(m, wx & 7, 0);
                AssertAll();
            }

            Assert.Same(_b, m.TileChunk);
            Assert.Equal(new GameObject[] { m }, Indexed(_b, 2, 0));
            Assert.DoesNotContain(m, Indexed(_a, 7, 0));

            // an item moved by a packet into the other chunk (SetInWorldTile's AddToTile)
            Place(_b, item, 0, 0);
            Assert.Empty(Indexed(_a, 7, 0));
            AssertAll();
        }

        [Fact]
        public void Park_And_Unpark_Leave_And_Rejoin_The_Index()
        {
            Place(_a, NewLand(_world, 0), 3, 3);
            Item item = NewItem(Plain, 0);
            Place(_a, item, 3, 3);

            // Map.Park: off every tile list (RemoveFromTile), parked until the chunk loads
            item.RemoveFromTile();
            item.PendingBlock = 77;
            Assert.Empty(Indexed(_a, 3, 3));
            AssertMerged(_a, 3, 3);

            // the chunk loads: the deferred add is a plain AddGameObject
            Place(_a, item, 3, 3);
            Assert.Equal(-1, item.PendingBlock);
            Assert.Equal(new GameObject[] { item }, Indexed(_a, 3, 3));
            AssertMerged(_a, 3, 3);
        }

        private FixedEffect NewEffect(sbyte z = 0)
        {
            var fx = new FixedEffect(_world, _world.Effects, 1, 1, z, 0x3779, 0, 100, 0);
            fx.RemoveFromTile(); // the constructor placed it (no map: nothing to remove); Place adds it to the test chunk

            return fx;
        }

        [Fact]
        public void Effects_Are_Held_Only_With_The_T8_Switch()
        {
            bool held = DynamicTileIndex.EffectsHeld;
            try
            {
                FixedEffect fx = NewEffect();
                DynamicTileIndex.EffectsHeld = false;
                Assert.False(DynamicTileIndex.Holds(fx));
                DynamicTileIndex.EffectsHeld = true;
                Assert.True(DynamicTileIndex.Holds(fx));
                Assert.True(DynamicTileIndex.Holds(NewItem(Plain, 0)));
                Assert.False(DynamicTileIndex.Holds(NewStatic(Plain, 0)));
            }
            finally
            {
                DynamicTileIndex.EffectsHeld = held;
            }
        }

        [Theory]
        [InlineData(false, false, false, false, false)] // everything off: no index
        [InlineData(true, false, false, false, true)]   // the walk
        [InlineData(false, true, false, false, true)]   // CUO_WORLD_TILE_INDEX=0
        [InlineData(false, false, true, false, true)]   // CUO_TILE_ORDER_CHECK
        [InlineData(false, false, false, true, true)]   // ecs_effects alone (walk off, no overrides): effects still indexed
        public void The_Index_Is_Maintained_When_Any_Reader_Needs_It(bool walk, bool envOff, bool orderCheck, bool effects, bool expected)
        {
            Assert.Equal(expected, DynamicTileIndex.ShouldMaintain(walk, envOff, orderCheck, effects));
        }

        [Fact]
        public void Peek_Chunk_Neither_Loads_A_Chunk_Nor_Touches_Its_Access_Time()
        {
            var map = new ClassicUO.Game.Map.Map(_world, 0, 4, 4);
            Assert.Null(map.PeekChunk(9, 9));
            Assert.Null(map.PeekChunk(-1, 3));
            Assert.Null(map.PeekChunk(9999, 9999));
            Assert.Null(map.GetChunk(9, 9, false));

            map.LoadEmptyChunkForTest(1, 1);
            Chunk c = map.GetChunk(9, 9, false);
            c.LastAccessTime = 12345;
            Assert.Same(c, map.PeekChunk(9, 9));
            Assert.Equal(12345, c.LastAccessTime);
            map.GetChunk(9, 9, false);
            Assert.NotEqual(12345, c.LastAccessTime); // GetChunk does touch it (the behavior the checker must not have)
        }

        [Fact]
        public void Effects_Interleave_With_Items_And_Mobiles_By_The_Plus_Two_Priority_And_Move_Between_Tiles()
        {
            DynamicTileIndex.EffectsHeld = true;
            Place(_a, NewLand(_world, 0), 2, 2);
            Place(_a, NewStatic(Tall, 0), 2, 2);
            Mobile m = NewMobile(0);
            Place(_a, m, 2, 2);
            FixedEffect fx = NewEffect();
            Place(_a, fx, 2, 2);
            Item item = NewItem(Plain, 0);
            Place(_a, item, 2, 2);
            FixedEffect fx2 = NewEffect(6);
            Place(_a, fx2, 2, 2);
            Assert.Equal(new GameObject[] { fx, fx2 }, Indexed(_a, 2, 2).Where(o => o is GameEffect).ToArray());
            AssertMerged(_a, 2, 2);

            // an effect moves (SetInWorldTile: out of the old cell, into the new one, in another chunk too)
            fx.RemoveFromTile();
            Place(_b, fx, 3, 4);
            Assert.DoesNotContain(fx, Indexed(_a, 2, 2));
            Assert.Contains(fx, Indexed(_b, 3, 4));
            AssertAll();

            fx2.RemoveFromTile();
            fx.RemoveFromTile();
            m.RemoveFromTile();
            AssertAll();
            Assert.Equal(new GameObject[] { item }, Indexed(_a, 2, 2));
        }

        [Fact]
        public void An_Effect_On_A_Landless_Tile_And_A_Parked_Effect_Keep_The_Index_Consistent()
        {
            DynamicTileIndex.EffectsHeld = true;
            FixedEffect fx = NewEffect();
            Place(_a, fx, 5, 5);
            Assert.Equal(new GameObject[] { fx }, Indexed(_a, 5, 5));
            AssertMerged(_a, 5, 5);

            fx.RemoveFromTile();
            fx.PendingBlock = 77;
            Assert.Empty(Indexed(_a, 5, 5));
            Place(_a, fx, 5, 5);
            Assert.Equal(new GameObject[] { fx }, Indexed(_a, 5, 5));
            AssertAll();
        }

        [Fact]
        public void Multis_And_Statics_Stay_Out_Of_The_Index()
        {
            Place(_a, NewLand(_world, 0), 4, 4);
            Multi house = NewMulti(Plain, 0);
            Static s = NewStatic(Plain, 0);
            Item i = NewItem(Plain, 0);
            Place(_a, house, 4, 4);
            Place(_a, s, 4, 4);
            Place(_a, i, 4, 4);
            Assert.Equal(new GameObject[] { i }, Indexed(_a, 4, 4));

            house.RemoveFromTile();
            Assert.Equal(new GameObject[] { i }, Indexed(_a, 4, 4));
            AssertMerged(_a, 4, 4);
        }

        [Fact]
        public void Landless_Tile_With_Only_Dynamics()
        {
            Mobile m = NewMobile(0);
            Item i = NewItem(Plain, 5);
            Place(_a, i, 5, 5);
            Place(_a, m, 5, 5);
            AssertMerged(_a, 5, 5);

            i.RemoveFromTile(); // the anchor leaves (ReleaseAnchor)
            Assert.Equal(new GameObject[] { m }, Merged(_a, 5, 5));
            AssertMerged(_a, 5, 5);
        }

        [Fact]
        public void Chunk_Destroy_And_Reload_Clear_The_Index()
        {
            Place(_a, NewLand(_world, 0), 0, 0);
            Place(_a, NewItem(Plain, 0), 0, 0);
            Place(_a, NewMobile(0), 0, 0);
            _a.ClearForReload();
            Assert.Equal(0, _a.Dynamic.Count);
            AssertMerged(_a, 0, 0);

            Place(_a, NewLand(_world, 0), 1, 0);
            Place(_a, NewItem(Plain, 0), 1, 0);
            _a.Destroy();
            Assert.Equal(0, _a.Dynamic.Count);
        }

        [Fact]
        public void Chunk_Clear_Drops_The_Player_Entry_Too()
        {
            // the chunk's teardown unlinks the player without destroying it (no RemoveFromTile), so the index must be cleared
            // wholesale, or the player would stay filed in a dead chunk and be filed twice once it is re-added
            // (PlayerMobile's constructor needs the UO files: an uninitialised one with its world and the field initialisers
            // AddGameObject reads is enough here)
            var player = (PlayerMobile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerMobile));
            typeof(BaseGameObject).GetField("<World>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, _world);
            player.PendingBlock = -1;
            typeof(World).GetProperty(nameof(World.Player)).SetValue(_world, player);

            foreach (bool reload in new[] { true, false })
            {
                Chunk c = new Chunk(_world);
                Place(c, NewLand(_world, 0), 0, 0);
                Place(c, player, 0, 0);
                Assert.Equal(1, c.Dynamic.Count);

                if (reload)
                {
                    c.ClearForReload();
                }
                else
                {
                    c.Destroy();
                }

                Assert.Null(player.TileChunk);
                Assert.Equal(0, c.Dynamic.Count);
                Assert.Empty(Indexed(c, 0, 0));
            }
        }

        [Fact]
        public void Many_Objects_Grow_The_Array_And_Keep_The_Order()
        {
            Place(_a, NewLand(_world, 0), 6, 6);
            var rng = new Random(7);

            for (int i = 0; i < 70; i++)
            {
                Place(_a, rng.Next(3) == 0 ? NewStatic(Plain, (sbyte)rng.Next(0, 4)) : NewItem((ushort)rng.Next(Plain, TallBackground + 1), (sbyte)rng.Next(0, 4)), 6, 6);
            }

            AssertMerged(_a, 6, 6);
            Assert.True(_a.Dynamic.RawCell(6 << 3 | 6, out int n).Length >= n);
        }

        [Fact]
        public void Checker_Flags_A_Missing_An_Extra_And_A_Stale_Entry()
        {
            Place(_a, NewLand(_world, 0), 7, 7);
            Item a = NewItem(Plain, 0), b = NewItem(Plain, 0);
            Place(_a, a, 7, 7);
            Place(_a, b, 7, 7);

            // missing: the index loses b behind the list's back
            Assert.True(_a.Dynamic.Remove(b, 7 << 3 | 7));
            TileOrder.IndexCheck r = TileOrder.CheckIndex(_a, 7, 7);
            Assert.True(r.Failed);
            Assert.True(r.CountMismatch);
            Assert.True(r.OrderMismatches > 0);

            // extra: an entry on a cell whose object is elsewhere
            _a.Dynamic.Add(b, 7 << 3 | 7);
            _a.Dynamic.Add(b, 0);
            r = TileOrder.CheckIndex(_a, 0, 0);
            Assert.True(r.Failed);
            Assert.Equal(1, r.BadEntries);
        }

        [Fact]
        public void Death_Rekey_And_Record_Replacement_Leave_The_Entry_Valid()
        {
            // a mobile re-keyed in place (as at death: the serial changes, no re-add to the tile)
            Place(_a, NewLand(_world, 0), 3, 3);
            Mobile m = NewMobile(0);
            Place(_a, m, 3, 3);
            Item item = NewItem(Plain, 0);
            Place(_a, item, 3, 3);
            _world.Mobiles.Remove(m.Serial);
            m.Serial = 0x8000_0100;
            _world.Mobiles[m.Serial] = m;
            ClassicUO.UnitTests.Ecs.LegacyRekeyReference.SerialChanged(_world, 0x100, m);
            Assert.False(TileOrder.CheckIndex(_a, 3, 3).Failed);

            // the full death shape: BuildDead's mobile (re-keyed, its worn items moved) is placed on a chunk of this test's, then
            // re-keyed once more under a corpse item with the same serial; the entries keep matching their objects
            World dead = ClassicUO.UnitTests.Ecs.WorldWriterMobileTests.BuildDead();
            Mobile dm = dead.Mobiles.Get(ClassicUO.UnitTests.Ecs.WorldWriterMobileTests.DEAD);
            Assert.NotNull(dm);
            dm.RemoveFromTile();
            var chunk = new Chunk(dead) { X = 0, Y = 0 };
            Place(chunk, NewLand(dead, 0), 1, 1);
            Place(chunk, dm, 1, 1);
            Item corpse = dead.GetOrCreateItem(0x4000_9000);
            corpse.Graphic = 0x2006;
            Place(chunk, corpse, 1, 1);
            dead.Mobiles.Remove(dm.Serial);
            dm.Serial = 0x8000_3001;
            dead.Mobiles[dm.Serial] = dm;
            ClassicUO.UnitTests.Ecs.LegacyRekeyReference.SerialChanged(dead, 0x8000_3000, dm);
            TileOrder.IndexCheck r = TileOrder.CheckIndex(chunk, 1, 1);
            Assert.False(r.Failed);
            Assert.Equal(0, r.BadEntries);
        }

        [Fact]
        public void Checker_Flags_An_Entry_Whose_Key_Went_Stale()
        {
            Place(_a, NewLand(_world, 0), 5, 5);
            Item a = NewItem(Plain, 0);
            Place(_a, a, 5, 5);
            Assert.Equal(0, TileOrder.CheckIndex(_a, 5, 5).BadEntries);

            // the object's priority changes under a placed entry (no re-add): the entry's stored key is stale
            a.PriorityZ = (short)(a.PriorityZ + 7);
            Assert.True(TileOrder.CheckIndex(_a, 5, 5).BadEntries >= 1);
        }

        [Fact]
        public void Off_Means_No_Index()
        {
            DynamicTileIndex.Maintain = false;
            Place(_a, NewLand(_world, 0), 0, 0);
            Item i = NewItem(Plain, 0);
            Place(_a, i, 0, 0);
            Assert.Null(_a.Dynamic);
            i.RemoveFromTile();
            Assert.Null(_a.Dynamic);
        }

        [Fact]
        public void Random_Operations_Keep_The_Merged_Order_Equal_To_The_List()
        {
            var rng = new Random(4321);
            var objects = new List<GameObject>();
            Chunk[] chunks = { _a, _b };

            // cells 0..1 of each chunk have land, cell 2 is landless
            foreach (Chunk c in chunks)
            {
                for (int x = 0; x < 2; x++)
                {
                    Place(c, NewLand(_world, (sbyte)rng.Next(-2, 8)), x, 0);
                }
            }

            for (int i = 0; i < 3000; i++)
            {
                int op = rng.Next(12);
                Chunk chunk = chunks[rng.Next(2)];
                int cx = rng.Next(3);

                if (op < 4 || objects.Count == 0)
                {
                    sbyte z = (sbyte)rng.Next(0, 6);
                    ushort g = (ushort)rng.Next(Plain, TallBackground + 1);
                    GameObject obj = rng.Next(8) switch
                    {
                        0 => NewMulti(g, z),
                        1 => NewMulti(g, z, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW),
                        2 => NewMulti(g, z, CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL),
                        3 => NewStatic(g, z),
                        4 => NewMobile(z),
                        5 => NewItem(0x2006, z),
                        _ => NewItem(g, z)
                    };
                    objects.Add(obj);
                    Place(chunk, obj, cx, 0);
                }
                else if (op < 7)
                {
                    // a move (a step, a packet, a boat), possibly into the other chunk
                    GameObject obj = objects[rng.Next(objects.Count)];
                    obj.Z = (sbyte)rng.Next(0, 6);
                    Place(chunk, obj, cx, 0);
                }
                else if (op < 8)
                {
                    GameObject obj = objects[rng.Next(objects.Count)];
                    obj.RemoveFromTile();
                    objects.Remove(obj);
                }
                else if (op < 9)
                {
                    // park, then the deferred add (or stay parked)
                    GameObject obj = objects[rng.Next(objects.Count)];
                    obj.RemoveFromTile();
                    obj.PendingBlock = 1;

                    if (rng.Next(2) == 0)
                    {
                        Place(chunk, obj, cx, 0);
                    }
                }
                else
                {
                    // graphic change without a re-insert
                    objects[rng.Next(objects.Count)].Graphic = (ushort)rng.Next(Plain, TallBackground + 1);
                }

                foreach (Chunk c in chunks)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        AssertMerged(c, x, 0);
                    }
                }
            }

            Assert.True(_a.Dynamic.Count + _b.Dynamic.Count > 0);
        }
    }
}
