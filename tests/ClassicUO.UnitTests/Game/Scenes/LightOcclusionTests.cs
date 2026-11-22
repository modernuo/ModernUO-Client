using ClassicUO.Assets;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// light_occlusion (light-edges): which objects hide a light's halo (drawn after the source, not its own emitter, not a
    /// flat walkable surface, translucent or internal tile, and covering the source's light point), the switch in both states,
    /// and the tile window the light pass walks for occluders: every occluder whose art overlaps a rectangle (within the art
    /// and Z the window assumes) lies in it. The owner's spots with the real layout, art and tiledata: LightOcclusionSceneTests.
    /// </summary>
    public sealed class LightOcclusionTests
    {
        private static StaticTiles Wall() => new StaticTiles { Flags = TileFlag.Wall, Height = 20 };

        private static StaticTiles Floor() => new StaticTiles { Flags = TileFlag.Surface, Height = 0 };

        private static StaticTiles Roof() => new StaticTiles { Flags = TileFlag.Roof | TileFlag.Surface, Height = 3 };

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            LightOcclusion.OverrideForTests = on;

            try
            {
                Assert.Equal(on, LightOcclusion.Enabled);
            }
            finally
            {
                LightOcclusion.OverrideForTests = null;
            }
        }

        private static StaticTiles LitWindow() => new StaticTiles { Flags = TileFlag.Wall | TileFlag.Window | TileFlag.LightSource, Height = 20, Layer = 27 };

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Bank_lit_window_behind_another_window_is_hidden_by_the_nearer_window(bool on)
        {
            // Moonglow bank (owner 2026-11-22): a lit window's glow showed through the lit window drawn in front of it. Its
            // light flag does not let the glow through: only the light's own emitter does
            StaticTiles nearer = LitWindow();

            Assert.Equal(on, LightOcclusion.Hides(on, false, 1, 0, 0, ref nearer, true));
            Assert.Equal(on, LightOcclusion.Hides(on, false, 0, 1, 0, ref nearer, true));
            Assert.Equal(on, LightOcclusion.Hides(on, false, 2, 3, 0, ref nearer, true));
            // the emitter itself
            Assert.False(LightOcclusion.Hides(on, true, 0, 0, 0, ref nearer, true));
            Assert.False(LightOcclusion.Hides(on, true, 1, 0, 0, ref nearer, true));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Objects_drawn_after_the_source_that_do_not_cover_its_light_point_never_hide(bool on)
        {
            // round 4 (owner 2026-11-22): the counter under a candle, the next piece of a window's wall line, the fence before a
            // street lamp are drawn after the source but do not stand between the viewer and it
            StaticTiles wall = Wall(), window = LitWindow(), counter = new StaticTiles { Flags = TileFlag.Surface, Height = 6 };

            Assert.False(LightOcclusion.Hides(on, false, 1, 0, 0, ref window, false));
            Assert.False(LightOcclusion.Hides(on, false, 0, 1, 0, ref wall, false));
            Assert.False(LightOcclusion.Hides(on, false, 0, 0, 20, ref window, false));
            Assert.False(LightOcclusion.Hides(on, false, 1, 1, -4, ref counter, false));
            Assert.Equal(on, LightOcclusion.Hides(on, false, 1, 1, -4, ref counter, true));
        }

        [Fact]
        public void Light_point_is_the_centre_of_the_emitters_visible_art_and_cover_point_maps_into_the_art()
        {
            // a 44x112 wall piece anchored at (100, 200): its art starts at (100, 132); visible bounds (12, 0, 32, 112)
            LightOcclusion.LightPoint(100, 200, 44, 112, 12, 0, 32, 112, out int px, out int py);
            Assert.Equal((128, 188), (px, py));
            Assert.True(LightOcclusion.CoverPoint(100, 200, 44, 112, px, py, out int lx, out int ly));
            Assert.Equal((28, 56), (lx, ly));
            // the next piece of a line along y, 22 px left and down: its art ends at x 122
            Assert.False(LightOcclusion.CoverPoint(78, 222, 44, 112, px, py, out _, out _));
            // a 22 px wide lamp post art is centred on the tile
            LightOcclusion.LightPoint(100, 200, 22, 100, 0, 0, 22, 100, out px, out py);
            Assert.Equal((122, 194), (px, py));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Near_wall_in_front_of_a_far_window_hides_its_glow_only_with_the_switch_on(bool on)
        {
            StaticTiles wall = Wall();

            // the near wall 6 tiles south of the window, straight below it on screen and to either side; the same wall line
            Assert.Equal(on, LightOcclusion.Hides(on, false, 6, 6, 0, ref wall, true));
            Assert.Equal(on, LightOcclusion.Hides(on, false, -1, 7, 0, ref wall, true));
            Assert.Equal(on, LightOcclusion.Hides(on, false, 9, 5, -20, ref wall, true));
            Assert.Equal(on, LightOcclusion.Hides(on, false, 3, 0, 0, ref wall, true));
            // on the source's tile, higher up
            Assert.Equal(on, LightOcclusion.Hides(on, false, 0, 0, 5, ref wall, true));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Glow_stays_on_the_floor_and_what_is_drawn_before_the_source(bool on)
        {
            StaticTiles wall = Wall(), floor = Floor();

            // drawn before the source: behind it, the same diagonal, its tile lower down
            Assert.False(LightOcclusion.Hides(on, false, -3, 0, 0, ref wall, true));
            Assert.False(LightOcclusion.Hides(on, false, -2, -2, 0, ref wall, true));
            Assert.False(LightOcclusion.Hides(on, false, 2, -2, 0, ref wall, true));
            Assert.False(LightOcclusion.Hides(on, false, 0, 0, -5, ref wall, true));
            // the floor in front of the source is lit
            Assert.False(LightOcclusion.Hides(on, false, 4, 4, 0, ref floor, true));
        }

        [Fact]
        public void Roofs_and_items_occlude_translucent_internal_and_floor_tiles_do_not()
        {
            StaticTiles roof = Roof();
            StaticTiles table = new StaticTiles { Flags = TileFlag.Surface, Height = 6 };
            StaticTiles water = new StaticTiles { Flags = TileFlag.Wet | TileFlag.Translucent | TileFlag.Surface, Height = 0 };
            StaticTiles glass = new StaticTiles { Flags = TileFlag.Wall | TileFlag.Translucent, Height = 20 };
            StaticTiles internalTile = new StaticTiles { Flags = TileFlag.Wall | TileFlag.Internal, Height = 20 };
            StaticTiles floor = Floor();

            Assert.True(LightOcclusion.OccluderData(ref roof));
            Assert.True(LightOcclusion.OccluderData(ref table));
            Assert.False(LightOcclusion.OccluderData(ref water));
            Assert.False(LightOcclusion.OccluderData(ref glass));
            Assert.False(LightOcclusion.OccluderData(ref internalTile));
            Assert.False(LightOcclusion.OccluderData(ref floor));
        }

        [Theory]
        [InlineData(350, 350, 0)]
        [InlineData(120, 90, 0)]
        [InlineData(350, 350, 37)]
        [InlineData(64, 64, -11)]
        public void Window_holds_every_overlapping_occluder(int lightW, int lightH, int offset)
        {
            // the source anchored at (ax, ay) (any offset), its light quad centred on anchor + 22 as AddLight places it
            int ax = 500 + offset, ay = 300 - offset;
            int left = ax + 22 - lightW / 2, top = ay + 22 - lightH / 2, right = left + lightW, bottom = top + lightH;
            LightOcclusion.Window(ax, ay, left, top, right, bottom, out int sLo, out int sHi, out int dLo, out int dHi);
            int[] widths = { 44, 100, 2 * LightOcclusion.ArtSide + 44 };
            int[] heights = { 44, 120, LightOcclusion.ArtUp };

            for (int dx = -60; dx <= 60; dx++)
            {
                for (int dy = -60; dy <= 60; dy++)
                {
                    if (dx + dy < 1)
                    {
                        continue;
                    }

                    for (int dz = -LightOcclusion.ZBelow; dz <= LightOcclusion.ZAbove; dz += 5)
                    {
                        int oax = ax + (dx - dy) * 22, oay = ay + (dx + dy) * 22 - 4 * dz;

                        foreach (int w in widths)
                        {
                            foreach (int h in heights)
                            {
                                if (!LightOcclusion.ArtOverlaps(oax, oay, w, h, left, top, right, bottom))
                                {
                                    continue;
                                }

                                int s = dx + dy, d = dx - dy;
                                Assert.True(s >= sLo && s <= sHi && d >= dLo && d <= dHi,
                                    $"occluder at ({dx},{dy},{dz}) art {w}x{h} overlaps the light but is outside S {sLo}..{sHi} D {dLo}..{dHi}");
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void Window_stays_small()
        {
            // the largest classic light (350 px): the window must stay a few hundred tiles per light, not the whole view
            int ax = 500, ay = 300;
            LightOcclusion.Window(ax, ay, ax + 22 - 175, ay + 22 - 175, ax + 22 + 175, ay + 22 + 175, out int sLo, out int sHi, out int dLo, out int dHi);
            int tiles = 0;

            for (int s = sLo; s <= sHi; s++)
            {
                for (int d = dLo; d <= dHi; d++)
                {
                    if (((s + d) & 1) == 0)
                    {
                        tiles++;
                    }
                }
            }

            Assert.InRange(tiles, 1, 600);
        }
    }
}
