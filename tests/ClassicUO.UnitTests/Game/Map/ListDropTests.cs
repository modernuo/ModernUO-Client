using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using Xunit;

namespace ClassicUO.UnitTests.Game.Map
{
    /// <summary>
    /// T6b part 3: the tile lists keep land, statics and multis and the items, mobiles and effects live only in the
    /// DynamicTileIndex. Random operations run on one world while the test keeps the legacy list order of every tile in a
    /// <see cref="ShadowTileList"/> (the old insertion rule, applied at every placement it makes); every walk and reader must
    /// see that order, the list must hold no dynamics and the checkers must pass. Until T9 a second world with the list
    /// holding everything (the deleted <c>world_tile_index</c> off path) was the oracle; the shadow was compared with it then.
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide
    public sealed class ListDropTests : IDisposable
    {
        private const ushort Plain = 1, Tall = 2, Background = 3, MultiMovable = 4, TallBackground = 5, Roof = 6, Chair = 0x0B00;

        private readonly StaticTiles[] _savedStaticData;

        public ListDropTests()
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
            ChairTable.Table[Chair] = new AnimationsLoader.SittingInfoData(Chair, 1, 2, 3, 4, 5, 6, false);
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            ChairTable.Table.Remove(Chair);
        }

        /// <summary>One world, its two chunks and the legacy order of their tiles (<see cref="Legacy"/>).</summary>
        private sealed class Side
        {
            public readonly World World = new World();
            public readonly Chunk[] Chunks;
            public readonly ShadowTileList[] Legacy = { new ShadowTileList(), new ShadowTileList() };
            public readonly List<GameObject> Objects = new List<GameObject>();
            public uint NextItem = 0x4000_0100, NextMobile = 0x100;

            public Side()
            {
                Chunks = new[] { new Chunk(World) { X = 0, Y = 0 }, new Chunk(World) { X = 1, Y = 0 } };
            }

            public T Track<T>(T obj) where T : GameObject
            {
                Objects.Add(obj);

                return obj;
            }

            public int Slot(Chunk c) => ReferenceEquals(c, Chunks[0]) ? 0 : 1;

            /// <summary>Takes <paramref name="obj"/> out of the legacy order (before it leaves its tile).</summary>
            public void Forget(GameObject obj)
            {
                if (obj.TileChunk != null)
                {
                    Assert.True(Legacy[Slot(obj.TileChunk)].Remove(obj, obj.TileCell));
                }
            }

            public void Place(Chunk chunk, GameObject obj, int x, int y)
            {
                Forget(obj);
                ListDropTests.Place(chunk, obj, x, y);
                Legacy[Slot(chunk)].Add(obj, obj.TileCell, obj.PriorityZ, LegacyTileOrder.State(obj));
            }

            public void Remove(GameObject obj)
            {
                Forget(obj);
                obj.RemoveFromTile();
            }

            public void ClearLegacy(Chunk chunk) => Legacy[Slot(chunk)] = new ShadowTileList();
        }

        private static Land NewLand(World world, sbyte z)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);
            land.Z = z;
            return land;
        }

        // kind: 0..2 multis (plain, preview, internal), 3 static, 4 mobile, 5 corpse, 6 item, 7 effect
        private static GameObject New(Side s, int kind, ushort graphic, sbyte z)
        {
            World w = s.World;

            switch (kind)
            {
                case 0: return new Multi(w) { Graphic = graphic, Z = z };
                case 1: return new Multi(w) { Graphic = graphic, Z = z, State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW };
                case 2: return new Multi(w) { Graphic = graphic, Z = z, State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL };
                case 3: return new Static(w) { Graphic = graphic, Z = z };
                case 4:
                {
                    Mobile m = w.GetOrCreateMobile(s.NextMobile++);
                    m.Z = z;
                    return m;
                }
                case 5:
                case 6:
                {
                    Item item = w.GetOrCreateItem(s.NextItem++);
                    item.Graphic = kind == 5 ? (ushort)0x2006 : graphic;
                    item.Z = z;
                    return item;
                }
                default:
                {
                    var fx = new FixedEffect(w, w.Effects, 1, 1, z, 0x3779, 0, 100, 0);
                    fx.RemoveFromTile();
                    return fx;
                }
            }
        }

        private static void Place(Chunk chunk, GameObject obj, int x, int y)
        {
            obj.X = (ushort)(chunk.X * 8 + x);
            obj.Y = (ushort)(chunk.Y * 8 + y);
            chunk.AddGameObject(obj, x, y);
        }

        private static List<GameObject> RealList(Chunk chunk, int x, int y)
        {
            var list = new List<GameObject>();

            for (GameObject o = chunk.GetHeadObject(x, y); o != null; o = o.TNext)
            {
                list.Add(o);
                Assert.True(list.Count < 100_000);
            }

            return list;
        }

        private static List<GameObject> MergedList(Chunk chunk, int cell)
        {
            var list = new List<GameObject>();

            for (GameObject o = TileWalk.Begin(chunk, cell, out TileWalk w); o != null; o = w.Next())
            {
                list.Add(o);
                Assert.True(list.Count < 100_000);
            }

            return list;
        }

        private static List<GameObject> ReaderList(Chunk chunk, int cell, GameObject from)
        {
            var list = new List<GameObject>();

            for (GameObject o = TileReader.Open(chunk, cell, from, out TileReader r); o != null; o = r.Next())
            {
                list.Add(o);
                Assert.True(list.Count < 100_000);
            }

            return list;
        }

        /// <summary>The whole-tile walk the render, mask-build and item-cache loops take (<see cref="TileWalk.MergeCells"/>,
        /// <see cref="TileWalk.Open"/>).</summary>
        private static List<GameObject> OpenedList(Chunk chunk, int cell)
        {
            var list = new List<GameObject>();
            ulong mergeCells = TileWalk.MergeCells(chunk);

            for (GameObject o = TileWalk.Open(chunk, cell, mergeCells, out TileWalk w, out bool m); o != null; o = m ? w.Next() : o.TNext)
            {
                list.Add(o);
                Assert.True(list.Count < 100_000);
            }

            return list;
        }

        /// <summary><see cref="DynamicTileIndex.Cells"/> has the cell's bit exactly when the cell holds entries.</summary>
        private static void AssertCellsMask(Chunk chunk, int cell)
        {
            DynamicTileIndex dyn = chunk.Dynamic;
            bool occupied = dyn != null && dyn.CountAt(cell) > 0;
            bool bit = dyn != null && (dyn.Cells >> cell & 1) != 0;
            Assert.Equal(occupied, bit);
        }

        // the pre-T6b scans over the legacy order: from the land anchor on a tile with land, else from the tile's head
        private static IEnumerable<GameObject> LegacyScan(Chunk chunk, int x, int y, List<GameObject> legacy)
        {
            int start = chunk.Tiles[x, y] is Land land ? legacy.IndexOf(land) : 0;
            Assert.True(start >= 0);

            return legacy.Skip(start);
        }

        private static (sbyte, sbyte) LegacyMapZ(IEnumerable<GameObject> scan)
        {
            sbyte g = 0, s = 0;

            foreach (GameObject o in scan)
            {
                if (o is Land)
                {
                    g = o.Z;
                }
                else if (s < o.Z)
                {
                    s = o.Z;
                }
            }

            return (g, s);
        }

        private static GameObject LegacyNearRoof(IEnumerable<GameObject> scan, int z) =>
            scan.FirstOrDefault(o => (o is Static || o is Multi) && o.Graphic < Chunk.StaticTileData.Length && Chunk.StaticTileData[o.Graphic].IsRoof && Math.Abs(z - o.Z) <= 6);

        private static ushort LegacyChair(Mobile m, List<GameObject> legacy) =>
            legacy.FirstOrDefault(o => (o is Item || o is Static || o is Multi) && Math.Abs(m.Z - o.Z) <= 1 && ChairTable.Table.ContainsKey(o.Graphic))?.Graphic ?? 0;

        /// <summary>Every tile, walk and reader equals the legacy order, and the checkers pass.</summary>
        private static void AssertLegacy(Side s)
        {
            for (int c = 0; c < s.Chunks.Length; c++)
            {
                Chunk chunk = s.Chunks[c];
                bool external = false;

                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        int cell = x << 3 | y;
                        List<GameObject> expected = s.Legacy[c].Cell(cell)?.ToList() ?? new List<GameObject>();
                        external |= expected.Any(o => !(o is Land) && !(o is Static));
                        // the stamp-order replay rebuilds the same order from the tile's current objects
                        Assert.Equal(expected, LegacyTileOrder.Of(chunk, cell));

                        List<GameObject> realList = RealList(chunk, x, y);
                        Assert.DoesNotContain(realList, DynamicTileIndex.Holds);
                        Assert.Equal(expected.Where(o => !DynamicTileIndex.Holds(o)).ToList(), realList);
                        // the anchor is never a dynamic: a tile holding only dynamics has none
                        Assert.True(chunk.Tiles[x, y] == null || !DynamicTileIndex.Holds(chunk.Tiles[x, y]));
                        Assert.Equal(expected, MergedList(chunk, cell));
                        // the whole-tile walks by the chunk's merge mask (GameScene's chunk loop, the Gpu mask build, the item
                        // cache): the mask is exactly the index's occupied cells, and the walk it picks is the legacy order
                        AssertCellsMask(chunk, cell);
                        Assert.Equal(expected, OpenedList(chunk, cell));

                        TileOrder.IndexCheck check = TileOrder.CheckIndex(chunk, x, y);
                        Assert.False(check.Failed, $"order {check.OrderMismatches}, bad {check.BadEntries}, count {check.CountMismatch}, list dynamics {check.ListDynamics}");
                        Assert.False(TileOrder.CheckTile(chunk, x, y).Failed);

                        // the readers from the head and from every object
                        Assert.Equal(expected, ReaderList(chunk, cell, null));

                        for (int k = 0; k < expected.Count; k++)
                        {
                            Assert.Equal(expected.Skip(k).ToList(), ReaderList(chunk, cell, expected[k]));
                        }

                        if (chunk.Tiles[x, y] != null || expected.Count > 0)
                        {
                            List<GameObject> scan = LegacyScan(chunk, x, y, expected).ToList();
                            ClassicUO.Game.Map.Map.ScanMapZ(chunk, x, y, out sbyte g1, out sbyte s1);
                            Assert.Equal(LegacyMapZ(scan), (g1, s1));

                            for (int z = -2; z < 12; z += 3)
                            {
                                Assert.Same(LegacyNearRoof(scan, z), ClassicUO.Game.Map.Map.FindNearRoof(chunk, x, y, z, Chunk.StaticTileData));
                            }
                        }

                        foreach (GameObject o in expected)
                        {
                            if (o is Mobile m)
                            {
                                bool found = m.FindChair(out var data);
                                Assert.Equal(LegacyChair(m, expected), found ? data.Graphic : (ushort)0);
                            }
                        }

                        // the render walk's whole-tile start (GameScene: TileWalk.First, then Start)
                        GameObject first = TileWalk.First(chunk, x, y);
                        Assert.Equal(expected.Count == 0, first == null);

                        if (first != null)
                        {
                            var seen = new List<GameObject>();

                            for (GameObject o = TileWalk.Start(first, chunk, false, out TileWalk w, out bool m); o != null; o = m ? w.Next() : o.TNext)
                            {
                                seen.Add(o);
                            }

                            Assert.Equal(expected, seen);
                        }

                        // the render walk's one-object calls (stopAt = obj.TNext, single): exactly that object
                        foreach (GameObject obj in expected)
                        {
                            GameObject stopAt = obj.TNext;
                            int n = 0;

                            for (GameObject o = TileWalk.Start(obj, chunk, true, out TileWalk w, out bool m); o != stopAt; o = m ? w.Next() : o.TNext)
                            {
                                Assert.Same(obj, o);
                                n++;
                            }

                            Assert.Equal(1, n);
                        }
                    }
                }

                Assert.Equal(!external, chunk.HasNoExternalData());
            }

            // everything placed is reachable (the checker's other direction)
            foreach (GameObject o in s.Objects)
            {
                if (o.TileChunk != null && !o.IsDestroyed)
                {
                    Assert.True(TileOrder.IsReachable(o), TileOrder.Describe(o));
                }
            }
        }

        private static void RunRandom(int seed, int steps, bool effects)
        {
            var s = new Side();
            var rng = new Random(seed);

            // cells (0,0) and (1,0) of each chunk have land, (2,0) is landless, (3,0) only ever holds dynamics
            void Lands(Chunk chunk, sbyte z0, sbyte z1)
            {
                s.Place(chunk, s.Track(NewLand(s.World, z0)), 0, 0);
                s.Place(chunk, s.Track(NewLand(s.World, z1)), 1, 0);
            }

            for (int c = 0; c < 2; c++)
            {
                Lands(s.Chunks[c], (sbyte)rng.Next(-2, 8), (sbyte)rng.Next(-2, 8));
            }

            // the placed (or parked) non-land objects
            var live = new List<GameObject>();

            for (int i = 0; i < steps; i++)
            {
                int op = rng.Next(20);
                Chunk chunk = s.Chunks[rng.Next(2)];
                int cx = rng.Next(4);

                if (op < 7 || live.Count == 0)
                {
                    int kind = rng.Next(effects ? 8 : 7);

                    if (cx == 3 && kind < 4)
                    {
                        kind = 4 + rng.Next(effects ? 4 : 3);
                    }

                    sbyte z = (sbyte)rng.Next(0, 8);
                    ushort g = rng.Next(5) == 0 ? Roof : rng.Next(6) == 0 ? Chair : (ushort)rng.Next(Plain, TallBackground + 1);
                    GameObject obj = s.Track(New(s, kind, g, z));
                    s.Place(chunk, obj, cx, 0);
                    live.Add(obj);
                }
                else if (op < 10)
                {
                    // a move (a step, a packet), possibly into the other chunk; dynamics only onto the dynamics-only cell
                    GameObject obj = live[rng.Next(live.Count)];
                    int to = cx == 3 && !DynamicTileIndex.Holds(obj) ? 2 : cx;
                    obj.Z = (sbyte)rng.Next(0, 8);
                    s.Place(chunk, obj, to, 0);
                }
                else if (op < 12)
                {
                    // a boat step: its carried entities move one tile (BoatMovingManager: X/Y, then re-added if placed)
                    foreach (GameObject e in live.Where(DynamicTileIndex.Holds).OrderBy(_ => rng.Next()).Take(3).ToList())
                    {
                        if (!e.IsPlaced || e.TileChunk == null)
                        {
                            continue;
                        }

                        int cell = e.TileCell >> 3, ch = s.Slot(e.TileChunk);
                        int nx = cell + 1, nch = ch;

                        if (nx > 3)
                        {
                            nx = 0;
                            nch = 1 - ch;
                        }

                        s.Place(s.Chunks[nch], e, nx, 0);
                    }
                }
                else if (op < 13)
                {
                    GameObject obj = live[rng.Next(live.Count)];
                    s.Remove(obj);
                    live.Remove(obj);
                }
                else if (op < 15)
                {
                    // Map.Park: off the tile, then the deferred add when the chunk loads (or stay parked a while)
                    GameObject obj = live[rng.Next(live.Count)];
                    int to = cx == 3 && !DynamicTileIndex.Holds(obj) ? 2 : cx;
                    s.Remove(obj);
                    obj.PendingBlock = 1;

                    if (rng.Next(2) == 0)
                    {
                        s.Place(chunk, obj, to, 0);
                    }
                }
                else if (op < 17)
                {
                    // a graphic change without a re-insert
                    live[rng.Next(live.Count)].Graphic = (ushort)rng.Next(Plain, Roof + 1);
                }
                else if (op < 19)
                {
                    // a death re-key: the mobile's serial changes in place (no re-add)
                    var mobiles = live.OfType<Mobile>().ToList();

                    if (mobiles.Count > 0)
                    {
                        Mobile m = mobiles[rng.Next(mobiles.Count)];
                        uint old = m.Serial;
                        s.World.Mobiles.Remove(old);
                        m.Serial = s.NextMobile++ | 0x0080_0000;
                        s.World.Mobiles[m.Serial] = m;
                        ClassicUO.UnitTests.Ecs.LegacyRekeyReference.SerialChanged(s.World, old, m);
                    }
                }
                else if (rng.Next(8) == 0)
                {
                    // an in-place reload (UltimaLive): everything on the chunk is destroyed, the land comes back
                    var on = s.Objects.Where(o => ReferenceEquals(o.TileChunk, chunk)).ToList();
                    chunk.ClearForReload();
                    s.ClearLegacy(chunk);
                    Assert.All(on, o => Assert.True(o.IsDestroyed && o.TileChunk == null));
                    Lands(chunk, (sbyte)rng.Next(-2, 8), (sbyte)rng.Next(-2, 8));
                    live.RemoveAll(o => o.IsDestroyed);
                }

                AssertLegacy(s);
            }

            // the run reached the cases it is for: dynamics held, some alone on a tile (no anchor), some on landless tiles
            Assert.True(s.Chunks.Sum(c => c.Dynamic?.Count ?? 0) > 5);
            Assert.Contains(s.Chunks, c => c.Dynamic != null && c.Dynamic.CountAt(3 << 3) > 0 && c.Tiles[3, 0] == null);
            Assert.Contains(s.Chunks, c => c.Dynamic != null && c.Dynamic.CountAt(2 << 3) > 0);

            // the chunks' teardown destroys every object on them, the index's included, and empties the index
            var placed = s.Objects.Where(o => o.TileChunk != null).ToList();

            foreach (Chunk c in s.Chunks)
            {
                c.Destroy();
            }

            Assert.All(placed, o => Assert.True(o.IsDestroyed && o.TileChunk == null));

            foreach (Chunk c in s.Chunks)
            {
                Assert.True(c.Dynamic == null || c.Dynamic.Count == 0);

                for (int cell = 0; cell < 64; cell++)
                {
                    Assert.Null(c.Tiles[cell >> 3, cell & 7]);
                }
            }
        }

        [Theory]
        [InlineData(11, true)]
        [InlineData(12, true)]
        [InlineData(13, false)]
        [InlineData(14, false)]
        public void Random_Operations_Keep_The_Legacy_Order(int seed, bool effects)
        {
            RunRandom(seed, 700, effects);
        }

        [Fact]
        public void The_List_Holds_No_Dynamics_And_The_Anchor_Stays_A_List_Object()
        {
            var s = new Side();
            Chunk c = s.Chunks[0];
            Item item = s.World.GetOrCreateItem(0x4000_0001);
            item.Graphic = Plain;
            Place(c, item, 4, 4);
            Assert.Null(c.Tiles[4, 4]);
            Assert.Null(item.TNext);
            Assert.Null(item.TPrevious);
            Assert.Same(item, TileWalk.First(c, 4, 4));
            Assert.Same(c, item.TileChunk);
            Assert.True(item.IsPlaced);

            Static st = new Static(s.World) { Graphic = Plain, Z = 0 };
            Place(c, st, 4, 4);
            Assert.Same(st, c.Tiles[4, 4]);
            Assert.Same(st, TileWalk.First(c, 4, 4));

            item.RemoveFromTile();
            Assert.False(item.IsPlaced);
            Assert.Same(st, c.Tiles[4, 4]);
            Assert.Equal(0, c.Dynamic.Count);
        }

        [Fact]
        public void Chunk_Teardown_Destroys_The_Index_Objects_And_Releases_The_Player()
        {
            foreach (bool reload in new[] { true, false })
            {
                var s = new Side();
                var player = (PlayerMobile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerMobile));
                typeof(BaseGameObject).GetField("<World>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, s.World);
                player.PendingBlock = -1;
                typeof(World).GetProperty(nameof(World.Player)).SetValue(s.World, player);
                Chunk c = s.Chunks[0];
                Land land = NewLand(s.World, 0);
                Place(c, land, 0, 0);
                Item item = s.World.GetOrCreateItem(0x4000_0002);
                item.Graphic = Plain;
                Place(c, item, 0, 0);
                Mobile mob = s.World.GetOrCreateMobile(0x0002);
                Place(c, mob, 5, 5); // a tile holding only dynamics
                Place(c, player, 5, 5);
                Assert.Equal(3, c.Dynamic.Count);

                if (reload)
                {
                    c.ClearForReload();
                }
                else
                {
                    c.Destroy();
                }

                Assert.True(item.IsDestroyed);
                Assert.True(mob.IsDestroyed);
                Assert.True(land.IsDestroyed);
                Assert.False(player.IsDestroyed);
                Assert.Null(player.TileChunk);
                Assert.Null(item.TileChunk);
                Assert.Equal(0, c.Dynamic.Count);
            }
        }

        [Fact]
        public void The_Checker_Flags_A_Dynamic_In_The_List_A_Misordered_Index_And_A_Lost_Entry()
        {
            var s = new Side();
            Chunk c = s.Chunks[0];
            Land land = NewLand(s.World, 0);
            Place(c, land, 2, 2);
            Item a = s.World.GetOrCreateItem(0x4000_0003), b = s.World.GetOrCreateItem(0x4000_0004);
            a.Graphic = b.Graphic = Plain;
            Place(c, a, 2, 2);
            Place(c, b, 2, 2);
            Assert.False(TileOrder.CheckIndex(c, 2, 2).Failed);
            Assert.True(TileOrder.IsReachable(a));

            // a dynamic linked into the list behind the index's back
            land.TNext = a;
            a.TPrevious = land;
            TileOrder.IndexCheck r = TileOrder.CheckIndex(c, 2, 2);
            Assert.Equal(1, r.ListDynamics);
            Assert.True(r.Failed);
            land.TNext = null;
            a.TPrevious = null;
            Assert.False(TileOrder.CheckIndex(c, 2, 2).Failed);

            // the index's entries swapped: out of key order, and the merged walk descends
            DynamicTileIndex.Entry[] raw = c.Dynamic.RawCell(2 << 3 | 2, out int n);
            Assert.Equal(2, n);
            (raw[0], raw[1]) = (raw[1], raw[0]);
            r = TileOrder.CheckIndex(c, 2, 2);
            Assert.True(r.BadEntries > 0);
            Assert.True(r.OrderMismatches > 0);
            (raw[0], raw[1]) = (raw[1], raw[0]);
            Assert.False(TileOrder.CheckIndex(c, 2, 2).Failed);

            // the index loses an object: unreachable
            c.Dynamic.Remove(a, 2 << 3 | 2);
            Assert.False(TileOrder.IsReachable(a));
        }

        // tile-index-2: the merge compares an index entry with a list object by the stored PriorityZ first and builds the list
        // object's full key (rank by type, stamp) only on equal priorities; the answer must be the full key comparison's
        [Fact]
        public void The_Priority_First_Merge_Comparison_Equals_The_Full_Key_Comparison()
        {
            var s = new Side();
            var rnd = new Random(2602);
            var objects = new List<GameObject>
            {
                NewLand(s.World, 0),
                new Static(s.World) { Graphic = Plain },
                new Multi(s.World) { Graphic = Plain },
                new Multi(s.World) { Graphic = Plain, State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW },
                new Multi(s.World) { Graphic = Plain, State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL }
            };
            int ties = 0, differ = 0;

            for (int i = 0; i < 20_000; i++)
            {
                GameObject o = objects[rnd.Next(objects.Count)];
                o.PriorityZ = (short)rnd.Next(-3, 4);
                o.TileSeq = (ulong)rnd.Next(1, 6);
                var key = new TileOrderKey((short)rnd.Next(-3, 4), (byte)rnd.Next(0, 3), (ulong)rnd.Next(1, 6));
                bool full = key.CompareTo(TileOrder.Key(o)) < 0;

                Assert.Equal(full, TileWalk.Before(in key, o));

                if (key.Priority == o.PriorityZ)
                {
                    ties++;
                }
                else
                {
                    differ++;
                }
            }

            Assert.True(ties > 1000 && differ > 1000);

            // the index files held objects by their rank-2 key without the rank's type tests
            Item item = s.World.GetOrCreateItem(0x4000_0201);
            Mobile mobile = s.World.GetOrCreateMobile(0x201);

            foreach (GameObject d in new GameObject[] { item, mobile })
            {
                d.PriorityZ = 5;
                d.TileSeq = 77;
                Assert.Equal(0, DynamicTileIndex.KeyOf(d).CompareTo(TileOrder.Key(d)));
            }
        }

        // tile-index-fix-a: a whole-tile walk of a tile whose index cell is empty follows TNext (merged false) and must see what
        // the merge and the legacy order see; a tile with dynamics merges
        [Fact]
        public void Whole_Tile_Start_Takes_The_List_Walk_Only_On_Tiles_Without_Dynamics()
        {
            var s = new Side();
            Chunk c = s.Chunks[0];

            Item NewItem(uint serial)
            {
                Item i = s.World.GetOrCreateItem(serial);
                i.Graphic = Plain;
                return i;
            }

            // (0,0) land, static, multi; (1,0) land, static, item, mobile; (2,0) land and an item that leaves;
            // (3,0) a landless mobile; (4,0) land only; (5,0) land and a mobile that steps away to (1,0)
            s.Place(c, NewLand(s.World, 0), 0, 0);
            s.Place(c, new Static(s.World) { Graphic = Tall, Z = 0 }, 0, 0);
            s.Place(c, new Multi(s.World) { Graphic = Plain, Z = 2 }, 0, 0);
            s.Place(c, NewLand(s.World, 0), 1, 0);
            s.Place(c, new Static(s.World) { Graphic = Plain, Z = 0 }, 1, 0);
            s.Place(c, NewItem(0x4000_0010), 1, 0);
            s.Place(c, s.World.GetOrCreateMobile(0x10), 1, 0);
            s.Place(c, NewLand(s.World, 0), 2, 0);
            Item leaves = NewItem(0x4000_0011);
            s.Place(c, leaves, 2, 0);
            s.Remove(leaves);
            s.Place(c, s.World.GetOrCreateMobile(0x11), 3, 0);
            s.Place(c, NewLand(s.World, 0), 4, 0);
            s.Place(c, NewLand(s.World, 0), 5, 0);
            Mobile steps = s.World.GetOrCreateMobile(0x12);
            s.Place(c, steps, 5, 0);
            steps.Z = 1;
            s.Place(c, steps, 1, 0);

            bool[] dynamics = { false, true, false, true, false, false };

            for (int x = 0; x < dynamics.Length; x++)
            {
                int cell = x << 3;
                List<GameObject> legacy = s.Legacy[0].Cell(cell).ToList();
                Assert.NotEmpty(legacy);
                Assert.Equal(dynamics[x], c.Dynamic != null && c.Dynamic.CountAt(cell) > 0);
                Assert.Equal(legacy, MergedList(c, cell));

                var seen = new List<GameObject>();
                GameObject first = TileWalk.First(c, x, 0);
                bool merged = false;

                for (GameObject o = TileWalk.Start(first, c, false, out TileWalk w, out merged); o != null; o = merged ? w.Next() : o.TNext)
                {
                    seen.Add(o);
                    Assert.True(seen.Count < 100_000);
                }

                Assert.Equal(dynamics[x], merged);
                Assert.Equal(legacy, seen);
            }
        }
    }
}
