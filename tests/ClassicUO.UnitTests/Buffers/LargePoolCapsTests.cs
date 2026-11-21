using ClassicUO.Game;
using Xunit;

namespace ClassicUO.UnitTests.Buffers
{
    public class LargePoolCapsTests
    {
        [Fact]
        public void DerivedFromThreadsBudgetsAndRegionCaches()
        {
            // 24-core box: 8 Uo.Render workers, MapLoader MaxPending 8, decode budget 16 MiB, gump budget off, 1 MiB chunks
            LargePoolCaps.Caps c = LargePoolCaps.Derive(16L << 20, 0, 8, 8, 1 << 20);

            Assert.Equal(19, c.ScratchThreads); // main 2 + workers 8 + prefetches 8 + one sync load
            Assert.Equal(35L << 20, c.GeneralBytes);
            Assert.Equal(8, c.RegionCount); // 2 caches x 4 regions
            Assert.Equal(32L << 20, c.RegionBytes);
            Assert.Equal(6, c.PageCount); // 3 lanes x 2
            Assert.Equal(6L << 20, c.PageBytes);
        }

        [Fact]
        public void GumpBudgetAddsToGeneral()
        {
            LargePoolCaps.Caps off = LargePoolCaps.Derive(16L << 20, 0, 2, 8, 1 << 20);
            LargePoolCaps.Caps on = LargePoolCaps.Derive(16L << 20, 32L << 20, 2, 8, 1 << 20);

            Assert.Equal(off.GeneralBytes + (32L << 20), on.GeneralBytes);
            Assert.Equal(off.RegionCount, on.RegionCount);
        }
    }
}
