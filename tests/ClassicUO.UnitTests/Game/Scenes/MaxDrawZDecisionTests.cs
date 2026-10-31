using System;
using ClassicUO.Assets;
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
        public void An_In_Place_Graphic_Change_Recomputes_After_InvalidateDecision()
        {
            // World.ChangeSeason swaps graphics in place (no tile-list change) and then calls Map.InvalidateDecision
            var map = new MapT(_world, 0, 4, 4);
            map.LoadEmptyChunkForTest(0, 0);
            Static s = Put(map, Plain, 7, 7, 20);

            Update(map, 6, 6, 0).Should().BeTrue();
            AssertOutside();

            s.Graphic = Roof; // the season's graphic
            Update(map, 6, 6, 0).Should().BeFalse("nothing tells the decision without the invalidation");

            map.InvalidateDecision();
            Update(map, 6, 6, 0).Should().BeTrue();
            AssertHidden(20);
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
