using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// view_margin_walk (vm-walk): the switch in both states, the margin tiles the walk keeps (the view's own diamond, the
    /// chunk's reach cells, the dynamic index's cells), which art marks a tile as reaching, the stretched land rise, and the
    /// per-object margin test (anchors inside the view are never left out; mobiles and other dynamics never).
    /// </summary>
    public sealed class ViewMarginWalkTests
    {
        private const float L = 0, T = 0, R = 800, B = 600;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            ViewMargin.WalkOverrideForTests = on;

            try
            {
                Assert.Equal(on, ViewMargin.WalkEnabled);
            }
            finally
            {
                ViewMargin.WalkOverrideForTests = null;
            }
        }

        [Fact]
        public void Ring_tiles_keep_the_view_reach_and_dynamic_cells_only()
        {
            const ulong inWalk = 0b1111_1111;
            const ulong view = 0b0000_0011;
            const ulong reach = 0b0001_0000;
            const ulong dyn = 0b1000_0000_0000;

            Assert.Equal(0b1001_0011UL, ViewMargin.RingTiles(inWalk, view, reach, 0b1000_0000));
            Assert.Equal(0b0001_0011UL, ViewMargin.RingTiles(inWalk, view, reach, dyn));
            // never more than the walk's own mask
            Assert.Equal(0UL, ViewMargin.RingTiles(0, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue));
            Assert.Equal(inWalk, ViewMargin.RingTiles(inWalk, ulong.MaxValue, 0, 0));
        }

        [Theory]
        [InlineData(44, 44, false)]
        [InlineData(44, 88, false)] // rises 44 px: the walk's tile of slack covers it
        [InlineData(44, 89, true)]
        [InlineData(45, 44, true)] // one px wider than a tile reaches beside it
        [InlineData(40, 80, false)]
        [InlineData(120, 200, true)]
        public void Art_reaches_past_tile(int w, int h, bool expected)
        {
            Assert.Equal(expected, ViewMargin.ArtReachesPastTile(w, h));
        }

        [Fact]
        public void Land_rise_matches_the_land_box()
        {
            // flat
            Assert.Equal(0, ViewMargin.LandUp(10, 40, 40, 40, 40));
            // neighbours 20 Z higher: right/left corners 80 px up (less the 22 px half tile), bottom 80 less 44
            int up = ViewMargin.LandUp(0, 0, 80, 80, 80);
            Assert.Equal(58, up);
            ViewMargin.LandBox(100, 200, 0, true, 0, 80, 80, 80, out _, out int y0, out _, out _);
            Assert.Equal(200 - y0, up);
            Assert.True(ViewMargin.LandReachesPastTile(up));
            Assert.False(ViewMargin.LandReachesPastTile(44));
        }

        [Fact]
        public void Flat_land_in_the_margin_is_left_out_stretched_land_only_off_the_view()
        {
            Assert.True(ViewMargin.RingSkips(ViewMargin.RingKind.Land, 400, 700, 0, false, 0, 0, 0, 0, 0, 0, 0, L, T, R, B));
            // stretched, rising 58 px from 650: top at 592, inside the view
            Assert.False(ViewMargin.RingSkips(ViewMargin.RingKind.Land, 400, 650, 0, true, 0, 80, 80, 80, 0, 0, 0, L, T, R, B));
            // rising 58 px from 700: top at 642, below the view
            Assert.True(ViewMargin.RingSkips(ViewMargin.RingKind.Land, 400, 700, 0, true, 0, 80, 80, 80, 0, 0, 0, L, T, R, B));
        }

        [Theory]
        // below the view: a 44 x 120 static at 700 reaches 624 (off), at 650 reaches 574 (on)
        [InlineData(400, 700, 44, 120, 0, true)]
        [InlineData(400, 650, 44, 120, 0, false)]
        // with the moving slack (items, multi pieces) the first one is kept
        [InlineData(400, 700, 44, 120, ViewMargin.MovingSlack, false)]
        // left of the view: a 200 px wide tree at -150 reaches -150 + 22 + 100 = -28 (off), at -100 reaches 22 (on)
        [InlineData(-150, 300, 200, 100, 0, true)]
        [InlineData(-100, 300, 200, 100, 0, false)]
        public void Art_in_the_margin_kept_only_when_its_box_meets_the_view(int ax, int ay, int w, int h, int slack, bool skips)
        {
            Assert.Equal(skips, ViewMargin.RingSkips(ViewMargin.RingKind.Art, ax, ay, 0, false, 0, 0, 0, 0, w, h, slack, L, T, R, B));
        }

        [Theory]
        [InlineData(0xFF, true)]
        [InlineData(0, false)]
        [InlineData(178, false)]
        [InlineData(0xFE, false)]
        public void Item_is_tested_by_its_art_only_once_its_fade_settled(int alpha, bool art)
        {
            Assert.Equal(art ? ViewMargin.RingKind.Art : ViewMargin.RingKind.Keep, ViewMargin.ItemRingKind((byte)alpha));
        }

        [Fact]
        public void Fading_item_far_off_the_view_is_kept_settled_one_left_out()
        {
            // a margin item whose art box misses the view: left out once settled, walked while it fades in (vmw-cost)
            Assert.False(ViewMargin.RingSkips(ViewMargin.ItemRingKind(0), 1200, 300, 0, false, 0, 0, 0, 0, 44, 44,
                ViewMargin.MovingSlack, L, T, R, B));
            Assert.True(ViewMargin.RingSkips(ViewMargin.ItemRingKind(0xFF), 1200, 300, 0, false, 0, 0, 0, 0, 44, 44,
                ViewMargin.MovingSlack, L, T, R, B));
        }

        [Fact]
        public void Dynamics_are_never_left_out()
        {
            Assert.False(ViewMargin.RingSkips(ViewMargin.RingKind.Keep, -5000, 5000, 0, false, 0, 0, 0, 0, 0, 0, 0, L, T, R, B));
        }

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(800, 600, true)]
        [InlineData(-1, 300, false)]
        [InlineData(400, 601, false)]
        public void In_view_is_inclusive(int ax, int ay, bool expected)
        {
            Assert.Equal(expected, ViewMargin.InView(ax, ay, L, T, R, B));
        }
    }
}
