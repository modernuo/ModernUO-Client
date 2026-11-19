using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

public class RenderViewReuseTests
{
    [Fact]
    public void ReusesOnlyAnIdenticalSettledView()
    {
        var cache = new RenderViewReuse();
        var view = new RectI(1, 2, 3, 4);
        var need = new RectI(0, 0, 8, 8);
        Assert.False(cache.Matches(view, need, 1, 7, 8, 9, true));
        cache.Remember(view, need, 1, 7, 8, 9);
        Assert.True(cache.Matches(view, need, 1, 7, 8, 9, true));
        Assert.False(cache.Matches(view, need, 1, 7, 8, 9, false));
        Assert.False(cache.Matches(new RectI(1, 2, 4, 4), need, 1, 7, 8, 9, true));
        Assert.False(cache.Matches(view, new RectI(0, 0, 9, 8), 1, 7, 8, 9, true));
        Assert.False(cache.Matches(view, need, 2, 7, 8, 9, true));
        Assert.False(cache.Matches(view, need, 1, 8, 8, 9, true));
        Assert.False(cache.Matches(view, need, 1, 7, 9, 9, true));
        Assert.False(cache.Matches(view, need, 1, 7, 8, 10, true));
    }

    [Fact]
    public void StableKeyChecksDoNotAllocate()
    {
        var cache = new RenderViewReuse();
        var rect = new RectI(0, 0, 10, 10);
        cache.Remember(rect, rect, 1, 1, 1, 1);
        cache.Matches(rect, rect, 1, 1, 1, 1, true);
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) cache.Matches(rect, rect, 1, 1, 1, 1, true);
        Assert.Equal(before, System.GC.GetAllocatedBytesForCurrentThread());
    }
}
