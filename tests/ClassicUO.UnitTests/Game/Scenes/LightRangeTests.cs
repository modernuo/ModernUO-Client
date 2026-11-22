using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>light_range (light-edges round 6c): a range limit from the player with a fade over its last tiles, and a count
    /// budget on the ranked light list whose lowest ranks fade.</summary>
    public sealed class LightRangeTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            LightRange.OverrideForTests = on;

            try
            {
                LightRange.Enabled.Should().Be(on);
            }
            finally
            {
                LightRange.OverrideForTests = null;
            }
        }

        [Fact]
        public void Tile_distance_is_the_chebyshev_distance_for_tile_offsets()
        {
            for (int dx = -30; dx <= 30; dx += 3)
            {
                for (int dy = -30; dy <= 30; dy += 4)
                {
                    // the isometric projection: one tile east is (+22, +22), one south (-22, +22)
                    int sx = (dx - dy) * 22, sy = (dx + dy) * 22;
                    LightRange.TileDistance(500 + sx, 300 + sy, 500, 300).Should().Be(Math.Max(Math.Abs(dx), Math.Abs(dy)));
                }
            }
        }

        [Fact]
        public void Auto_range_is_just_past_the_zoom_1_view_and_the_largest_halo()
        {
            // 1920 x 1080: the corner is (960 + 540) / 44 = 34.1 tiles out, the 175 px halo 4 tiles, then the fade
            LightRange.AutoRange(1920, 1080, 175).Should().Be(35 + 4 + LightRange.FadeTiles);
            LightRange.Range(0, 41).Should().Be(41);
            LightRange.Range(24, 41).Should().Be(24);
        }

        [Fact]
        public void Range_share_fades_over_the_last_two_tiles()
        {
            LightRange.RangeShare(10f, 20).Should().Be(1f);
            LightRange.RangeShare(18f, 20).Should().Be(1f);
            LightRange.RangeShare(19f, 20).Should().BeApproximately(0.5f, 1e-6f);
            LightRange.RangeShare(20f, 20).Should().Be(0f);
            LightRange.InRange(19.99f, 20).Should().BeTrue();
            LightRange.InRange(20f, 20).Should().BeFalse();
        }

        [Fact]
        public void Budget_setting_defaults_and_is_bounded()
        {
            LightRange.Budget(0).Should().Be(LightRange.DefaultBudget);
            LightRange.Budget(-5).Should().Be(LightRange.DefaultBudget);
            LightRange.Budget(40).Should().Be(40);
            LightRange.Budget(1_000_000).Should().Be(FrameLights.SafetyBound);
        }

        [Fact]
        public void Budget_share_rises_by_rank_from_the_lowest()
        {
            LightRange.BudgetShare(0, 16).Should().BeApproximately(1f / 16, 1e-6f);
            LightRange.BudgetShare(7, 16).Should().BeApproximately(0.5f, 1e-6f);
            LightRange.BudgetShare(15, 16).Should().Be(1f);
            LightRange.BudgetShare(16, 16).Should().Be(1f);
        }

        // the light list of one frame as GameScene.AddLightCore fills it with chunk_lights on: lamps in a row east of the player
        // (tile i at distance i), ranked by FrameLights.Priority; with light_range on past the range no slot and a range share,
        // the budget as the list's limit, then FadeLowest when the budget binds
        private static FrameLights Frame(bool on, int lamps, int range, int budget)
        {
            var lights = new FrameLights(100);
            lights.BeginFrame(1, on ? budget : FrameLights.SafetyBound, true);
            const int px = 1000, py = 1000;

            for (int i = 0; i < lamps; i++)
            {
                int x = px + i * 22, y = py + i * 22;
                ref FrameLights.Entry e = ref lights.Next();
                e.ID = 2;
                e.DrawX = x;
                e.DrawY = y;
                e.Share = 1f;
                float d = LightRange.TileDistance(x, y, px, py);

                if (on)
                {
                    if (!LightRange.InRange(d, range))
                    {
                        continue;
                    }

                    e.Share = LightRange.RangeShare(d, range);
                }

                lights.Commit(FrameLights.Priority(x, y, 0, 0, 4000, 4000, px, py));
            }

            if (on && lights.Refused > 0)
            {
                lights.FadeLowest(LightRange.FadeRanks);
            }

            return lights;
        }

        private static List<(int Tile, float Share)> Kept(FrameLights lights) =>
            Enumerable.Range(0, lights.CountFor(1)).Select(i => ((lights[i].DrawX - 1000) / 22, lights[i].Share)).OrderBy(t => t.Item1).ToList();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Range_drops_and_fades_lights_only_with_the_switch_on(bool on)
        {
            List<(int Tile, float Share)> kept = Kept(Frame(on, 60, 30, 4096));

            if (!on)
            {
                kept.Should().HaveCount(60);
                kept.Should().OnlyContain(k => k.Share == 1f);

                return;
            }

            kept.Should().HaveCount(30, "tiles 0..29 are inside a range of 30");
            kept.Where(k => k.Tile <= 28).Should().OnlyContain(k => k.Share == 1f);
            kept.Single(k => k.Tile == 29).Share.Should().BeApproximately(0.5f, 1e-6f);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Budget_keeps_the_nearest_and_fades_its_lowest_ranks_only_with_the_switch_on(bool on)
        {
            FrameLights lights = Frame(on, 60, 200, 40);
            List<(int Tile, float Share)> kept = Kept(lights);

            if (!on)
            {
                kept.Should().HaveCount(60);
                lights.Refused.Should().Be(0);
                kept.Should().OnlyContain(k => k.Share == 1f);

                return;
            }

            kept.Select(k => k.Tile).Should().Equal(Enumerable.Range(0, 40), "the budget keeps the best ranks: nearest the player");
            lights.Refused.Should().Be(20);

            for (int r = 0; r < LightRange.FadeRanks; r++)
            {
                // tile 39 is the lowest rank (r = 0)
                kept[39 - r].Share.Should().BeApproximately((r + 1) / (float)LightRange.FadeRanks, 1e-6f);
            }

            kept.Take(40 - LightRange.FadeRanks).Should().OnlyContain(k => k.Share == 1f);

            long worstKept = Enumerable.Range(0, lights.CountFor(1)).Max(i => lights.PriorityAt(i));
            lights.MinRefusedPriority.Should().BeGreaterThan(worstKept);
        }

        [Fact]
        public void Fade_lowest_orders_ties_by_index_and_is_a_no_op_unranked()
        {
            var lights = new FrameLights(100);
            lights.BeginFrame(1, 10, true);

            for (int i = 0; i < 4; i++)
            {
                ref FrameLights.Entry e = ref lights.Next();
                e.Share = 1f;
                lights.Commit(5);
            }

            lights.FadeLowest(4);
            // equal priorities: the largest index is the lowest rank
            Enumerable.Range(0, 4).Select(i => lights[i].Share).Should().Equal(1f, 0.75f, 0.5f, 0.25f);

            lights.BeginFrame(2, 10);
            ref FrameLights.Entry u = ref lights.Next();
            u.Share = 1f;
            lights.Commit();
            lights.FadeLowest(4);
            lights[0].Share.Should().Be(1f);
        }
    }
}
