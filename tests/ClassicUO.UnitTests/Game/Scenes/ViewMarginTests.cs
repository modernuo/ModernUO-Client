using System;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// view_margin (light-edges): the band geometry the light margin selects sources with, the switch in both states, and
    /// the chunk light lists (Chunk.LightSources) it reads instead of walking the band's tiles.
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide
    public sealed class ViewMarginTests : IDisposable
    {
        private const ushort Torch = 0x0A0F, Wall = 0x0080, Window = 0x0A10;

        private readonly StaticTiles[] _saved;

        public ViewMarginTests()
        {
            var data = new StaticTiles[0x10000];
            data[Torch].Flags = TileFlag.LightSource;
            data[Window].Flags = TileFlag.LightSource;
            _saved = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
            // the emitter table: the torch has a light shape, the flagged window has none
            var emitters = new bool[0x10000];
            emitters[Torch] = true;
            LightEmitters.OverrideForTests = emitters;
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _saved;
            LightEmitters.OverrideForTests = null;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            ViewMargin.OverrideForTests = on;

            try
            {
                Assert.Equal(on, ViewMargin.Enabled);
            }
            finally
            {
                ViewMargin.OverrideForTests = null;
            }
        }

        [Fact]
        public void Light_margin_is_the_largest_halo_radius()
        {
            Assert.Equal(175, ViewMargin.LightMargin(350));
            Assert.Equal(56, ViewMargin.LightMargin(111));
        }

        [Theory]
        // inside the walked rectangle: the walk's own
        [InlineData(0, 0, false)]
        [InlineData(-100, 600, false)]
        // just beside it, within the margin: the band
        [InlineData(-101, 0, true)]
        [InlineData(900 + 175, 0, true)]
        [InlineData(0, -100 - 175, true)]
        [InlineData(0, 600 + 1, true)]
        // beyond the margin
        [InlineData(-100 - 176, 0, false)]
        [InlineData(0, 600 + 176, false)]
        public void Band_is_the_ring_between_the_walked_and_the_grown_rectangle(int ax, int ay, bool inBand)
        {
            Assert.Equal(inBand, ViewMargin.InBand(ax, ay, -100, -100, 900, 600, 175));
        }

        [Fact]
        public void Every_tile_whose_anchor_is_in_the_grown_rectangle_is_in_a_touched_chunk_of_the_range()
        {
            var rng = new Random(7);

            for (int round = 0; round < 40; round++)
            {
                int px = 200 + rng.Next(800), py = 200 + rng.Next(800);
                int offX = (px - py) * 22 - 400, offY = (px + py) * 22 - 300;
                int left = -44 - 175, top = -44 - 175, right = 844 + 175, bottom = 644 + 175;
                ViewMargin.Bands(left, top, right, bottom, offX, offY, out int dLo, out int dHi, out int sLo, out int sHi);
                ViewMargin.ChunkRange(dLo, dHi, sLo, sHi, out int cx0, out int cy0, out int cx1, out int cy1);

                for (int x = px - 80; x <= px + 80; x++)
                {
                    for (int y = py - 80; y <= py + 80; y++)
                    {
                        int z = rng.Next(-128, 128);
                        ViewMargin.Anchor(x, y, z, offX, offY, out int ax, out int ay);

                        if (ax < left || ax > right || ay < top || ay > bottom)
                        {
                            continue;
                        }

                        int cx = x >> 3, cy = y >> 3;
                        Assert.InRange(cx, cx0, cx1);
                        Assert.InRange(cy, cy0, cy1);
                        Assert.True(ViewMargin.ChunkTouches(cx, cy, dLo, dHi, sLo, sHi), $"tile {x},{y},{z} chunk {cx},{cy}");
                    }
                }
            }
        }

        [Fact]
        public void Chunk_light_list_follows_placement_removal_and_teardown()
        {
            var world = new World();
            Chunk chunk = Chunk.Create(world, 0, 0);
            var torch = new Static(world) { Graphic = Torch, Z = 5 };
            var wall = new Static(world) { Graphic = Wall, Z = 0 };
            var lantern = new Multi(world) { Graphic = Torch, Z = 7 };

            var window = new Static(world) { Graphic = Window, Z = 0 };

            chunk.AddGameObject(torch, 1, 2);
            chunk.AddGameObject(wall, 1, 2);
            chunk.AddGameObject(lantern, 3, 3);
            // light flag without a light shape: not an emitter, no slot
            chunk.AddGameObject(window, 5, 5);
            Assert.Equal(2, chunk.LightSourceCount);

            // a move within the chunk leaves it listed once
            chunk.AddGameObject(torch, 4, 4);
            Assert.Equal(2, chunk.LightSourceCount);

            lantern.RemoveFromTile();
            Assert.Equal(1, chunk.LightSourceCount);
            Assert.Same(torch, chunk.LightSources[0]);

            wall.RemoveFromTile();
            Assert.Equal(1, chunk.LightSourceCount);

            chunk.Destroy();
            Assert.Equal(0, chunk.LightSourceCount);
            Assert.Null(chunk.LightSources[0]);
        }

        [Theory]
        // centre inside, on the edges, and halos reaching in from each side
        [InlineData(400, 300, true)]
        [InlineData(0, 0, true)]
        [InlineData(-50, 300, true)]
        [InlineData(850, 300, true)]
        [InlineData(400, -50, true)]
        [InlineData(400, 650, true)]
        // a halo one pixel short of each edge
        [InlineData(-51, 300, false)]
        [InlineData(851, 300, false)]
        [InlineData(400, -51, false)]
        [InlineData(400, 651, false)]
        public void Light_counts_only_when_its_halo_reaches_the_view(int x, int y, bool reaches)
        {
            Assert.Equal(reaches, ViewMargin.LightReaches(x, y, 100, 100, 0, 0, 800, 600));
        }

        [Fact]
        public void Light_without_a_shape_never_reaches()
        {
            Assert.False(ViewMargin.LightReaches(400, 300, 0, 0, 0, 0, 800, 600));
        }

        [Fact]
        public void Chunk_rect_grows_by_the_chunks_overhang_and_the_mobile_reach()
        {
            ViewMargin.ChunkRect(0, 0, 800, 600, 10, 400, 20, out float minX, out float minY, out float maxX, out float maxY);
            Assert.Equal(-400, minX);
            Assert.Equal(0, minY);
            Assert.Equal(800 + ViewMargin.MobileSide, maxX);
            Assert.Equal(600 + ViewMargin.MobileUp, maxY);
        }

        // the owner's tree repro, generalised: a static whose art reaches the view from beside or below it is on a tile of
        // the chunk's walk mask (the chunk rect's diamond), for random positions, Z and art within the chunk's record
        [Fact]
        public void Every_sprite_reaching_the_view_is_on_a_walked_tile_of_its_chunk()
        {
            var rng = new Random(11);
            const int vMinX = -44, vMinY = -44, vMaxX = 844, vMaxY = 644;

            for (int round = 0; round < 30; round++)
            {
                int px = 300 + rng.Next(600), py = 300 + rng.Next(600);
                int offX = (px - py) * 22 - 400, offY = (px + py) * 22 - 300;
                int w = 44 + rng.Next(400), h = 44 + rng.Next(600);
                int overLeft = (w >> 1) - 22, overRight = w - (w >> 1) - 22, overUp = h - 44;
                ViewMargin.ChunkRect(vMinX, vMinY, vMaxX, vMaxY, overLeft, overRight, overUp, out float minX, out float minY, out float maxX, out float maxY);
                FillDiamond.Bounds b = FillDiamond.Compute(offX, offY, new Microsoft.Xna.Framework.Vector2(minX, minY), new Microsoft.Xna.Framework.Vector2(maxX, maxY), false, default, 0);

                for (int x = px - 60; x <= px + 60; x++)
                {
                    for (int y = py - 60; y <= py + 60; y++)
                    {
                        int z = rng.Next(-20, 60);
                        ViewMargin.Anchor(x, y, z, offX, offY, out int ax, out int ay);
                        int x0 = ax - (w >> 1) + 22, y0 = ay - h + 44;

                        if (x0 + w < 0 || x0 > 800 || y0 + h < 0 || y0 > 600)
                        {
                            continue;
                        }

                        ulong mask = FillDiamond.ChunkMask(x >> 3, y >> 3, in b);
                        Assert.True((mask & 1UL << ((x & 7) << 3 | (y & 7))) != 0, $"tile {x},{y},{z} art {w}x{h}");
                    }
                }
            }
        }

        [Fact]
        public void Candidate_hash_is_order_independent_and_tells_objects_apart()
        {
            uint a = ViewMargin.Mix(10, 20, 5, Torch, false), b = ViewMargin.Mix(11, 20, 5, Torch, false);
            Assert.Equal(a + b, b + a);
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, ViewMargin.Mix(10, 20, 5, Torch, true));
        }
    }
}
