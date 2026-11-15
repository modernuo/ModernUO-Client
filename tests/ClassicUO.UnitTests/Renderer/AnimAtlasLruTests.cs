using System;
using System.Runtime.CompilerServices;
using ClassicUO.Game;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Animations;
using Microsoft.Xna.Framework.Graphics;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    [CollectionDefinition(nameof(AnimAtlasLruCollection), DisableParallelization = true)]
    public class AnimAtlasLruCollection
    {
    }

    // anim_atlas_lru: the Legacy anim atlas page budget and eviction order (pure policy, no GPU) and the switch.
    [Collection(nameof(AnimAtlasLruCollection))]
    public class AnimAtlasLruTests : IDisposable
    {
        private const long Page = 64L << 20;

        public void Dispose() => AnimAtlasLruSetup.OnOverride = null;

        private static AnimAtlasLruPolicy Policy(int budget = 4, int floor = 2, int ceiling = 16) =>
            new AnimAtlasLruPolicy(budget, floor, ceiling) { MinAgeMs = 45_000, ThrashWindowMs = 60_000, GrowCooldownMs = 5_000 };

        private static int[] Select(AnimAtlasLruPolicy p, long now, params long[] lastUse)
        {
            var victims = new int[lastUse.Length];
            int n = p.SelectVictims(lastUse, lastUse.Length - 1, now, victims);

            return victims.AsSpan(0, n).ToArray();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Switch_creates_the_policy_only_when_on(bool on)
        {
            AnimAtlasLruSetup.OnOverride = on;
            AnimAtlasLruSetup.On.Should().Be(on);
            var policy = AnimAtlasLruSetup.Create(AnimAtlasLruSetup.On, Page);

            if (!on)
            {
                policy.Should().BeNull();

                return;
            }

            policy.Should().NotBeNull();
            // defaults (no CUO_ANIM_LRU_* in the test environment): 256 / 128 / 1024 MiB of 64 MiB pages
            policy.BudgetPages.Should().Be(4);
            policy.FloorPages.Should().Be(2);
            policy.CeilingPages.Should().Be(16);
            policy.MinAgeMs.Should().Be(45_000);
        }

        [Fact]
        public void Pages_round_down_with_at_least_one()
        {
            AnimAtlasLruSetup.Pages(128, Page).Should().Be(2);
            AnimAtlasLruSetup.Pages(100, Page).Should().Be(1);
            AnimAtlasLruSetup.Pages(32, 16L << 20).Should().Be(2);
            AnimAtlasLruSetup.Pages(1, Page).Should().Be(1);
        }

        [Fact]
        public void Within_budget_nothing_is_evicted()
        {
            Select(Policy(budget: 4), 1_000_000, 0, 0, 0, 0).Should().BeEmpty();
        }

        [Fact]
        public void Above_budget_evicts_the_oldest_down_to_low_water()
        {
            var p = Policy(budget: 4);
            p.LowWater.Should().Be(3);
            long now = 1_000_000;

            // 6 pages, the last one open: down to 3 = three oldest of the old enough ones
            Select(p, now, 500, 100, 300, 200, 400, now).Should().Equal(1, 3, 2);
        }

        [Fact]
        public void Never_evicts_the_open_page_or_a_recently_used_one()
        {
            var p = Policy(budget: 1);
            long now = 100_000;

            // page 0 used 10 s ago (min age 45 s), page 2 is open (and oldest): only page 1 may go
            Select(p, now, now - 10_000, now - 50_000, 0).Should().Equal(1);
            // nothing old enough: no eviction although over budget
            Select(p, now, now - 1_000, now - 2_000, 0).Should().BeEmpty();
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(4, 3)]
        [InlineData(8, 6)]
        [InlineData(16, 12)]
        public void Low_water_is_a_quarter_below_budget(int budget, int low)
        {
            Policy(budget: budget, floor: 1).LowWater.Should().Be(low);
        }

        [Fact]
        public void Thrash_reload_grows_one_page_with_cooldown_and_ceiling()
        {
            var p = Policy(budget: 2, floor: 2, ceiling: 3);
            p.Tick(0, false);

            p.OnReload(evictedAtMs: 10_000, nowMs: 20_000).Should().BeTrue();
            p.BudgetPages.Should().Be(3);
            p.ThrashReloads.Should().Be(1);

            // at the ceiling: counted, no growth
            p.OnReload(30_000, 40_000).Should().BeFalse();
            p.BudgetPages.Should().Be(3);
            p.ThrashReloads.Should().Be(2);
            p.Grows.Should().Be(1);

            var q = Policy(budget: 2, floor: 2, ceiling: 8);
            q.Tick(0, false);
            q.OnReload(10_000, 20_000).Should().BeTrue();
            // within the 5 s cooldown
            q.OnReload(19_000, 22_000).Should().BeFalse();
            q.OnReload(19_000, 25_000).Should().BeTrue();
            q.BudgetPages.Should().Be(4);
        }

        [Fact]
        public void A_late_reload_is_not_thrash()
        {
            var p = Policy(budget: 2);
            p.OnReload(evictedAtMs: 0, nowMs: 60_000).Should().BeFalse();
            p.Reloads.Should().Be(1);
            p.ThrashReloads.Should().Be(0);
            p.BudgetPages.Should().Be(2);
        }

        [Fact]
        public void Growth_needs_memory()
        {
            var p = Policy(budget: 2);
            p.MemoryAllowsGrowth = () => false;
            p.OnReload(0, 1_000).Should().BeFalse();
            p.ThrashReloads.Should().Be(1);
            p.BudgetPages.Should().Be(2);
        }

        [Fact]
        public void Shrinks_after_quiet_minutes_down_to_the_floor_sooner_when_unfocused()
        {
            var p = Policy(budget: 4, floor: 2);
            p.ShrinkQuietMs = 180_000;
            p.ShrinkQuietUnfocusedMs = 45_000;
            p.Tick(0, false);

            p.Tick(179_000, false).Should().BeFalse();
            p.Tick(180_000, false).Should().BeTrue();
            p.BudgetPages.Should().Be(3);

            // unfocused: 45 s after the last resize
            p.Tick(224_000, true).Should().BeFalse();
            p.Tick(225_000, true).Should().BeTrue();
            p.BudgetPages.Should().Be(2);

            // floor
            p.Tick(10_000_000, true).Should().BeFalse();
            p.BudgetPages.Should().Be(2);
            p.Shrinks.Should().Be(2);
        }

        [Fact]
        public void Thrash_postpones_shrinking()
        {
            var p = Policy(budget: 4, floor: 2, ceiling: 4);
            p.ShrinkQuietMs = 180_000;
            p.Tick(0, false);
            p.OnReload(100_000, 150_000); // thrash at 150 s (at the ceiling: no growth)
            p.Tick(200_000, false).Should().BeFalse();
            p.Tick(330_000, false).Should().BeTrue();
        }

        [Fact]
        public void Floor_and_ceiling_bracket_the_budget()
        {
            var p = new AnimAtlasLruPolicy(budgetPages: 2, floorPages: 4, ceilingPages: 1);
            p.FloorPages.Should().Be(2);
            p.CeilingPages.Should().Be(2);
            p.BudgetPages.Should().Be(2);
        }

        [Fact]
        public void Touch_ignores_untracked_textures()
        {
            AtlasPageClock.Touch(null);
        }

        // a page texture without a device: only its Tag is used (no finalizer, it was never initialised)
        private static Texture2D FakePage(AtlasPage page)
        {
            var texture = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            GC.SuppressFinalize(texture);
            texture.Tag = page;

            return texture;
        }

        [Fact]
        public void A_direction_over_three_pages_touches_every_page()
        {
            // nine 512x512 frames on 1024^2 pages: four, four and one frame on three pages, plus an empty frame
            var pages = new[] { new AtlasPage(0, 0), new AtlasPage(1, 0), new AtlasPage(2, 0) };
            var textures = Array.ConvertAll(pages, FakePage);
            var frames = new SpriteInfo[10];

            for (int i = 0; i < 9; i++)
            {
                frames[i].Texture = textures[i / 4];
            }

            long saved = AtlasPageClock.NowMs;

            try
            {
                AtlasPageClock.NowMs = 123_456;
                AtlasPageClock.TouchFrames(frames);
            }
            finally
            {
                AtlasPageClock.NowMs = saved;
            }

            pages.Should().OnlyContain(p => p.LastUseMs == 123_456);
        }

        [Fact]
        public void The_clock_is_64_bit_and_policy_times_past_the_uint_wrap_work()
        {
            AtlasPageClock.MonotonicMs().Should().BeGreaterThan(0);

            // a session past 49.7 days: the uint Time.Ticks would have wrapped to a small value here
            long wrap = (long)uint.MaxValue + 1;
            var p = Policy(budget: 1);
            p.ShrinkQuietMs = 180_000;
            Select(p, wrap + 10_000, wrap - 60_000, wrap + 9_000, 0).Should().Equal(0);

            var q = Policy(budget: 3, floor: 2);
            q.ShrinkQuietMs = 180_000;
            q.Tick(wrap - 100_000, false);
            q.Tick(wrap + 80_000, false).Should().BeTrue();
            q.OnReload(wrap + 70_000, wrap + 90_000).Should().BeTrue(); // thrash after the boundary, cooldown since the shrink passed
            q.BudgetPages.Should().Be(3);
        }
    }
}
