#if CUO_DEVTOOLS
using ClassicUO;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

public class DiagHistogramTests
{
    [Fact]
    public void PercentilesIncludeTailsAndClear()
    {
        var h = new DiagHistogram();
        Assert.Equal(0, h.Percentile(0.99));
        for (int i = 1; i <= 100; i++) h.Add(i);
        Assert.Equal(50, h.Percentile(0.50));
        Assert.Equal(95, h.Percentile(0.95));
        Assert.Equal(99, h.Percentile(0.99));
        h.Add(1000000);
        Assert.Equal(1000000, h.Max);
        Assert.Equal(101, h.Count);
        h.Clear();
        Assert.Equal(0, h.Max);
        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.Percentile(1));
    }

    [Fact]
    public void OverflowBucketUsesActualMaximum()
    {
        var h = new DiagHistogram();
        h.Add(-1);
        h.Add(1000000);
        Assert.Equal(0, h.Percentile(0.5));
        Assert.Equal(1000000, h.Percentile(0.99));
    }
}
#endif
