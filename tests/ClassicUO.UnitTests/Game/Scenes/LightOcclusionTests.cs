using System;
using ClassicUO.Assets;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// light_occlusion (light-edges): which objects hide a light's halo (drawn after the source, not its own emitter, not a
    /// flat walkable surface, translucent or internal tile, and covering the source's light point), the switch in both states,
    /// and the chunk search the light pass runs for occluders: every sprite whose art overlaps a rectangle lies in a chunk and
    /// tile it walks, whatever the art size or Z (round 5). The owner's spots with the real layout, art and tiledata: LightOcclusionSceneTests.
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
        public void Wall_faces_away_from_a_wall_source_behind_its_plane_but_not_along_the_same_line()
        {
            const byte E = LightOcclusion.FacingEast, S = LightOcclusion.FacingSouth, N = LightOcclusion.FacingNone;

            // an east-facing window (line along Y): its own line (dx 0) is lit, the parallel line east of it and the
            // south-facing line south of it (the inside corner) face away
            Assert.False(LightOcclusion.FacesAway(E, E, 0, 1));
            Assert.True(LightOcclusion.FacesAway(E, E, 2, -1));
            Assert.True(LightOcclusion.FacesAway(E, S, 1, 1));
            Assert.True(LightOcclusion.FacesAway(E, S, 1, 0));
            Assert.False(LightOcclusion.FacesAway(E, S, 2, -1));
            // a south-facing window (line along X): mirrored
            Assert.False(LightOcclusion.FacesAway(S, S, 1, 0));
            Assert.True(LightOcclusion.FacesAway(S, S, -1, 2));
            Assert.True(LightOcclusion.FacesAway(S, E, 1, 1));
            Assert.True(LightOcclusion.FacesAway(S, E, 0, 1));
            Assert.False(LightOcclusion.FacesAway(S, E, -1, 2));
            // not a wall source, or a piece of unknown facing: never
            Assert.False(LightOcclusion.FacesAway(N, S, 1, 1));
            Assert.False(LightOcclusion.FacesAway(E, N, 1, 1));
            // the facing from the art's visible bounds (44 px art): right half east, left half south, else none
            Assert.Equal(E, LightOcclusion.Facing(44, 12, 32));
            Assert.Equal(S, LightOcclusion.Facing(44, 0, 32));
            Assert.Equal(N, LightOcclusion.Facing(44, 0, 44));
            Assert.Equal(N, LightOcclusion.Facing(66, 0, 30));
            // Hides: a facing-away wall hides without covering the source, only with the switch on
            StaticTiles wall = Wall();
            Assert.True(LightOcclusion.Hides(true, false, 1, 1, 0, ref wall, false, true));
            Assert.False(LightOcclusion.Hides(false, false, 1, 1, 0, ref wall, false, true));
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
            // on the source's own tile, higher up: at its depth, it stands with the emitter
            Assert.False(LightOcclusion.Hides(on, false, 0, 0, 5, ref wall, true));
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

        // a static's art rectangle (View.StaticArt) at tile (x, y, z) with the draw offset (offX, offY)
        private static (int X, int Y) ArtTopLeft(int x, int y, int z, int w, int h, int offX, int offY)
        {
            ViewMargin.Anchor(x, y, z, offX, offY, out int ax, out int ay);

            return (ax - ((w >> 1) - 22), ay - (h - 44));
        }

        [Theory]
        [InlineData(350, 350, 0)]
        [InlineData(120, 90, 37)]
        [InlineData(1, 1, -11)]
        public void Chunk_search_holds_every_overlapping_sprite_whatever_its_size_or_elevation(int rectW, int rectH, int offset)
        {
            // round 5 (review P2 #5): the source at tile (800, 800, 0), a rectangle (a light point or a wall source's quad)
            // around it; any sprite of any size (up to 44 x 421, 0x0D42, and 600 px wide) at any Z whose art overlaps it is in a
            // chunk ChunkReaches accepts and a tile TileReachesX accepts, with the chunk's record made from that sprite alone
            int offX = 1000 + offset, offY = 2000 - offset;
            ViewMargin.Anchor(800, 800, 0, offX, offY, out int sax, out int say);
            int left = sax + 22 - rectW / 2, top = say + 22 - rectH / 2, right = left + Math.Max(rectW, 1), bottom = top + Math.Max(rectH, 1);
            (int W, int H)[] arts = { (44, 44), (44, 421), (600, 120), (100, 260) };
            int found = 0;

            for (int dx = -40; dx <= 40; dx++)
            {
                for (int dy = -40; dy <= 40; dy++)
                {
                    for (int z = -128; z <= 127; z += 17)
                    {
                        int x = 800 + dx, y = 800 + dy;

                        foreach (var (w, h) in arts)
                        {
                            var (ox, oy) = ArtTopLeft(x, y, z, w, h, offX, offY);

                            if (!LightOcclusion.RectOverlaps(ox, oy, w, h, left, top, right, bottom))
                            {
                                continue;
                            }

                            found++;
                            int overL = (w >> 1) - 22, overR = w - (w >> 1) - 22, overUp = h - 44;
                            Assert.True(LightOcclusion.ChunkReaches(x >> 3, y >> 3, overL, overR, overUp, z, z, offX, offY, left, top, right, bottom),
                                $"{w}x{h} at ({dx},{dy},{z}) overlaps but its chunk is skipped");
                            Assert.True(LightOcclusion.TileReachesX((x - y) * 22 - offX - 22, overL, overR, left, right),
                                $"{w}x{h} at ({dx},{dy},{z}) overlaps but its tile is skipped");
                        }
                    }
                }
            }

            Assert.True(found > 0);
        }

        [Fact]
        public void Review_counterexample_elevated_wall_is_searched()
        {
            // review P2 #5: a 22 x 100 lamp at (0, 0, 0), its light point (22, -6) from its anchor; an opaque 44 x 112 wall at
            // (10, 10, 100) covers it, outside the round 4 window (S = 20 > 17)
            int offX = 0, offY = 0;
            ViewMargin.Anchor(400, 400, 0, offX, offY, out int sax, out int say);
            int px = sax + 22, py = say - 6;
            var (wx, wy) = ArtTopLeft(410, 410, 100, 44, 112, offX, offY);
            Assert.True(LightOcclusion.RectOverlaps(wx, wy, 44, 112, px, py, px + 1, py + 1));
            Assert.True(LightOcclusion.ChunkReaches(410 >> 3, 410 >> 3, 0, 0, 112 - 44, 100, 100, offX, offY, px, py, px + 1, py + 1));
        }

        [Fact]
        public void Chunk_far_away_or_empty_is_skipped()
        {
            ViewMargin.Anchor(800, 800, 0, 0, 0, out int sax, out int say);
            int px = sax + 22, py = say + 22;
            // empty record (no statics, items or multis)
            Assert.False(LightOcclusion.ChunkReaches(100, 100, 0, 0, 0, int.MaxValue, int.MinValue, 0, 0, px, py, px + 1, py + 1));
            // 10 chunks to the side, ordinary art
            Assert.False(LightOcclusion.ChunkReaches(110, 90, 10, 10, 60, 0, 0, 0, 0, px, py, px + 1, py + 1));
            // the light's own chunk
            Assert.True(LightOcclusion.ChunkReaches(100, 100, 10, 10, 60, 0, 0, 0, 0, px, py, px + 1, py + 1));
        }

        [Theory]
        [InlineData(255, true)]
        [InlineData(192, true)] // a custom house component drawn transparent (MultiView)
        [InlineData(128, true)]
        [InlineData(127, false)]
        [InlineData(76, false)]  // foliage the player stands behind
        [InlineData(0, false)]   // faded out (above the max Z, hidden roof)
        public void Partly_transparent_object_hides_only_from_half_opaque(byte alpha, bool hides)
        {
            Assert.Equal(hides, alpha >= LightOcclusion.MinOccluderAlpha);
        }
    }
}
