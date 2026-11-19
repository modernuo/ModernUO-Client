using System;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

public class UploadReuseTests
{
    [Fact]
    public void ComparesBytesAndDestinationAndInvalidates()
    {
        var cache = new UploadContentCache(16);
        byte[] bytes = [1, 2, 3, 4];
        Assert.True(cache.Prepare(bytes, (IntPtr)1, 0));
        Assert.False(cache.Prepare(bytes, (IntPtr)1, 0));
        bytes[3] = 7;
        Assert.True(cache.Prepare(bytes, (IntPtr)1, 0));
        Assert.True(cache.Prepare(bytes, (IntPtr)2, 0));
        Assert.True(cache.Prepare(bytes, (IntPtr)2, 32));
        Assert.True(cache.Prepare(bytes.AsSpan(0, 3), (IntPtr)2, 32));
        cache.Invalidate(); // cycling, recreation or failed submit
        Assert.True(cache.Prepare(bytes.AsSpan(0, 3), (IntPtr)2, 32));
        Assert.True(cache.Prepare(bytes, (IntPtr)2, 32));
        Assert.False(cache.Prepare(bytes, (IntPtr)2, 32));
    }

    [Fact]
    public void ReusesOnlyTheSameSortInputsIncludingEmissionOrder()
    {
        var cache = new TranslucentOrderCache(8);
        ulong[] input = [9, 0, 4, 4, ulong.MaxValue];
        var keys = (ulong[])input.Clone();
        Assert.False(cache.Sort(keys));
        Assert.Equal(new ulong[] { 0, 4, 4, 9, ulong.MaxValue }, keys);
        input.CopyTo(keys, 0);
        Assert.True(cache.Sort(keys));
        Assert.Equal(new ulong[] { 0, 4, 4, 9, ulong.MaxValue }, keys);
        keys = [4, 9, 0, 4, ulong.MaxValue];
        Assert.False(cache.Sort(keys));
        Assert.False(cache.Sort(keys.AsSpan(0, 3)));
    }

    [Fact]
    public void WarmCachesDoNotAllocateEvenWhenContentChanges()
    {
        var upload = new UploadContentCache(16);
        var sort = new TranslucentOrderCache(8);
        byte[] bytes = [1, 2, 3];
        ulong[] keys = [3, 1, 2];
        upload.Prepare(bytes, (IntPtr)1, 0);
        sort.Sort(keys);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            bytes[0] = (byte)i;
            upload.Prepare(bytes, (IntPtr)1, 0);
            keys[0] = (ulong)i;
            sort.Sort(keys);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void FrameStatsKeysCoverEveryCounter()
    {
        var assembly = typeof(ClassicUO.Configuration.Settings).Assembly;
        var stats = assembly.GetType("ClassicUO.FrameStats")!;
        var counter = assembly.GetType("ClassicUO.FrameCounter")!;
        var keys = (string[])stats.GetField("_counterKeys", System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Equal(Convert.ToInt32(Enum.Parse(counter, "Count")), keys.Length);
        Assert.Equal("prepare_full_runs", keys[Convert.ToInt32(Enum.Parse(counter, "PrepareFullRuns"))]);
    }
}
