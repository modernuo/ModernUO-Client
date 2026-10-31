using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // Grow on demand for the Gpu world's art pages, anim pages and instance buffer (poc/Uo.Render/GrowthPolicy.cs).
    public class GrowthPolicyTests
    {
        [Fact]
        public void Below_75_percent_never_grows()
        {
            var p = new GrowthPolicy();

            for (long f = 0; f < 1000; f++)
            {
                p.Check(used: 8, capacity: 12, max: 24, hold: true, starved: false, frame: f).Should().BeFalse();
            }
        }

        [Fact]
        public void At_75_percent_grows_at_once_during_a_hold()
        {
            var p = new GrowthPolicy();
            p.Check(9, 12, 24, hold: true, starved: false, frame: 5).Should().BeTrue();
        }

        [Fact]
        public void At_75_percent_without_a_hold_waits_DeferFrames()
        {
            var p = new GrowthPolicy();

            for (long f = 100; f < 100 + GrowthPolicy.DeferFrames; f++)
            {
                p.Check(9, 12, 24, false, false, f).Should().BeFalse($"frame {f} is within the deferral");
            }

            p.Check(9, 12, 24, false, false, 100 + GrowthPolicy.DeferFrames).Should().BeTrue();
        }

        [Fact]
        public void Dropping_below_75_percent_restarts_the_deferral()
        {
            var p = new GrowthPolicy();
            p.Check(9, 12, 24, false, false, 0).Should().BeFalse();
            p.Check(8, 12, 24, false, false, 60).Should().BeFalse();
            p.Check(9, 12, 24, false, false, 61).Should().BeFalse();
            p.Check(9, 12, 24, false, false, 61 + GrowthPolicy.DeferFrames - 1).Should().BeFalse();
            p.Check(9, 12, 24, false, false, 61 + GrowthPolicy.DeferFrames).Should().BeTrue();
        }

        [Fact]
        public void Starved_grows_at_once_whatever_the_occupancy()
        {
            var p = new GrowthPolicy();
            p.Check(12, 12, 24, hold: false, starved: true, frame: 0).Should().BeTrue();
            p.Check(1, 12, 24, hold: false, starved: true, frame: 0).Should().BeTrue();
        }

        [Fact]
        public void At_the_ceiling_never_grows()
        {
            var p = new GrowthPolicy();
            p.Check(24, 24, 24, hold: true, starved: true, frame: 0).Should().BeFalse();
            p.Check(128, 128, 128, true, true, 1000).Should().BeFalse();
        }

        [Fact]
        public void Grew_restarts_the_deferral_for_the_new_size()
        {
            var p = new GrowthPolicy();
            p.Check(9, 12, 24, false, false, 0).Should().BeFalse();
            p.Check(9, 12, 24, false, false, GrowthPolicy.DeferFrames).Should().BeTrue();
            p.Grew();
            // 18 of 24 is 75% again: a fresh deferral, not the old one
            p.Check(18, 24, 48, false, false, GrowthPolicy.DeferFrames + 1).Should().BeFalse();
        }

        [Theory]
        [InlineData(12, 24, 24)]
        [InlineData(32, 128, 64)]
        [InlineData(64, 128, 128)]
        [InlineData(96, 128, 128)]
        [InlineData(256 << 10, 2 << 20, 512 << 10)]
        [InlineData(1 << 20, 2 << 20, 2 << 20)]
        public void Next_doubles_up_to_the_ceiling(int capacity, int max, int expected)
        {
            GrowthPolicy.Next(capacity, max).Should().Be(expected);
        }

        [Fact]
        public void Steps_from_the_start_sizes_to_the_ceilings()
        {
            // art 12 -> 24 (1 step), anim 32 -> 64 -> 128 (2 steps), instances 256k -> 2M (3 steps)
            Steps(12, 24).Should().Be(1);
            Steps(32, 128).Should().Be(2);
            Steps(256 << 10, 2 << 20).Should().Be(3);

            static int Steps(int c, int max)
            {
                int n = 0;

                while (c < max)
                {
                    c = GrowthPolicy.Next(c, max);
                    n++;
                }

                return n;
            }
        }

        // Codex audit P2: a batch that crosses capacity must grow first. Before the fix the check saw 11/12 layers, no
        // hold and an unexpired deferral, declined, and the batch's second upload evicted a resident page.
        [Fact]
        public void A_batch_that_crosses_capacity_grows_before_uploading()
        {
            var p = new GrowthPolicy();
            p.Check(11, 12, 24, hold: false, starved: false, frame: 0).Should().BeFalse("the deferral starts");
            // the old check (starved only when already full) declines this frame
            p.Check(11, 12, 24, hold: false, starved: false, frame: 1).Should().BeFalse();
            // the batch of two loaded pages does not fit in the one free layer: grow now
            p.CheckBatch(11, 12, 24, incoming: 2, hold: false, frame: 1).Should().BeTrue();
            GrowthPolicy.NextFor(12, 24, 11 + 2).Should().Be(24);
        }

        [Fact]
        public void A_batch_that_fits_keeps_the_deferral()
        {
            var p = new GrowthPolicy();
            p.CheckBatch(11, 12, 24, incoming: 1, hold: false, frame: 0).Should().BeFalse("one upload fits in the free layer");
            p.CheckBatch(11, 12, 24, incoming: 0, hold: false, frame: 1).Should().BeFalse();
            p.CheckBatch(5, 12, 24, incoming: 7, hold: false, frame: 2).Should().BeFalse("7 fit exactly next to 5 (and 5/12 is below 75%)");
            p.CheckBatch(5, 12, 24, incoming: 8, hold: false, frame: 3).Should().BeTrue();
        }

        [Fact]
        public void A_batch_never_grows_past_the_ceiling()
        {
            var p = new GrowthPolicy();
            p.CheckBatch(24, 24, 24, incoming: 5, hold: false, frame: 0).Should().BeFalse("at the ceiling the batch evicts as before");
            GrowthPolicy.Starved(24, 24, 5).Should().BeTrue();
            GrowthPolicy.Starved(20, 24, 4).Should().BeFalse();
        }

        [Fact]
        public void NextFor_doubles_until_the_batch_fits_in_one_step()
        {
            GrowthPolicy.NextFor(32, 128, 40).Should().Be(64);
            GrowthPolicy.NextFor(32, 128, 70).Should().Be(128, "two doublings, recreated once");
            GrowthPolicy.NextFor(32, 128, 500).Should().Be(128, "at most the ceiling");
            GrowthPolicy.NextFor(12, 24, 1).Should().Be(24, "at least one doubling");
        }

        [Fact]
        public void RangeAllocator_grow_keeps_allocations_and_merges_the_tail()
        {
            var a = new RangeAllocator(100);
            a.Alloc(60).Should().Be(0);
            a.Alloc(50).Should().Be(-1, "only 40 are free");
            a.Grow(200);
            a.Capacity.Should().Be(200);
            // the free tail [60, 100) merged with [100, 200): one 140 range
            a.Alloc(140).Should().Be(60);
            a.Used.Should().Be(200);
            a.Alloc(1).Should().Be(-1);
        }

        [Fact]
        public void RangeAllocator_grow_appends_when_the_tail_is_used()
        {
            var a = new RangeAllocator(100);
            a.Alloc(100).Should().Be(0);
            a.Free(10, 10);
            a.Grow(150);
            a.Alloc(30).Should().Be(100, "the 10-wide hole is too small; the new range starts at the old capacity");
            a.Alloc(10).Should().Be(10);
            a.Alloc(20).Should().Be(130);
            a.Alloc(1).Should().Be(-1);
        }
    }
}
