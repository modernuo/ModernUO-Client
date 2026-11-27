using System;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using Uo.Buffers;
using Uo.Render;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Buffers;

public class LargePoolTypeRegistrationTests(ITestOutputHelper output)
{
    // Include growable ref collections and optional measurement paths in the same session.
    // Poc's ChunkDraw arrays use new; bounded int/ushort/sbyte/Chunk/Wall consumers stay small.
    private static Type[] ProductionTypes() =>
    [
        typeof(byte), typeof(uint), typeof(char), typeof(ulong), typeof(GameObject),
        typeof(LightSearchMeasure).GetNestedType("Search", BindingFlags.NonPublic)!,
        typeof(GpuItemCache.Entry), typeof(SpriteInstance),
        typeof(WebLinkRect), typeof(HTMLDataInfo), typeof(Multi)
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_poc_element_type_remains_pooled_regardless_of_registration_order(bool reverse)
    {
        using var pool = new IsolatedLargePool();
        Type[] types = ProductionTypes();
        if (reverse) Array.Reverse(types);
        foreach (Type type in types)
        {
            output.WriteLine($"{type.FullName}: {IsolatedLargePool.SizeOf(type)} bytes/element");
            pool.RoundTrip(type);
        }

        Assert.Equal(11, pool.Counter(nameof(LargeBufferPool.Hits)));
        Assert.Equal(11, pool.Counter(nameof(LargeBufferPool.Misses)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.Dropped)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.Evicted)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.OutstandingBytes)));
        Assert.True(pool.RetainedBytes > 11L * BufferPool.SmallMaxBytes);
        output.WriteLine($"Retained payload for one array per type: {pool.RetainedBytes} bytes");
    }

    [Fact]
    public void Fresh_pool_retains_eleven_element_types_and_drops_the_twelfth()
    {
        using var pool = new IsolatedLargePool();
        // Test-only wrappers spend registrations only in this isolated assembly.
        Type[] arguments = [typeof(byte), typeof(uint), typeof(int), typeof(long), typeof(char),
            typeof(short), typeof(ushort), typeof(float), typeof(double), typeof(decimal), typeof(ulong)];
        foreach (Type argument in arguments)
            pool.RoundTrip(typeof(Probe<>).MakeGenericType(argument));

        pool.RoundTrip(typeof(Guid), retained: false);
        Assert.Equal(11, pool.Counter(nameof(LargeBufferPool.Hits)));
        Assert.Equal(13, pool.Counter(nameof(LargeBufferPool.Misses)));
        Assert.Equal(2, pool.Counter(nameof(LargeBufferPool.Dropped)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.Evicted)));
        Assert.Equal(0, pool.Counter(nameof(LargeBufferPool.OutstandingBytes)));
    }

    private struct Probe<T> { }
}
