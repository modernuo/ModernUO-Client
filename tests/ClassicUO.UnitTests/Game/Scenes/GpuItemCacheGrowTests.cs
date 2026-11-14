using System;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // GpuItemCache.Add grows its arrays from the shared ArrayPool (and returns the outgrown ones to it) instead of
    // Array.Resize: the entries and instances added keep their values across growth, and an entry past Count reads as a new
    // one even when the pooled array held an earlier build's values.
    public sealed class GpuItemCacheGrowTests
    {
        private static SpriteInstance[] One(int x) => new[] { new SpriteInstance { X = x, Y = -x, Depth = (uint)x } };

        [Fact]
        public void Growth_keeps_entries_and_instances()
        {
            var cache = new GpuItemCache();

            for (int i = 0; i < 200; i++)
            {
                ref GpuItemCache.Entry e = ref cache.Add(i % 3 == 0 ? new[] { One(i)[0], One(i + 1000)[0] } : One(i));
                e.Sx = i;
                e.Sig0 = (ulong)i * 7;
            }

            cache.Count.Should().Be(200);
            int inst = 0;

            for (int i = 0; i < 200; i++)
            {
                GpuItemCache.Entry e = cache.Entries[i];
                e.Sx.Should().Be(i);
                e.Sig0.Should().Be((ulong)i * 7);
                e.InstStart.Should().Be(inst);
                e.InstCount.Should().Be(i % 3 == 0 ? 2 : 1);
                cache.Instances[e.InstStart].X.Should().Be(i);
                inst += e.InstCount;
            }

            cache.InstanceCount.Should().Be(inst);
        }

        [Fact]
        public void Entries_past_count_read_as_new_after_growth_from_a_dirty_pooled_array()
        {
            // dirty pool-sized arrays of the next size up, returned to the pool this thread rents from
            for (int k = 0; k < 4; k++)
            {
                var dirty = new GpuItemCache.Entry[32];
                Array.Fill(dirty, new GpuItemCache.Entry { Sx = 99, Light = true, Tile = 7, Sig1 = 5 });
                System.Buffers.ArrayPool<GpuItemCache.Entry>.Shared.Return(dirty);
            }

            var cache = new GpuItemCache();

            for (int i = 0; i < 17; i++)
            {
                cache.Add(One(i));
            }

            for (int i = 16; i < cache.Entries.Length; i++)
            {
                GpuItemCache.Entry e = cache.Entries[i];

                if (i == 16)
                {
                    e.InstStart.Should().Be(16);
                    e.InstCount.Should().Be(1);
                }
                else
                {
                    e.InstStart.Should().Be(0);
                    e.InstCount.Should().Be(0);
                }

                e.Sx.Should().Be(0);
                e.Light.Should().BeFalse();
                e.Tile.Should().Be(0);
                e.Sig1.Should().Be(0UL);
            }
        }
    }
}
