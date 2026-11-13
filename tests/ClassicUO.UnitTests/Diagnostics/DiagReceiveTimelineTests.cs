#if CUO_DEVTOOLS
using ClassicUO;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

public class DiagReceiveTimelineTests
{
    [Fact]
    public void FragmentedReplyUsesSliceContainingFinalByte()
    {
        var timeline = new DiagReceiveTimeline();
        timeline.Enqueue(5, 123);
        timeline.Enqueue(5, 456);
        Assert.Equal(123, timeline.Dequeue(4));
        Assert.Equal(456, timeline.Dequeue(2));
        Assert.Equal(456, timeline.Dequeue(4));
    }

    [Fact]
    public void ReconnectDiscardsPreviousConnectionAndUnknownDataStaysUnknown()
    {
        var timeline = new DiagReceiveTimeline();
        timeline.Enqueue(10, 123);
        timeline.Clear();
        timeline.Enqueue(2, 0);
        timeline.Enqueue(2, 789);
        Assert.Equal(0, timeline.Dequeue(2));
        Assert.Equal(789, timeline.Dequeue(2));
        Assert.Equal(0, timeline.Dequeue(1));
    }

    [Fact]
    public void OverflowDoesNotMisattributeDroppedSlices()
    {
        var timeline = new DiagReceiveTimeline();
        for (int i = 1; i <= 8193; i++) timeline.Enqueue(1, i);
        Assert.Equal(0, timeline.Dequeue(1));
        Assert.Equal(2, timeline.Dequeue(1));
        Assert.Equal(8193, timeline.Dequeue(8191));
    }
}
#endif
