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
    /// T6b part 3: with the list dropped (<see cref="DynamicTileIndex.ListDropped"/>) the tile lists keep land, statics and
    /// multis and the items, mobiles and effects live only in the DynamicTileIndex. Two worlds run the same operations: the
    /// legacy one (the list holds everything) is the oracle; in the dropped one every walk and reader must see the legacy
    /// list's order, the list must hold no dynamics, the shadow list (the checker's legacy order) must equal the legacy list
    /// and the checkers must pass.
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData and the DynamicTileIndex switches are process-wide
    public sealed class ListDropTests : IDisposable
    {
        private const ushort Plain = 1, Tall = 2, Background = 3, MultiMovable = 4, TallBackground = 5, Roof = 6, Chair = 0x0B00;

        private readonly StaticTiles[] _savedStaticData;
        private readonly bool _savedMaintain, _savedEffectsHeld, _savedDropped, _savedShadow;
        // dev client builds only (the test project doesn't see CUO_DEVTOOLS): the mutable walk switch and the shadow walk
        private static readonly FieldInfo s_walkOn = typeof(DynamicTileIndex).GetField(nameof(DynamicTileIndex.WalkOn));
        private static readonly FieldInfo s_shadowWalk = typeof(DynamicTileIndex).GetField("ShadowWalk");
        private static readonly MethodInfo s_setWalk = typeof(DynamicTileIndex).GetMethod("SetWalk");
        private readonly bool _savedWalkOn, _savedShadowWalk;

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
            _savedMaintain = DynamicTileIndex.Maintain;
            _savedEffectsHeld = DynamicTileIndex.EffectsHeld;
            _savedDropped = DynamicTileIndex.ListDropped;
            _savedShadow = DynamicTileIndex.Shadow;
            _savedWalkOn = DynamicTileIndex.WalkOn;

            if (s_shadowWalk != null)
            {
                _savedShadowWalk = (bool)s_shadowWalk.GetValue(null);
                s_shadowWalk.SetValue(null, false);
            }
            ChairTable.Table[Chair] = new AnimationsLoader.SittingInfoData(Chair, 1, 2, 3, 4, 5, 6, false);
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            DynamicTileIndex.Maintain = _savedMaintain;
            DynamicTileIndex.EffectsHeld = _savedEffectsHeld;
            DynamicTileIndex.ListDropped = _savedDropped;
            DynamicTileIndex.Shadow = _savedShadow;
            if (s_shadowWalk != null)
            {
                s_walkOn.SetValue(null, _savedWalkOn);
                s_shadowWalk.SetValue(null, _savedShadowWalk);
            }
            ChairTable.Table.Remove(Chair);
        }

        /// <summary>One world under one setting of the switches (set before each of its operations: they are static).</summary>
        private sealed class Side
        {
            public readonly World World = new World();
            public readonly Chunk[] Chunks;
            public readonly bool Dropped, Shadow;
            public readonly List<GameObject> Objects = new List<GameObject>();
            public readonly Dictionary<GameObject, int> Id = new Dictionary<GameObject, int>(ReferenceEqualityComparer.Instance);
            public uint NextItem = 0x4000_0100, NextMobile = 0x100;

            public Side(bool dropped, bool shadow)
            {
                Dropped = dropped;
                Shadow = shadow;
                Use();
                Chunks = new[] { new Chunk(World) { X = 0, Y = 0 }, new Chunk(World) { X = 1, Y = 0 } };
            }

            public void Use()
            {
                DynamicTileIndex.Maintain = true;
                DynamicTileIndex.ListDropped = Dropped;
                DynamicTileIndex.Shadow = Shadow;
            }

            public T Track<T>(T obj) where T : GameObject
            {
                Id[obj] = Id.Count;
                Objects.Add(obj);

                return obj;
            }
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

            for (GameObject o = TileWalk.BeginIndex(chunk, cell, out TileWalk w); o != null; o = w.Next())
            {
                list.Add(o);
                Assert.True(list.Count < 100_000);
            }

            return list;
        }

        private static List<GameObject> ReaderList(Chunk chunk, int cell, bool merged, GameObject from)
        {
            var list = new List<GameObject>();

            for (GameObject o = TileReader.Open(chunk, cell, merged, from, out TileReader r); o != null; o = r.Next())
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
            ulong mergeCells = TileWalk.MergeCells(chunk, DynamicTileIndex.Merged);

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

        private static List<int> Ids(Side s, IEnumerable<GameObject> objects) => objects.Select(o => s.Id[o]).ToList();

        /// <summary>Every tile, walk and reader of the dropped side equals the legacy side's list, and the checkers pass.</summary>
        private static void AssertSame(Side legacy, Side dropped)
        {
            for (int c = 0; c < legacy.Chunks.Length; c++)
            {
                Chunk lc = legacy.Chunks[c], dc = dropped.Chunks[c];

                legacy.Use();
                bool legacyExternal = lc.HasNoExternalData();
                dropped.Use();
                Assert.Equal(legacyExternal, dc.HasNoExternalData());

                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        int cell = x << 3 | y;
                        legacy.Use();
                        List<GameObject> legacyList = RealList(lc, x, y);
                        List<int> expected = Ids(legacy, legacyList);
                        Assert.False(TileOrder.CheckIndex(lc, x, y).Failed);
                        // the whole-tile walks by the chunk's merge mask with the switch off: the list from its head
                        AssertCellsMask(lc, cell);
                        Assert.Equal(expected, Ids(legacy, OpenedList(lc, cell)));
                        ClassicUO.Game.Map.Map.ScanMapZ(lc, x, y, false, out sbyte g0, out sbyte s0);
                        var roofs = new List<int>();

                        for (int z = -2; z < 12; z += 3)
                        {
                            GameObject r = ClassicUO.Game.Map.Map.FindNearRoof(lc, x, y, z, Chunk.StaticTileData, false);
                            roofs.Add(r == null ? -1 : legacy.Id[r]);
                        }

                        var chairs = new List<(bool, ushort)>();

                        foreach (GameObject o in legacyList)
                        {
                            if (o is Mobile m)
                            {
                                bool found = m.FindChair(false, out var data);
                                chairs.Add((found, data.Graphic));
                            }
                        }

                        dropped.Use();
                        List<GameObject> realList = RealList(dc, x, y);
                        Assert.DoesNotContain(realList, DynamicTileIndex.Holds);
                        Assert.Equal(expected.Where(i => !DynamicTileIndex.Holds(legacy.Objects[i])).ToList(), Ids(dropped, realList));
                        // the anchor is never a dynamic: a tile holding only dynamics has none
                        Assert.True(dc.Tiles[x, y] == null || !DynamicTileIndex.Holds(dc.Tiles[x, y]));
                        Assert.Equal(expected, Ids(dropped, MergedList(dc, cell)));
                        // the whole-tile walks by the chunk's merge mask (GameScene's chunk loop, the Gpu mask build, the item
                        // cache): the mask is exactly the index's occupied cells, and the walk it picks is the legacy order
                        AssertCellsMask(dc, cell);
                        Assert.Equal(expected, Ids(dropped, OpenedList(dc, cell)));

                        if (dropped.Shadow)
                        {
                            Assert.Equal(expected, Ids(dropped, dc.Shadow?.Cell(cell) ?? new List<GameObject>()));
                        }

                        TileOrder.IndexCheck check = TileOrder.CheckIndex(dc, x, y);
                        Assert.False(check.Failed, $"order {check.OrderMismatches}, bad {check.BadEntries}, count {check.CountMismatch}, list dynamics {check.ListDynamics}");
                        Assert.False(TileOrder.CheckTile(dc, x, y).Failed);

                        // the readers: both walks (merged; not merged = the shadow list, or the merge without one) from the head
                        // and from every object
                        foreach (bool merged in new[] { true, false })
                        {
                            Assert.Equal(expected, Ids(dropped, ReaderList(dc, cell, merged, null)));

                            for (int k = 0; k < legacyList.Count; k++)
                            {
                                GameObject from = dropped.Objects[expected[k]];
                                Assert.Equal(expected.Skip(k).ToList(), Ids(dropped, ReaderList(dc, cell, merged, from)));
                            }

                            ClassicUO.Game.Map.Map.ScanMapZ(dc, x, y, merged, out sbyte g1, out sbyte s1);
                            Assert.Equal((g0, s0), (g1, s1));

                            for (int z = -2, i = 0; z < 12; z += 3, i++)
                            {
                                GameObject r = ClassicUO.Game.Map.Map.FindNearRoof(dc, x, y, z, Chunk.StaticTileData, merged);
                                Assert.Equal(roofs[i], r == null ? -1 : dropped.Id[r]);
                            }

                            int ci = 0;

                            foreach (int id in expected)
                            {
                                if (dropped.Objects[id] is Mobile m)
                                {
                                    bool found = m.FindChair(merged, out var data);
                                    Assert.Equal(chairs[ci++], (found, data.Graphic));
                                }
                            }
                        }

                        // the render walk's whole-tile start (GameScene: TileWalk.First, then Start)
                        GameObject first = TileWalk.First(dc, x, y);
                        Assert.Equal(expected.Count == 0, first == null);

                        if (first != null)
                        {
                            var seen = new List<GameObject>();

                            for (GameObject o = TileWalk.Start(first, dc, false, DynamicTileIndex.Merged, out TileWalk w, out bool m); o != null; o = m ? w.Next() : o.TNext)
                            {
                                seen.Add(o);
                            }

                            Assert.Equal(expected, Ids(dropped, seen));
                        }

                        // the render walk's one-object calls (stopAt = obj.TNext, single): exactly that object
                        foreach (int id in expected)
                        {
                            GameObject obj = dropped.Objects[id];
                            GameObject stopAt = obj.TNext;
                            int n = 0;

                            for (GameObject o = TileWalk.Start(obj, dc, true, DynamicTileIndex.Merged, out TileWalk w, out bool m); o != stopAt; o = m ? w.Next() : o.TNext)
                            {
                                Assert.Same(obj, o);
                                n++;
                            }

                            Assert.Equal(1, n);
                        }
                    }
                }
            }

            // everything placed is reachable (the checker's other direction)
            dropped.Use();

            foreach (GameObject o in dropped.Objects)
            {
                if (o.TileChunk != null && !o.IsDestroyed)
                {
                    Assert.True(TileOrder.IsReachable(o), TileOrder.Describe(o));
                }
            }
        }

        private static void Both(Side legacy, Side dropped, Action<Side> op)
        {
            legacy.Use();
            op(legacy);
            dropped.Use();
            op(dropped);
        }

        private static void RunRandom(int seed, int steps, bool shadow, bool effects)
        {
            DynamicTileIndex.EffectsHeld = effects;
            var legacy = new Side(false, false);
            var dropped = new Side(true, shadow);
            var rng = new Random(seed);

            // cells (0,0) and (1,0) of each chunk have land, (2,0) is landless, (3,0) only ever holds dynamics
            void Lands(Side s, Chunk chunk, sbyte z0, sbyte z1)
            {
                Place(chunk, s.Track(NewLand(s.World, z0)), 0, 0);
                Place(chunk, s.Track(NewLand(s.World, z1)), 1, 0);
            }

            for (int c = 0; c < 2; c++)
            {
                sbyte z0 = (sbyte)rng.Next(-2, 8), z1 = (sbyte)rng.Next(-2, 8);
                int ci = c;
                Both(legacy, dropped, s => Lands(s, s.Chunks[ci], z0, z1));
            }

            // the ids of placed (or parked) non-land objects; the same on both sides
            var live = new List<int>();

            for (int i = 0; i < steps; i++)
            {
                int op = rng.Next(20);
                int chunk = rng.Next(2);
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
                    int id = -1;
                    Both(legacy, dropped, s =>
                    {
                        GameObject obj = s.Track(New(s, kind, g, z));
                        id = s.Id[obj];
                        Place(s.Chunks[chunk], obj, cx, 0);
                    });
                    live.Add(id);
                }
                else if (op < 10)
                {
                    // a move (a step, a packet), possibly into the other chunk; dynamics only onto the dynamics-only cell
                    int id = live[rng.Next(live.Count)];
                    sbyte z = (sbyte)rng.Next(0, 8);
                    Both(legacy, dropped, s =>
                    {
                        GameObject obj = s.Objects[id];
                        int to = cx == 3 && !DynamicTileIndex.Holds(obj) ? 2 : cx;
                        obj.Z = z;
                        Place(s.Chunks[chunk], obj, to, 0);
                    });
                }
                else if (op < 12)
                {
                    // a boat step: its carried entities move one tile (BoatMovingManager: X/Y, then re-added if placed)
                    var carried = live.Where(id => DynamicTileIndex.Holds(legacy.Objects[id])).OrderBy(_ => rng.Next()).Take(3).ToList();
                    Both(legacy, dropped, s =>
                    {
                        foreach (int id in carried)
                        {
                            GameObject e = s.Objects[id];

                            if (!e.IsPlaced || e.TileChunk == null)
                            {
                                continue;
                            }

                            int cell = e.TileCell >> 3, ch = e.TileChunk == s.Chunks[0] ? 0 : 1;
                            int nx = cell + 1, nch = ch;

                            if (nx > 3)
                            {
                                nx = 0;
                                nch = 1 - ch;
                            }

                            Place(s.Chunks[nch], e, nx, 0);
                        }
                    });
                }
                else if (op < 13)
                {
                    int id = live[rng.Next(live.Count)];
                    Both(legacy, dropped, s => s.Objects[id].RemoveFromTile());
                    live.Remove(id);
                }
                else if (op < 15)
                {
                    // Map.Park: off the tile, then the deferred add when the chunk loads (or stay parked a while)
                    int id = live[rng.Next(live.Count)];
                    bool unpark = rng.Next(2) == 0;
                    Both(legacy, dropped, s =>
                    {
                        GameObject obj = s.Objects[id];
                        int to = cx == 3 && !DynamicTileIndex.Holds(obj) ? 2 : cx;
                        obj.RemoveFromTile();
                        obj.PendingBlock = 1;

                        if (unpark)
                        {
                            Place(s.Chunks[chunk], obj, to, 0);
                        }
                    });
                }
                else if (op < 17)
                {
                    // a graphic change without a re-insert
                    int id = live[rng.Next(live.Count)];
                    ushort g = (ushort)rng.Next(Plain, Roof + 1);
                    Both(legacy, dropped, s => s.Objects[id].Graphic = g);
                }
                else if (op < 19)
                {
                    // a death re-key: the mobile's serial changes in place (no re-add)
                    var mobiles = live.Where(id => legacy.Objects[id] is Mobile).ToList();

                    if (mobiles.Count > 0)
                    {
                        int id = mobiles[rng.Next(mobiles.Count)];
                        Both(legacy, dropped, s =>
                        {
                            var m = (Mobile)s.Objects[id];
                            uint old = m.Serial;
                            s.World.Mobiles.Remove(old);
                            m.Serial = s.NextMobile++ | 0x0080_0000;
                            s.World.Mobiles[m.Serial] = m;
                            ClassicUO.UnitTests.Ecs.LegacyRekeyReference.SerialChanged(s.World, old, m);
                        });
                    }
                }
                else if (rng.Next(8) == 0)
                {
                    // an in-place reload (UltimaLive): everything on the chunk is destroyed, the land comes back
                    sbyte z0 = (sbyte)rng.Next(-2, 8), z1 = (sbyte)rng.Next(-2, 8);
                    Both(legacy, dropped, s =>
                    {
                        s.Chunks[chunk].ClearForReload();
                        Lands(s, s.Chunks[chunk], z0, z1);
                    });
                    live.RemoveAll(id => legacy.Objects[id].IsDestroyed);
                    Assert.Equal(legacy.Objects.Select(o => o.IsDestroyed), dropped.Objects.Select(o => o.IsDestroyed));
                }

                AssertSame(legacy, dropped);
            }

            // the run reached the cases it is for: dynamics held, some alone on a tile (no anchor), some on landless tiles
            Assert.True(dropped.Chunks.Sum(c => c.Dynamic?.Count ?? 0) > 5);
            Assert.Contains(dropped.Chunks, c => c.Dynamic != null && c.Dynamic.CountAt(3 << 3) > 0 && c.Tiles[3, 0] == null);
            Assert.Contains(dropped.Chunks, c => c.Dynamic != null && c.Dynamic.CountAt(2 << 3) > 0);

            // the chunks' teardown destroys every object on them, the index's included, and empties index and shadow
            Both(legacy, dropped, s =>
            {
                foreach (Chunk c in s.Chunks)
                {
                    c.Destroy();
                }
            });

            Assert.Equal(legacy.Objects.Select(o => o.IsDestroyed), dropped.Objects.Select(o => o.IsDestroyed));
            Assert.Equal(legacy.Objects.Select(o => o.TileChunk == null), dropped.Objects.Select(o => o.TileChunk == null));

            foreach (Chunk c in dropped.Chunks)
            {
                Assert.True(c.Dynamic == null || c.Dynamic.Count == 0);

                for (int cell = 0; cell < 64; cell++)
                {
                    Assert.Null(c.Tiles[cell >> 3, cell & 7]);
                    Assert.Empty(c.Shadow?.Cell(cell) ?? new List<GameObject>());
                }
            }
        }

        [Theory]
        [InlineData(11, true, true)]
        [InlineData(12, false, true)]
        [InlineData(13, true, false)]
        [InlineData(14, false, false)]
        public void Random_Operations_With_The_List_Dropped_Equal_The_Legacy_List(int seed, bool shadow, bool effects)
        {
            RunRandom(seed, 700, shadow, effects);
        }

        [Fact]
        public void The_List_Holds_No_Dynamics_And_The_Anchor_Stays_A_List_Object()
        {
            var s = new Side(true, true);
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
            Assert.Equal(new GameObject[] { st }, c.Shadow.Cell(4 << 3 | 4));
        }

        [Fact]
        public void Chunk_Teardown_Destroys_The_Index_Objects_And_Releases_The_Player()
        {
            foreach (bool reload in new[] { true, false })
            {
                var s = new Side(true, true);
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
                Assert.Empty(c.Shadow.Cell(5 << 3 | 5));
                Assert.Empty(c.Shadow.Cell(0));
            }
        }

        [Fact]
        public void The_Checker_Flags_A_Dynamic_In_The_List_A_Shadow_Gap_And_A_Lost_Entry()
        {
            var s = new Side(true, true);
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

            // the shadow (legacy order) loses an object: the merged order no longer equals it
            c.Shadow.Remove(b, 2 << 3 | 2);
            r = TileOrder.CheckIndex(c, 2, 2);
            Assert.True(r.OrderMismatches > 0);
            Assert.True(r.CountMismatch);
            Assert.False(TileOrder.IsReachable(b));
            c.Shadow.Add(b, 2 << 3 | 2, b.PriorityZ, -1);
            Assert.False(TileOrder.CheckIndex(c, 2, 2).Failed);

            // the index loses an object: unreachable, and the order differs from the shadow
            c.Dynamic.Remove(a, 2 << 3 | 2);
            Assert.False(TileOrder.IsReachable(a));
            Assert.True(TileOrder.CheckIndex(c, 2, 2).Failed);
        }

        [Fact]
        public void Without_A_Shadow_The_Unmerged_Reader_Still_Reaches_The_Index()
        {
            var s = new Side(true, false);
            Chunk c = s.Chunks[0];
            Place(c, NewLand(s.World, 0), 1, 1);
            Item item = s.World.GetOrCreateItem(0x4000_0005);
            item.Graphic = Plain;
            Place(c, item, 1, 1);
            Assert.Null(c.Shadow);
            Assert.Equal(2, ReaderList(c, 1 << 3 | 1, false, null).Count);
            Assert.False(c.HasNoExternalData());
        }

        [Fact]
        public void With_The_Switch_Off_Nothing_Changes()
        {
            var s = new Side(false, false);
            Chunk c = s.Chunks[0];
            Place(c, NewLand(s.World, 0), 1, 1);
            Item item = s.World.GetOrCreateItem(0x4000_0006);
            item.Graphic = Plain;
            Place(c, item, 1, 1);
            Assert.Null(c.Shadow);
            Assert.Same(item, c.GetHeadObject(1, 1).TNext);
            c.Destroy();
            Assert.True(item.IsDestroyed);
        }

        [Fact]
        public void Walk_Off_With_The_List_Dropped_Is_The_Shadow_List()
        {
            if (s_setWalk == null)
            {
                return; // release client build: the walk switch is read once and there is no shadow walk
            }

            bool SetWalk(bool on) => (bool)s_setWalk.Invoke(null, new object[] { on });
            bool ShadowWalk() => (bool)s_shadowWalk.GetValue(null);

            var s = new Side(true, true);
            Chunk c = s.Chunks[0];
            Place(c, NewLand(s.World, 0), 3, 3);
            Item item = s.World.GetOrCreateItem(0x4000_0007);
            item.Graphic = Plain;
            Place(c, item, 3, 3);
            Item before = s.World.GetOrCreateItem(0x4000_0008);
            before.Graphic = Plain;
            Place(c, before, 3, 3);
            s_walkOn.SetValue(null, true);

            // corrupt the index's order (swap the two entries' objects): the merged walk now differs from the legacy order
            DynamicTileIndex.Entry[] raw = c.Dynamic.RawCell(3 << 3 | 3, out int n);
            Assert.Equal(2, n);
            (raw[0].Obj, raw[1].Obj) = (raw[1].Obj, raw[0].Obj);
            List<GameObject> legacy = c.Shadow.Cell(3 << 3 | 3).ToList();
            Assert.NotEqual(legacy, MergedList(c, 3 << 3 | 3));

            Assert.True(SetWalk(false));
            Assert.True(ShadowWalk());
            Assert.True(DynamicTileIndex.WalkOn);
            Assert.False(DynamicTileIndex.IndexWalkOn);
            Assert.Equal(legacy, Walk(c, 3 << 3 | 3));
            Assert.Equal(legacy, ReaderList(c, 3 << 3 | 3, true, null));

            Assert.True(SetWalk(true));
            Assert.False(ShadowWalk());
            Assert.True(DynamicTileIndex.IndexWalkOn);
            Assert.NotEqual(legacy, Walk(c, 3 << 3 | 3));

            // no shadow: walk off is refused (the list alone misses the dynamics)
            DynamicTileIndex.Shadow = false;
            Assert.False(SetWalk(false));
            Assert.False(ShadowWalk());
            Assert.True(DynamicTileIndex.IndexWalkOn);
        }

        // tile-index-2: the merge compares an index entry with a list object by the stored PriorityZ first and builds the list
        // object's full key (rank by type, stamp) only on equal priorities; the answer must be the full key comparison's
        [Fact]
        public void The_Priority_First_Merge_Comparison_Equals_The_Full_Key_Comparison()
        {
            var s = new Side(true, false);
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

        private static List<GameObject> Walk(Chunk c, int cell)
        {
            var list = new List<GameObject>();

            for (GameObject o = TileWalk.Begin(c, cell, out TileWalk w); o != null; o = w.Next())
            {
                list.Add(o);
            }

            return list;
        }

        [Fact]
        public void The_Unmerged_Reader_Reads_The_Legacy_Order_Not_The_Index()
        {
            // the readers' checks (CUO_TILE_ORDER_CHECK) run the other walk: with the list dropped that must be the shadow
            // list's legacy order, or a broken index would be compared with itself
            var s = new Side(true, true);
            Chunk c = s.Chunks[0];
            Place(c, NewLand(s.World, 0), 6, 6);
            Item a = s.World.GetOrCreateItem(0x4000_0009);
            a.Graphic = Plain;
            Place(c, a, 6, 6);
            c.Dynamic.Remove(a, 6 << 3 | 6);
            Assert.Contains(a, ReaderList(c, 6 << 3 | 6, false, null));
            Assert.DoesNotContain(a, ReaderList(c, 6 << 3 | 6, true, null));
        }

        // tile-index-fix-a: a whole-tile walk of a tile whose index cell is empty follows TNext (merged false) and must see what
        // the merge and the legacy order see; a tile with dynamics still merges; the shadow walk (dev) bypasses the plain walk
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Whole_Tile_Start_Takes_The_List_Walk_Only_On_Tiles_Without_Dynamics(bool dropped)
        {
            var s = new Side(dropped, dropped);
            Chunk c = s.Chunks[0];

            Item NewItem(uint serial)
            {
                Item i = s.World.GetOrCreateItem(serial);
                i.Graphic = Plain;
                return i;
            }

            // (0,0) land, static, multi; (1,0) land, static, item, mobile; (2,0) land and an item that leaves;
            // (3,0) a landless mobile; (4,0) land only; (5,0) land and a mobile that steps away to (1,0)
            Place(c, NewLand(s.World, 0), 0, 0);
            Place(c, new Static(s.World) { Graphic = Tall, Z = 0 }, 0, 0);
            Place(c, new Multi(s.World) { Graphic = Plain, Z = 2 }, 0, 0);
            Place(c, NewLand(s.World, 0), 1, 0);
            Place(c, new Static(s.World) { Graphic = Plain, Z = 0 }, 1, 0);
            Place(c, NewItem(0x4000_0010), 1, 0);
            Place(c, s.World.GetOrCreateMobile(0x10), 1, 0);
            Place(c, NewLand(s.World, 0), 2, 0);
            Item leaves = NewItem(0x4000_0011);
            Place(c, leaves, 2, 0);
            leaves.RemoveFromTile();
            Place(c, s.World.GetOrCreateMobile(0x11), 3, 0);
            Place(c, NewLand(s.World, 0), 4, 0);
            Place(c, NewLand(s.World, 0), 5, 0);
            Mobile steps = s.World.GetOrCreateMobile(0x12);
            Place(c, steps, 5, 0);
            steps.Z = 1;
            Place(c, steps, 1, 0);

            bool[] dynamics = { false, true, false, true, false, false };

            for (int x = 0; x < dynamics.Length; x++)
            {
                int cell = x << 3;
                List<GameObject> legacy = dropped ? c.Shadow.Cell(cell).ToList() : RealList(c, x, 0);
                Assert.NotEmpty(legacy);
                Assert.Equal(dynamics[x], c.Dynamic != null && c.Dynamic.CountAt(cell) > 0);
                Assert.Equal(legacy, MergedList(c, cell));

                var seen = new List<GameObject>();
                GameObject first = TileWalk.First(c, x, 0);
                bool merged = false;

                for (GameObject o = TileWalk.Start(first, c, false, true, out TileWalk w, out merged); o != null; o = merged ? w.Next() : o.TNext)
                {
                    seen.Add(o);
                    Assert.True(seen.Count < 100_000);
                }

                Assert.Equal(dynamics[x], merged);
                Assert.Equal(legacy, seen);

                if (s_shadowWalk != null)
                {
                    s_shadowWalk.SetValue(null, true);

                    try
                    {
                        seen.Clear();

                        for (GameObject o = TileWalk.Start(first, c, false, true, out TileWalk w, out merged); o != null; o = merged ? w.Next() : o.TNext)
                        {
                            seen.Add(o);
                        }

                        Assert.True(merged);
                        Assert.Equal(legacy, seen);
                    }
                    finally
                    {
                        s_shadowWalk.SetValue(null, false);
                    }
                }
            }
        }
    }
}
