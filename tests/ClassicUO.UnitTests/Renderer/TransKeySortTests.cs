// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    public class TransKeySortTests
    {
        [Fact]
        public void Sorts_Ascending_Like_The_Default_Sort()
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

                WorldRenderer.SortTransKeys(keys);

                Assert.Equal(expected, keys);
            }
        }

        [Fact]
        public void Does_Not_Allocate()
        {
            var keys = new ulong[64];
            var rnd = new Random(7);

            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = (ulong)rnd.NextInt64();
            }

            WorldRenderer.SortTransKeys(keys);
            Array.Reverse(keys);

            long before = GC.GetAllocatedBytesForCurrentThread();
            WorldRenderer.SortTransKeys(keys);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }
    }
}
