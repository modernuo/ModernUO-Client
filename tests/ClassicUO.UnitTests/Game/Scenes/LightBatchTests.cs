using System;
using System.Linq;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>light_batch (light-edges round 6c): with light_occlusion on, the lights with no occluders go in one batch
    /// before the occluded ones; the additive light target does not depend on the order.</summary>
    public sealed class LightBatchTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            LightBatch.OverrideForTests = on;

            try
            {
                LightBatch.Enabled.Should().Be(on);
            }
            finally
            {
                LightBatch.OverrideForTests = null;
            }
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void Batched_path_runs_only_with_the_switch_and_light_occlusion(bool on, bool occlusion, bool batched)
        {
            LightBatch.Batched(on, occlusion).Should().Be(batched);
        }

        // a light target pixel row as the additive blend (one, one) fills it: a saturating sum per channel; each light adds its
        // value where its stencil lets it (an occluded light skips the masked pixels)
        private static byte[] Target(int[][] lights, bool[][] masked, int[] order)
        {
            var px = new byte[lights[0].Length];

            foreach (int l in order)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    if (masked[l] == null || !masked[l][i])
                    {
                        px[i] = (byte)Math.Min(255, px[i] + lights[l][i]);
                    }
                }
            }

            return px;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Batched_order_gives_the_same_target_as_the_per_light_order(bool on)
        {
            var rng = new Random(6);
            const int count = 40, width = 64;
            int[][] lights = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, width).Select(_ => rng.Next(0, 120)).ToArray()).ToArray();
            // about half the lights occluded (bench-001041: 49-54 % at the Moonglow bank and Britain)
            bool[][] masked = Enumerable.Range(0, count)
                .Select(_ => rng.Next(2) == 0 ? null : Enumerable.Range(0, width).Select(_ => rng.Next(3) == 0).ToArray()).ToArray();

            int[] perLight = Enumerable.Range(0, count).ToArray();
            int[] order = on
                ? perLight.Where(l => masked[l] == null).Concat(perLight.Where(l => masked[l] != null)).ToArray()
                : perLight;

            Target(lights, masked, order).Should().Equal(Target(lights, masked, perLight));
        }

        [Fact]
        public void Count_sets_groups_occluded_lights_whose_quads_do_not_overlap()
        {
            var setOf = new int[8];

            LightBatch.CountSets(ReadOnlySpan<Rectangle>.Empty, setOf).Should().Be(0);

            // apart: one set
            LightBatch.CountSets(new[] { new Rectangle(0, 0, 10, 10), new Rectangle(20, 0, 10, 10), new Rectangle(40, 0, 10, 10) }, setOf)
                .Should().Be(1);

            // touching edges do not overlap
            LightBatch.CountSets(new[] { new Rectangle(0, 0, 10, 10), new Rectangle(10, 0, 10, 10) }, setOf).Should().Be(1);

            // all on one spot: one set each
            LightBatch.CountSets(new[] { new Rectangle(0, 0, 10, 10), new Rectangle(1, 1, 10, 10), new Rectangle(2, 2, 10, 10) }, setOf)
                .Should().Be(3);

            // a chain: a overlaps b, b overlaps c, a and c apart: a and c share a set
            LightBatch.CountSets(new[] { new Rectangle(0, 0, 10, 10), new Rectangle(8, 0, 10, 10), new Rectangle(16, 0, 10, 10) }, setOf)
                .Should().Be(2);
            setOf[0].Should().Be(0);
            setOf[1].Should().Be(1);
            setOf[2].Should().Be(0);
        }

        [Fact]
        public void Differ_counts_pixels_and_the_largest_channel_difference()
        {
            var a = new[] { new Color(10, 20, 30, 255), new Color(0, 0, 0, 255), new Color(5, 5, 5, 255) };
            var b = new[] { new Color(10, 20, 30, 255), new Color(0, 3, 0, 255), new Color(5, 5, 6, 255) };

            LightBatch.Differ(a, a, out int none).Should().Be(0);
            none.Should().Be(0);
            LightBatch.Differ(a, b, out int max).Should().Be(2);
            max.Should().Be(3);
        }
    }
}
