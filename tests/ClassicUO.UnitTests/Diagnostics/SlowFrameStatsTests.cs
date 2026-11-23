using System;
using System.Text;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

[Collection("FrameStats")]
public class SlowFrameStatsTests
{
    [Theory]
    [InlineData(8.0, 0)]
    [InlineData(11.99, 0)]
    [InlineData(12.0, 1)]
    [InlineData(16.0, 2)]
    [InlineData(25.0, 3)]
    [InlineData(50.0, 4)]
    [InlineData(99.9, 4)]
    [InlineData(100.0, 5)]
    [InlineData(400.0, 5)]
    public void Buckets(double ms, int bucket) => Assert.Equal(bucket, SlowFrameStats.Bucket(ms));

    [Fact]
    public void Ring_Keeps_The_Worst_And_Flush_Formats_Old_Lines()
    {
        SlowFrameStats.Reset();
        int tag = SlowFrameStats.TagId("walk");

        for (int i = 0; i < 100; i++)
        {
            SlowFrameStats.Record(i, 9 + i * 0.5, i % 10 == 0 ? SlowFrameKind.Held : SlowFrameKind.Play, tag);
        }

        Assert.Equal(100, SlowFrameStats.Total);
        Assert.Equal(SlowFrameStats.RingSize, SlowFrameStats.RingCount);
        Assert.True(SlowFrameStats.RingMin() >= 9 + (100 - SlowFrameStats.RingSize) * 0.5 - 0.001);

        var sb = new StringBuilder();
        Assert.True(SlowFrameStats.Flush(sb, "window"));
        string text = sb.ToString();

        Assert.Contains("[gcs] slow frames window: 100 over 8 ms: play 90 (max 58.5 ms), held 10 (max 54.0 ms)", text);
        Assert.Contains("worst 32", text);
        Assert.Contains("[gcs] +99.00s slow frame 58.50 ms, play, tag walk", text);
        Assert.Equal(0, SlowFrameStats.Total);
        Assert.False(SlowFrameStats.Flush(new StringBuilder(), "window"));
    }

    [Fact]
    public void Record_Allocates_Nothing()
    {
        SlowFrameStats.Reset();
        int tag = SlowFrameStats.TagId("alloc");

        for (int i = 0; i < 1000; i++)
        {
            SlowFrameStats.Record(i, 9 + i % 200, SlowFrameKind.Play, tag);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100000; i++)
        {
            SlowFrameStats.Record(i, 9 + i % 200, (SlowFrameKind)(i % 3), tag);
            SlowFrameStats.NoteAfterReport();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        SlowFrameStats.Reset();
    }
}
