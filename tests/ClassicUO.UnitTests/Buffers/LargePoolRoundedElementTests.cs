using System.Runtime.InteropServices;
using Uo.Buffers;
using Xunit;

namespace ClassicUO.UnitTests.Buffers;

public class LargePoolRoundedElementTests
{
    [StructLayout(LayoutKind.Sequential, Size = 3)]
    private struct ThreeBytes { }

    [Fact]
    public void Whole_elements_below_the_byte_class_remain_pooled()
    {
        using var pool = new IsolatedLargePool();
        ThreeBytes[] first = pool.Rent<ThreeBytes>(21846);
        Assert.Equal(27306, first.Length); // 81920-byte class / 3, rounded down.
        pool.Return(first);
        Assert.Equal(81918, pool.RetainedBytes);
        Assert.Equal(81918, pool.GeneralFreeBytes);
        ThreeBytes[] second = pool.Rent<ThreeBytes>(21846);
        Assert.Same(first, second);
        Assert.Equal(0, pool.RetainedBytes);
        pool.Return(second);
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.OutstandingBytes)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.Dropped)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evicting_rounded_arrays_subtracts_actual_payload_bytes(bool idle)
    {
        using var pool = new IsolatedLargePool();
        pool.RoundTrip(typeof(byte)); // Element size must be selected by slot, not by registration order.
        if (idle) pool.TrimIdle();
        else pool.SetGeneralCap(0);
        pool.SetGeneralCap(32L << 20);

        ThreeBytes[] array = pool.Rent<ThreeBytes>(21846);
        pool.Return(array);
        Assert.Equal(81918, pool.RetainedBytes);
        if (idle) pool.TrimIdle();
        else pool.SetGeneralCap(0);

        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.GeneralFreeBytes);
        Assert.Equal(2, pool.Counter(nameof(LargeBufferPool.Evicted)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.OutstandingBytes)));
        pool.SetGeneralCap(32L << 20);
        pool.RoundTrip(typeof(byte));
        Assert.Equal(81920, pool.RetainedBytes);
    }
}
