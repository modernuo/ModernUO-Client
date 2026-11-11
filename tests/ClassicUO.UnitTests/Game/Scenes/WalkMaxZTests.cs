using System;
using System.Collections.Generic;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// clip-1 (walk_maxz_continue): the walk's max-Z cut. A tile list is in PriorityZ order, so a tall static (PriorityZ 101,
    /// height 60: top 161 over the walk's 150) comes before a lower one (PriorityZ 102, height 1: top 103). The old rule ends
    /// the tile's walk at the tall one and drops the lower one; the new rule skips only the tall one.
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide
    public sealed class WalkMaxZTests : IDisposable
    {
        private const int MaxZ = 150;
        private const ushort Tall = 2, Short = 3, TallToo = 4;

        private readonly StaticTiles[] _data = new StaticTiles[0x10000];
        private readonly StaticTiles[] _saved;

        public WalkMaxZTests()
        {
            _data[Tall].Height = 60;
            _data[Short].Height = 1;
            _data[TallToo].Height = 70;
            _saved = Chunk.StaticTileData;
            Chunk.StaticTileData = _data;
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _saved;
            WalkMaxZ.OverrideForTests = null;
        }

        private static Land NewLand(World world, sbyte z)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);
            land.Z = z;

            return land;
        }

        private static Static Add(Chunk chunk, Static s)
        {
            s.X = 3;
            s.Y = 4;
            chunk.AddGameObject(s, 3, 4);

            return s;
        }

        // the tile: land z 0, the tall static (z 100: PriorityZ 101), the short one (z 101: PriorityZ 102), another tall one
        private (Chunk chunk, Static tall, Static low, Static tall2) Tile(World world)
        {
            var chunk = new Chunk(world) { X = 0, Y = 0 };
            Land land = NewLand(world, 0);
            land.X = 3;
            land.Y = 4;
            chunk.AddGameObject(land, 3, 4);
            Static tall = Add(chunk, new Static(world) { Graphic = Tall, Z = 100 });
            Static low = Add(chunk, new Static(world) { Graphic = Short, Z = 101 });
            Static tall2 = Add(chunk, new Static(world) { Graphic = TallToo, Z = 102 });

            return (chunk, tall, low, tall2);
        }

        [Fact]
        public void TileListIsInPriorityZOrderNotTopZ()
        {
            var world = new World();
            var (chunk, tall, low, tall2) = Tile(world);

            Assert.Equal(101, tall.PriorityZ);
            Assert.Equal(102, low.PriorityZ);
            Assert.Same(low, tall.TNext);
            Assert.Same(tall2, low.TNext);
            Assert.True(WalkMaxZ.TopZ(tall, _data) > MaxZ);
            Assert.True(WalkMaxZ.TopZ(low, _data) <= MaxZ);
            Assert.True(WalkMaxZ.TopZ(tall2, _data) > MaxZ);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheCutEndsTheWalkOnlyWithTheSwitchOff(bool on)
        {
            WalkMaxZ.OverrideForTests = on;
            Assert.Equal(!on, WalkMaxZ.EndsWalk());

            var world = new World();
            var (chunk, tall, low, _) = Tile(world);

            // the walk's drawn candidates: every object not over the max Z, until a cut ends the walk
            var walked = new List<GameObject>();

            for (TileListObject o = chunk.GetHeadObject(3, 4); o != null; o = o.TNext)
            {
                if (WalkMaxZ.TopZ(o, _data) > MaxZ)
                {
                    if (WalkMaxZ.EndsWalk())
                    {
                        break;
                    }

                    continue;
                }

                walked.Add(o);
            }

            Assert.Equal(on, walked.Contains(low));
            Assert.DoesNotContain(tall, walked);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AfterCutListsTheLowerObjectsBehindTheCut(bool merged)
        {
            var world = new World();
            var (chunk, tall, low, _) = Tile(world);
            var behind = new List<GameObject>();
            TileWalk walk = default;

            if (merged)
            {
                // the merged reader positioned after the cut object (TileWalk.Begin, then Next to the tall static)
                for (GameObject o = TileWalk.Begin(chunk, 3 << 3 | 4, out walk); !ReferenceEquals(o, tall); o = walk.Next())
                {
                    Assert.NotNull(o);
                }
            }

            WalkMaxZ.AfterCut(merged ? null : tall.TNext, merged, walk, MaxZ, _data, behind);

            Assert.Equal(new GameObject[] { low }, behind);
        }

        [Fact]
        public void AfterCutOfASingleObjectWalkIsEmpty()
        {
            var behind = new List<GameObject>();
            WalkMaxZ.AfterCut(null, false, default, MaxZ, _data, behind);
            Assert.Empty(behind);
        }
    }
}
