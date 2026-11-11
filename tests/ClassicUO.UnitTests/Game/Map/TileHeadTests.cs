using ClassicUO.UnitTests.Ecs;
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
    /// tile-head: <see cref="Chunk.GetHeadObject"/> returns a stored per-cell head instead of following TPrevious from the anchor.
    /// Random placements, moves, removals (land and statics too), in-place reloads and chunk teardown with pool reuse; after
    /// every step every cell's stored head must be the head found by TPrevious from the anchor, and the list read from it must
    /// equal an independent copy of the legacy insertion rule without the dynamics (the list holds none).
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide
    public sealed class TileHeadTests : IDisposable
    {
        private const ushort Plain = 1, Tall = 2, Background = 3, MultiMovable = 4, TallBackground = 5;

        private readonly StaticTiles[] _savedStaticData;

        public TileHeadTests()
        {
            var data = new StaticTiles[0x10000];
            data[Tall].Height = 5;
            data[Background].Flags = TileFlag.Background;
            data[MultiMovable].Flags = TileFlag.MultiMovable;
            data[TallBackground].Height = 3;
            data[TallBackground].Flags = TileFlag.Background;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
        }

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

        private static uint s_nextItem = 0x4100_0000, s_nextMobile = 0x0010_0000;

        // kind: 0 land, 1..3 multis (plain, preview, internal), 4 static, 5 mobile, 6 corpse, 7 item, 8 effect
        private static GameObject New(World w, int kind, ushort graphic, sbyte z)
        {
            switch (kind)
            {
                case 0: return NewLand(w, z);
                case 1: return new Multi(w) { Graphic = graphic, Z = z };
                case 2: return new Multi(w) { Graphic = graphic, Z = z, State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW };
                case 3: return new Multi(w) { Graphic = graphic, Z = z, State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL };
                case 4: return new Static(w) { Graphic = graphic, Z = z };
                case 5:
                {
                    Mobile m = w.GetOrCreateMobile(s_nextMobile++);
                    m.Z = z;
                    return m;
                }
                case 6:
                case 7:
                {
                    Item item = w.GetOrCreateItem(s_nextItem++);
                    item.TestSetGraphic(kind == 6 ? (ushort)0x2006 : graphic);
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

        /// <summary>The legacy insertion rule's state for an object AddGameObject just placed (its switch, verbatim).</summary>
        private static sbyte State(GameObject obj) => obj switch
        {
            Land => 0,
            Multi m when (m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0 => 1,
            Multi m when (m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) != 0 => 2,
            Multi => 1,
            _ => -1
        };

        private sealed class Model
        {
            // per chunk slot: the legacy order of every object per cell (dynamics included), by the list's insertion rule
            public readonly ShadowTileList[] Cells = { new ShadowTileList(), new ShadowTileList() };
            public readonly Dictionary<GameObject, int> Slot = new Dictionary<GameObject, int>(ReferenceEqualityComparer.Instance);

            public void Remove(GameObject obj)
            {
                if (Slot.Remove(obj, out int slot))
                {
                    Assert.True(Cells[slot].Remove(obj, obj.TileCell));
                }
            }
        }

        private static void Place(Chunk[] chunks, Model model, int slot, GameObject obj, int cell)
        {
            model.Remove(obj);
            Chunk chunk = chunks[slot];
            obj.X = (ushort)(chunk.X * 8 + (cell >> 3));
            obj.Y = (ushort)(chunk.Y * 8 + (cell & 7));
            chunk.AddGameObject(obj, cell >> 3, cell & 7);
            model.Cells[slot].Add(obj, cell, obj.PriorityZ, State(obj));
            model.Slot[obj] = slot;
        }

        private static List<GameObject> Chain(Chunk chunk, int cell)
        {
            var list = new List<GameObject>();

            for (TileListObject o = chunk.GetHeadObject(cell >> 3, cell & 7); o != null; o = o.TNext)
            {
                list.Add(o);
                Assert.True(list.Count < 10_000);
            }

            return list;
        }

        private static int s_frontInsertions, s_headRemovals;

        private static void AssertHeads(Chunk[] chunks, Model model)
        {
            for (int slot = 0; slot < chunks.Length; slot++)
            {
                Chunk chunk = chunks[slot];

                for (int cell = 0; cell < 64; cell++)
                {
                    int x = cell >> 3, y = cell & 7;
                    TileListObject anchor = chunk.Tiles[x, y];
                    TileListObject walked = TileOrder.FindHead(anchor, out bool cycle);
                    Assert.False(cycle);
                    // the stored head is the TPrevious walk's head (both null on an empty list)
                    Assert.Same(walked, chunk.GetHeadObject(x, y));
                    Assert.Same(walked, chunk.HeadAt(cell));
                    Assert.Null(walked?.TPrevious);

                    // the list read from it is the legacy order without the dynamics
                    List<GameObject> expected = (model.Cells[slot].Cell(cell) ?? new List<GameObject>()).Where(o => !DynamicTileIndex.Holds(o)).ToList();

                    Assert.Equal(expected, Chain(chunk, cell));

                    if (anchor != null)
                    {
                        TileOrder.TileCheck check = TileOrder.CheckTile(chunk, x, y);
                        Assert.False(check.Failed, $"cell {cell}: head wrong {check.HeadWrong}, anchor missed {check.AnchorMissed}");
                    }
                }
            }
        }

        private static void RunRandom(int seed, int steps, bool effects)
        {
            s_frontInsertions = s_headRemovals = 0;

            var world = new World();
            var chunks = new[] { Chunk.Create(world, 0, 0), Chunk.Create(world, 1, 0) };
            var model = new Model();
            var rng = new Random(seed);
            var live = new List<GameObject>();
            int teardowns = 0, reuses = 0;

            // a few cells only, so lists get long and heads change often; cell 9 starts landless
            int[] cells = { 0, 1, 9, 18 };

            for (int slot = 0; slot < 2; slot++)
            {
                foreach (int cell in new[] { 0, 1, 18 })
                {
                    GameObject land = New(world, 0, 0, (sbyte)rng.Next(-4, 10));
                    Place(chunks, model, slot, land, cell);
                    live.Add(land);
                }
            }

            AssertHeads(chunks, model);

            for (int i = 0; i < steps; i++)
            {
                int op = rng.Next(40);
                int slot = rng.Next(2);
                int cell = cells[rng.Next(cells.Length)];

                if (op < 14 || live.Count == 0)
                {
                    int kind = rng.Next(effects ? 9 : 8);
                    var obj = New(world, kind, (ushort)rng.Next(Plain, TallBackground + 1), (sbyte)rng.Next(-6, 12));
                    GameObject before = chunks[slot].GetHeadObject(cell >> 3, cell & 7);
                    Place(chunks, model, slot, obj, cell);

                    if (before != null && ReferenceEquals(chunks[slot].GetHeadObject(cell >> 3, cell & 7), obj))
                    {
                        s_frontInsertions++;
                    }

                    live.Add(obj);
                }
                else if (op < 24)
                {
                    // a move, possibly to the other chunk, with a new Z
                    GameObject obj = live[rng.Next(live.Count)];
                    obj.Z = (sbyte)rng.Next(-6, 12);
                    Place(chunks, model, slot, obj, cell);
                }
                else if (op < 34)
                {
                    // any object leaves its tile (land and statics too: the anchor and the head move on)
                    GameObject obj = live[rng.Next(live.Count)];

                    if (obj.TileChunk != null && ReferenceEquals(obj.TileChunk.HeadAt(obj.TileCell), obj))
                    {
                        s_headRemovals++;
                    }

                    model.Remove(obj);
                    obj.RemoveFromTile();
                    live.Remove(obj);
                }
                else if (op < 36)
                {
                    // the same object placed twice in a row (AddGameObject removes it first)
                    GameObject obj = live[rng.Next(live.Count)];
                    Place(chunks, model, slot, obj, cell);
                    Place(chunks, model, slot, obj, cell);
                }
                else if (op < 38)
                {
                    // an in-place reload (UltimaLive): every object on the chunk is destroyed (or unlinked)
                    chunks[slot].ClearForReload();
                    Forget(slot);
                }
                else
                {
                    // eviction: the chunk is destroyed into the pool and a chunk is taken for the same slot (normally the same one)
                    Chunk old = chunks[slot];
                    old.Destroy();
                    Forget(slot);
                    teardowns++;
                    chunks[slot] = Chunk.Create(world, slot, 0);
                    reuses += ReferenceEquals(old, chunks[slot]) ? 1 : 0;
                }

                AssertHeads(chunks, model);
            }

            // the run reached what it is for: new heads in front of an old head, and heads that left
            Assert.True(s_frontInsertions > 20, $"front insertions {s_frontInsertions}");
            Assert.True(s_headRemovals > 20, $"head removals {s_headRemovals}");
            Assert.True(teardowns > 3 && reuses > 0, $"teardowns {teardowns}, reuses {reuses}");

            void Forget(int s)
            {
                foreach (GameObject o in model.Slot.Where(kv => kv.Value == s).Select(kv => kv.Key).ToList())
                {
                    model.Slot.Remove(o);
                    live.Remove(o);
                }

                model.Cells[s] = new ShadowTileList();
            }
        }

        [Theory]
        // the list holds no dynamics; with and without effects
        [InlineData(25, true)]
        [InlineData(26, false)]
        [InlineData(27, true)]
        public void The_Stored_Head_Is_The_Walked_Head_After_Every_Operation(int seed, bool effects)
        {
            RunRandom(seed, 1500, effects);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Teardown_Leaves_No_Head_When_The_Player_Heads_A_Tile(bool reload)
        {
            // the player is unlinked by the teardown without RemoveFromTile (it is not destroyed), so only the teardown's own
            // clear drops it as a head
            var world = new World();
            var player = (PlayerMobile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerMobile));
            typeof(BaseGameObject).GetField("<World>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, world);
            player.PendingBlock = -1;
            typeof(World).GetProperty(nameof(World.Player)).SetValue(world, player);
            Chunk chunk = Chunk.Create(world, 0, 0);
            Land land = NewLand(world, 5);
            chunk.AddGameObject(land, 3, 3);
            player.Z = -20;
            chunk.AddGameObject(player, 3, 3);
            chunk.AddGameObject(player, 4, 4); // alone on a tile too, after a move
            chunk.AddGameObject(player, 3, 3);
            Assert.Same(land, chunk.GetHeadObject(3, 3));

            if (reload)
            {
                chunk.ClearForReload();
            }
            else
            {
                chunk.Destroy();
                Assert.Same(chunk, Chunk.Create(world, 0, 0)); // the pooled chunk comes back
            }

            Assert.Null(player.TileChunk);

            for (int cell = 0; cell < 64; cell++)
            {
                Assert.Null(chunk.HeadAt(cell));
            }
        }

        [Fact]
        public void The_Checker_Flags_A_Stale_Head()
        {
            var world = new World();
            var chunk = new Chunk(world);
            Land land = NewLand(world, 5);
            chunk.AddGameObject(land, 2, 2);
            var below = new Static(world) { Graphic = Plain, Z = -10, X = 2, Y = 2 };
            land.X = land.Y = 2;
            chunk.AddGameObject(below, 2, 2);
            Assert.Same(below, chunk.GetHeadObject(2, 2));
            Assert.False(TileOrder.CheckTile(chunk, 2, 2).Failed);

            // a link changed behind the chunk's back: the stored head is no longer the list's head
            below.TNext = null;
            land.TPrevious = null;
            TileOrder.TileCheck check = TileOrder.CheckTile(chunk, 2, 2);
            Assert.True(check.HeadWrong);
            Assert.True(check.Failed);
        }
    }
}
