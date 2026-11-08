// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    public class TransKeySortTests
    {
        // both paths: the cached-comparison sort NativeAOT uses and the default sort JIT uses
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Sorts_Ascending_Like_The_Default_Sort(bool cachedComparison)
        {
            var rnd = new Random(1234);

            foreach (int n in new[] { 2, 3, 17, 300, 5000 })
            {
                var keys = new ulong[n];

                for (int i = 0; i < n; i++)
                {
                    keys[i] = (ulong)rnd.NextInt64() ^ ((ulong)rnd.Next(2) << 63);
                }

                keys[n - 1] = keys[0]; // a duplicate key
                var expected = (ulong[])keys.Clone();
                Array.Sort(expected);

                WorldRenderer.SortTransKeys(keys, cachedComparison);

                Assert.Equal(expected, keys);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Does_Not_Allocate(bool cachedComparison)
        {
            var keys = new ulong[64];
            var rnd = new Random(7);

            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = (ulong)rnd.NextInt64();
            }

            WorldRenderer.SortTransKeys(keys, cachedComparison);
            Array.Reverse(keys);

            long before = GC.GetAllocatedBytesForCurrentThread();
            WorldRenderer.SortTransKeys(keys, cachedComparison);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }

        [Fact]
        public void Default_Overload_Sorts()
        {
            var keys = new ulong[] { 5, ulong.MaxValue, 0, 3, 3 };

            WorldRenderer.SortTransKeys(keys);

            Assert.Equal(new ulong[] { 0, 3, 3, 5, ulong.MaxValue }, keys);
        }
    }
}
