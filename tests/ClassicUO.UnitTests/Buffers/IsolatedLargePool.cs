using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Uo.Buffers;
using Xunit;

namespace ClassicUO.UnitTests.Buffers;

// ResetForTesting keeps TypeSlot<T> registrations. Load the real facade and pool together so
// retention assertions cannot depend on which types another test has already registered.
internal sealed class IsolatedLargePool : IDisposable
{
    private readonly AssemblyLoadContext _context = new("isolated-large-pool", isCollectible: true);
    private readonly Type _pool;
    private readonly Type _facade;

    internal IsolatedLargePool()
    {
        var assembly = _context.LoadFromAssemblyPath(typeof(LargeBufferPool).Assembly.Location);
        _pool = assembly.GetType(typeof(LargeBufferPool).FullName!, throwOnError: true)!;
        _facade = assembly.GetType(typeof(BufferPool).FullName!, throwOnError: true)!;
    }

    internal long Counter(string name) => (long)_pool.GetField(name)!.GetValue(null)!;

    internal long RetainedBytes => (long)_pool.GetProperty(nameof(LargeBufferPool.RetainedBytes))!.GetValue(null)!;

    private object General => Enum.ToObject(_pool.Assembly.GetType(typeof(LargeUse).FullName!)!, 0);

    internal long GeneralFreeBytes
    {
        get
        {
            object stats = _pool.GetMethod(nameof(LargeBufferPool.StatsOf))!.Invoke(null, [General])!;
            return (long)stats.GetType().GetProperty(nameof(LargeBufferPool.UseStats.FreeBytes))!.GetValue(stats)!;
        }
    }

    internal void SetGeneralCap(long bytes) =>
        _pool.GetMethod(nameof(LargeBufferPool.SetCaps))!.Invoke(null, [General, bytes, int.MaxValue]);

    internal void TrimIdle() => _pool.GetMethod(nameof(LargeBufferPool.TrimIdle))!.Invoke(null, [-1L]);

    internal T[] Rent<T>(int length) => (T[])Rent(typeof(T), length);

    private Array Rent(Type type, int length) =>
        (Array)_facade.GetMethod(nameof(BufferPool.Rent))!.MakeGenericMethod(type).Invoke(null, [length])!;

    internal void Return<T>(T[] array) => Return(typeof(T), array);

    private void Return(Type type, Array array) =>
        _facade.GetMethod(nameof(BufferPool.Return))!.MakeGenericMethod(type).Invoke(null, [array, false]);

    internal void RoundTrip(Type type, bool retained = true)
    {
        int size = SizeOf(type);
        int length = BufferPool.SmallMaxBytes / size + 1;
        var first = Rent(type, length);
        Assert.True((long)first.Length * size > BufferPool.SmallMaxBytes);
        Assert.True(first.Length >= length);
        Return(type, first);
        var second = Rent(type, length);
        if (retained) Assert.Same(first, second);
        else Assert.NotSame(first, second);
        Return(type, second);
    }

    internal static int SizeOf(Type type) =>
        (int)typeof(IsolatedLargePool).GetMethod(nameof(ElementSize), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, null)!;

    private static int ElementSize<T>() => Unsafe.SizeOf<T>();

    public void Dispose() => _context.Unload();
}
