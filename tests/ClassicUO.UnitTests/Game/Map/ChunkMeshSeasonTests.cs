using System;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using FluentAssertions;
using Xunit;
using MapT = ClassicUO.Game.Map.Map;

namespace ClassicUO.UnitTests.Game.Map
{
    /// <summary>
    /// A season change (World.ChangeSeason -> UpdateGraphicBySeason -> the graphic setter) swaps placed land, statics and
    /// multi components in place, without a tile-list change. The Legacy chunk mesh baked the old graphic's art, so the
    /// change must mark the chunk's mesh dirty; before the fix meshed statics kept the previous season until the chunk was
    /// rebuilt for another reason.
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide
    public sealed class ChunkMeshSeasonTests : IDisposable
    {
        private const ushort Summer = 1, Winter = 2;

        private readonly StaticTiles[] _savedStaticData;
        private readonly World _world = new World();
        private readonly MapT _map;
        private readonly Chunk _chunk;

        public ChunkMeshSeasonTests()
        {
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = new StaticTiles[0x10000];
            _map = new MapT(_world, 0, 4, 4);
            _chunk = _map.LoadEmptyChunkForTest(0, 0);
        }

        public void Dispose() => Chunk.StaticTileData = _savedStaticData;

        private T Place<T>(T obj, int x, int y) where T : GameObject
        {
            obj.X = (ushort)x;
            obj.Y = (ushort)y;
            _chunk.AddGameObject(obj, x, y);
            _chunk.Mesh.IsDirty = false; // as after the mesh build
            return obj;
        }

        [Fact]
        public void A_Placed_Static_Swapped_In_Place_Dirties_The_Mesh()
        {
            Static s = Place(new Static(_world) { Graphic = Summer }, 3, 4);

            s.SetGraphic(Winter);
            _chunk.Mesh.IsDirty.Should().BeTrue();

            _chunk.Mesh.IsDirty = false;
            s.SetGraphic(Summer); // back to the original (RestoreOriginalGraphic, another season)
            _chunk.Mesh.IsDirty.Should().BeTrue();
        }

        [Fact]
        public void An_Unchanged_Graphic_Keeps_The_Mesh()
        {
            Static s = Place(new Static(_world) { Graphic = Summer }, 3, 4);

            // a season without a swap for this graphic: the mesh stays built
            s.SetGraphic(Summer);
            _chunk.Mesh.IsDirty.Should().BeFalse();
        }

        [Fact]
        public void Placed_Land_And_Multi_Swapped_In_Place_Dirty_The_Mesh()
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new object[] { _world }, null);
            land.Graphic = Summer;
            Place(land, 1, 1);
            land.Graphic = Winter;
            _chunk.Mesh.IsDirty.Should().BeTrue();

            Multi m = Place(new Multi(_world) { Graphic = Summer }, 2, 2);
            m.Graphic = Winter;
            _chunk.Mesh.IsDirty.Should().BeTrue();
        }

        [Fact]
        public void An_Unplaced_Static_Changes_Graphic_Without_A_Chunk()
        {
            var s = new Static(_world) { Graphic = Summer };
            s.SetGraphic(Winter);
            s.Graphic.Should().Be(Winter);
        }
    }
}
