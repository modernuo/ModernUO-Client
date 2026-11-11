using System;
using System.IO;
using ClassicUO.Assets;
using Uo.Pack;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;
using MapT = ClassicUO.Game.Map.Map;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>Chunk.StaticTileData (read by Chunk.AddGameObject) is process-wide: the classes that set it don't run in parallel.</summary>
    [CollectionDefinition(nameof(ChunkStaticTileDataCollection), DisableParallelization = true)]
    public sealed class ChunkStaticTileDataCollection { }

    /// <summary>
    /// GameScene.UpdateMaxDrawZ's decision (MaxDrawZState.Update, the code GameScene calls with the player's x/y/z, DrawRoofs
    /// and the loaded tiledata) on a real Map and real Chunks (the test seams only replace the map files: chunks are put in
    /// empty and filled through Chunk.AddGameObject, the path every placement takes). Covers the known-issues "roof over the
    /// player is sometimes drawn" race and the Codex audit of its first fix: the recursive roof search, a facet change, a
    /// roof placed or removed nearby, an in-place graphic change (season), and the south map edge.
    /// </summary>
    [Collection(nameof(ChunkStaticTileDataCollection))]
    public sealed class MaxDrawZDecisionTests : IDisposable
    {
        private const ushort Plain = 1, Roof = 2;

        private readonly StaticTiles[] _tiles = new StaticTiles[0x10000];
        private readonly StaticTiles[] _savedStaticData;
        private readonly World _world = new World();
        private readonly MaxDrawZState _state = new MaxDrawZState();

        public MaxDrawZDecisionTests()
        {
            _tiles[Roof].Flags = TileFlag.Roof;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = _tiles;
        }

        public void Dispose() => Chunk.StaticTileData = _savedStaticData;

        private bool Update(MapT map, int x, int y, int z, bool drawRoofs = true) =>
            _state.Update(map, _tiles, x, y, z, drawRoofs, force: false);

        private Static Put(MapT map, ushort graphic, int x, int y, sbyte z)
        {
            var s = new Static(_world) { Graphic = graphic, X = (ushort)x, Y = (ushort)y, Z = z };
            map.GetChunk(x, y, false).AddGameObject(s, x % 8, y % 8);

            return s;
        }

        private void AssertHidden(int maxZ)
        {
            _state.NoDrawRoofs.Should().BeTrue("the player stands under the roof");
            _state.MaxZ.Should().Be(maxZ);
        }

        private void AssertOutside()
        {
            _state.NoDrawRoofs.Should().BeFalse();
            _state.MaxZ.Should().Be(127);
        }

        private static ArtLoader ArtFor(params ushort[] graphicsWithArt)
        {
            var sprites = new SpriteEntry[0x14000];

            foreach (ushort g in graphicsWithArt)
            {
                sprites[g + 0x4000] = new SpriteEntry { Page = 1, W = 44, H = 44, OrigW = 44, OrigH = 44 };
            }

            var arts = new ArtLoader(new UOFileManager(default, Path.GetTempPath(), null));
            arts.SetTablesForTests(sprites);

            return arts;
        }

        [Fact]
        public void A_Component_Without_Art_Does_Not_Lower_Max_Z_A_Component_With_Art_Does()
        {
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            map.LoadEmptyChunkForTest(0, 1);
            map.LoadEmptyChunkForTest(1, 0);
            map.LoadEmptyChunkForTest(1, 1);
            // on the player's own tile: the first pass counts any non-roof static there (the diagonal pass counts only roofs)
            Put(map, Plain, 6, 6, 20); // empty tiledata entry, no art (a padded entry)
            Put(map, Roof, 7, 7, 40);

            _state.Update(map, _tiles, 6, 6, 0, true, force: true, ArtFor(Roof)).Should().BeTrue();
            _state.MaxZ.Should().Be(40, "the artless component at z 20 is skipped");

            _state.Update(map, _tiles, 6, 6, 0, true, force: true, ArtFor(Plain, Roof)).Should().BeTrue();
            _state.MaxZ.Should().Be(20, "with art the same component hides what is above it");

            _state.Update(map, _tiles, 6, 6, 0, true, force: true, arts: null);
            _state.MaxZ.Should().Be(20, "no art index: every graphic counts");
        }

        [Fact]
        public void HasDrawableArt_Follows_The_Art_Index()
        {
            ArtLoader arts = ArtFor(Roof);
            MaxDrawZState.HasDrawableArt(arts, Roof).Should().BeTrue();
            MaxDrawZState.HasDrawableArt(arts, Plain).Should().BeFalse();
            MaxDrawZState.HasDrawableArt(arts, 0xFFFF).Should().BeFalse();
            MaxDrawZState.HasDrawableArt(null, Plain).Should().BeTrue();
        }

        [Fact]
        public void A_Missing_Chunk_Then_A_Loaded_Roof_Is_Recomputed_At_The_Same_Position_Without_Retrying_Every_Frame()
        {
            // player at (7, 7) in chunk (0, 0); the diagonal tile (8, 8), where the roof is, is in chunk (1, 1), not loaded yet
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            map.LoadEmptyChunkForTest(0, 1); // the roof search's neighbours of (8, 8)
            map.LoadEmptyChunkForTest(1, 0);

            Update(map, 7, 7, 0).Should().BeTrue();
            AssertOutside();
            _state.Missing.Should().Be(1);
            map.IsQueuedForTest(1, 1).Should().Be(MapT.Incremental, "the missing chunk is requested (sticky)");

            // nothing changed: no recompute (the first fix retried every frame while a chunk was missing)
            int computations = _state.Computations;
            Update(map, 7, 7, 0).Should().BeFalse();
            Update(map, 7, 7, 0).Should().BeFalse();
            _state.Computations.Should().Be(computations);

            // the chunk loads (empty block: the load alone invalidates), then the roof arrives
            map.LoadEmptyChunkForTest(1, 1);
            map.DecisionDirty.Should().BeTrue();
            Put(map, Roof, 8, 8, 20);

            Update(map, 7, 7, 0).Should().BeTrue("one recompute for both changes, at an unchanged position");
            AssertHidden(20);
            _state.Missing.Should().Be(0);
            _state.Computations.Should().Be(computations + 1);
            Update(map, 7, 7, 0).Should().BeFalse();
        }

        [Fact]
        public void The_Recursive_Roof_Search_Is_Recomputed_When_A_Chunk_It_Needed_Loads()
        {
            // the audit's example: player (6, 6, 0), diagonal roof (7, 7, 30), connected roof (8, 7, 24) in chunk (1, 0)
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            map.LoadEmptyChunkForTest(0, 1);
            Put(map, Roof, 7, 7, 30);

            Update(map, 6, 6, 0).Should().BeTrue();
            AssertHidden(30);
            _state.Missing.Should().Be(1, "CalculateNearZ stopped at chunk (1, 0)");

            map.LoadEmptyChunkForTest(1, 0);
            map.LoadEmptyChunkForTest(1, 1); // the search now also looks at (8, 8)
            Put(map, Roof, 8, 7, 24);

            Update(map, 6, 6, 0).Should().BeTrue();
            AssertHidden(24);
            _state.Missing.Should().Be(0);
        }

        [Fact]
        public void A_Facet_Change_At_The_Same_Position_Recomputes()
        {
            var trammel = new MapT(_world, 1, 4, 4);
            trammel.LoadEmptyChunkForTest(0, 0);
            Update(trammel, 6, 6, 0).Should().BeTrue();
            AssertOutside();
            _state.Missing.Should().Be(0);

            // World.MapIndex replaces the Map and restores the player's x/y/z
            var felucca = new MapT(_world, 0, 4, 4);
            felucca.LoadEmptyChunkForTest(0, 0);
            Put(felucca, Roof, 7, 7, 20);
            felucca.SetDecisionReach(new ChunkReach()); // a clean map: only the Map.Id check can tell
            felucca.DecisionDirty.Should().BeFalse();

            Update(felucca, 6, 6, 0).Should().BeTrue();
            AssertHidden(20);
        }

        [Fact]
        public void A_Roof_Placed_On_Or_Removed_From_The_Diagonal_Tile_Recomputes()
        {
            // the player is outside a house's bounds, but its diagonal tile is inside (the multi-arrival callbacks only force
            // an update for a player inside)
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            Update(map, 6, 6, 0).Should().BeTrue();
            AssertOutside();

            var component = new Multi(_world) { Graphic = Roof, X = 7, Y = 7, Z = 20 };
            map.GetChunk(7, 7, false).AddGameObject(component, 7, 7);
            Update(map, 6, 6, 0).Should().BeTrue();
            AssertHidden(20);

            // house deleted or the roof removed by customisation: Destroy -> RemoveFromTile
            component.RemoveFromTile();
            Update(map, 6, 6, 0).Should().BeTrue();
            AssertOutside();
        }

        [Fact]
        public void Changes_Outside_The_Decisions_Reach_Do_Not_Recompute()
        {
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            map.LoadEmptyChunkForTest(3, 3);
            Update(map, 6, 6, 0).Should().BeTrue();

            Put(map, Roof, 30, 30, 20);
            map.LoadEmptyChunkForTest(2, 2);

            map.DecisionDirty.Should().BeFalse();
            Update(map, 6, 6, 0).Should().BeFalse();
        }

        [Fact]
        public void An_In_Place_Graphic_Change_Of_A_Placed_Object_Recomputes()
        {
            // the 0xBF/0x1B spellbook content packet sets a ground item's graphic in place (no tile-list change); so do the
            // season (Static.UpdateGraphicBySeason) and Static.SetGraphic: the Graphic setter notes the chunk
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            var book = new Item(_world) { Graphic = Plain, X = 7, Y = 7, Z = 20 };
            map.GetChunk(7, 7, false).AddGameObject(book, 7, 7);

            Update(map, 6, 6, 0).Should().BeTrue();
            AssertOutside();

            book.Graphic = Plain; // unchanged: no recompute
            Update(map, 6, 6, 0).Should().BeFalse();

            book.Graphic = Roof;
            map.DecisionDirty.Should().BeTrue();
            Update(map, 6, 6, 0).Should().BeTrue();
            AssertHidden(20);

            // a mobile's body change is not an input
            var mobile = new Mobile(_world) { Graphic = 0x190, X = 7, Y = 7, Z = 0 };
            map.GetChunk(7, 7, false).AddGameObject(mobile, 7, 7);
            Update(map, 6, 6, 0);
            mobile.Graphic = 0x191;
            map.DecisionDirty.Should().BeFalse();
        }

        [Fact]
        public void InvalidateDecision_Recomputes()
        {
            // World.ChangeSeason calls it after swapping the graphics
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            Update(map, 6, 6, 0).Should().BeTrue();
            Update(map, 6, 6, 0).Should().BeFalse();

            map.InvalidateDecision();
            Update(map, 6, 6, 0).Should().BeTrue();
        }

        [Fact]
        public void An_Off_Screen_Dependency_Stays_Loaded_While_The_Decision_Stands_And_Ages_Out_After_The_Player_Leaves()
        {
            // Codex round 2: the roof search's chunk (1, 0) is outside the draw walk, so nothing refreshes its access time;
            // evicting it dirtied the decision, whose recompute requested it again (every ~3.5 s while standing still)
            uint savedTicks = Time.Ticks;

            try
            {
                Time.Ticks = 1_000_000;
                var map = new MapT(_world, 0, 8, 8);
                map.LoadEmptyChunkForTest(0, 0);
                map.LoadEmptyChunkForTest(0, 1);
                map.LoadEmptyChunkForTest(1, 1);
                Chunk dependency = map.LoadEmptyChunkForTest(1, 0);
                Put(map, Roof, 7, 7, 30);
                Put(map, Roof, 8, 7, 24);

                Update(map, 6, 6, 0).Should().BeTrue();
                AssertHidden(24);
                map.InDecisionReach(1, 0).Should().BeTrue();

                dependency.LastAccessTime = 0; // not drawn for longer than CLEAR_TEXTURES_DELAY
                map.ClearUnusedBlocks();

                dependency.IsDestroyed.Should().BeFalse(); // (no GetChunk2 here: it refreshes the access time)
                Update(map, 6, 6, 0).Should().BeFalse("nothing was evicted, so nothing to recompute");

                // the player walks away: the new decision's reach no longer covers (1, 0), and the sweep evicts it
                map.LoadEmptyChunkForTest(5, 5);
                map.LoadEmptyChunkForTest(6, 6);
                Update(map, 46, 46, 0).Should().BeTrue();
                map.InDecisionReach(1, 0).Should().BeFalse();
                dependency.LastAccessTime = 0;
                map.ClearUnusedBlocks();

                map.GetChunk2(1, 0, false).Should().BeNull();
                dependency.IsDestroyed.Should().BeTrue();
                map.DecisionDirty.Should().BeFalse("outside the new decision's reach");
            }
            finally
            {
                Time.Ticks = savedTicks;
            }
        }

        [Fact]
        public void A_Large_Reach_Is_Not_Pinned()
        {
            var map = new MapT(_world, 0, 16, 16);
            var reach = new ChunkReach();
            reach.Add(0, 0);
            reach.Add(MapT.MaxPinnedReach, 0); // MaxPinnedReach + 1 chunks wide
            map.SetDecisionReach(reach);

            map.InDecisionReach(0, 0).Should().BeFalse();
            map.NoteContentChanged(0, 0);
            map.DecisionDirty.Should().BeTrue("a large reach still invalidates; it just does not keep its chunks");
        }

        [Fact]
        public void A_Decision_Request_Survives_When_The_Parked_Mobile_Of_Its_Chunk_Leaves_Before_The_Load()
        {
            // player (7, 7): the diagonal tile (8, 8) is in chunk (1, 1), not loaded; a mobile is parked there too
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            map.LoadEmptyChunkForTest(0, 1);
            map.LoadEmptyChunkForTest(1, 0);

            var mobile = new Mobile(_world) { X = 9, Y = 9 };
            map.Park(mobile, 9, 9).Should().Be(MapT.Incremental);

            if (!MapT.Incremental)
            {
                return; // CUO_CHUNK_SYNC=1: nothing is parked or queued
            }

            Update(map, 7, 7, 0).Should().BeTrue();
            _state.Missing.Should().Be(1);

            // the mobile walks off (or is deleted) before chunk (1, 1) loads: the parked entry is stale
            mobile.PendingBlock = -1;
            map.DropStaleParked(1 * map.BlocksY + 1, 1, 1).Should().BeFalse("the decision still needs the chunk: the load goes ahead");
            map.IsQueuedForTest(1, 1).Should().BeTrue();

            // a chunk only parked objects wanted, outside the decision's reach, is dropped as before
            var other = new Mobile(_world) { X = 25, Y = 25 };
            map.Park(other, 25, 25).Should().BeTrue();
            other.PendingBlock = -1;
            map.DropStaleParked(3 * map.BlocksY + 3, 3, 3).Should().BeTrue();
        }

        [Fact]
        public void DrawRoofs_Is_Part_Of_The_Decision()
        {
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            Update(map, 6, 6, 0).Should().BeTrue();
            _state.NoDrawRoofs.Should().BeFalse();

            Update(map, 6, 6, 0, drawRoofs: false).Should().BeTrue();
            _state.NoDrawRoofs.Should().BeTrue();
        }

        [Fact]
        public void The_South_Map_Edge_Does_Not_Alias_The_Next_Column_And_Counts_As_Known_Empty()
        {
            // 2 x 2 chunks: chunk (0, 2) used to flatten to block 2, which is chunk (1, 0)
            var map = new MapT(_world, 0, 2, 2);
            map.LoadEmptyChunkForTest(0, 1);
            map.LoadEmptyChunkForTest(1, 0);
            Put(map, Roof, 15, 0, 20); // chunk (1, 0), cell (7, 0): where tile (7, 16) aliased to

            map.GetChunk2(0, 2, false).Should().BeNull();
            map.GetChunk2(0, 2, true).Should().BeNull("an out-of-map chunk is never created either");
            map.GetChunk(7, 16, false).Should().BeNull();
            map.GetChunk2(-1, 0, false).Should().BeNull();

            // player on the last row (6, 15): the diagonal tile (7, 16) is outside the map
            Update(map, 6, 15, 0).Should().BeTrue();
            AssertOutside();
            _state.Missing.Should().Be(0, "outside the map is known empty, not missing");
            map.IsQueuedForTest(0, 2).Should().BeFalse();
            Update(map, 6, 15, 0).Should().BeFalse("no retry");
        }
    }
}
