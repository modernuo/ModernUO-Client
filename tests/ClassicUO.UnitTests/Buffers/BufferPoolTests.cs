using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Uo.Buffers;
using Xunit;

namespace ClassicUO.UnitTests.Buffers
{
    [CollectionDefinition(nameof(LargeBufferPoolCollection), DisableParallelization = true)]
    public class LargeBufferPoolCollection
    {
    }

    // Ported from ModernUO Server.Tests/Tests/Buffers/STArrayPoolTests.cs (commit 3161a5ba4), for the per-thread pool.
    public class STArrayPoolTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(2, 16)]
        [InlineData(56, 64)]
        [InlineData(120, 128)]
        [InlineData(65535, 65536)]
        [InlineData(1024 * 1024 * 15, 1024 * 1024 * 16)]
        public void ValidMinimumLengths(int requestedLength, int expectedLength)
        {
            byte[] arr = STArrayPool<byte>.ThreadLocal.Rent(requestedLength);
            Assert.Equal(expectedLength, arr.Length);
        }

        [Fact]
        public void NegativeLengthThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => STArrayPool<byte>.ThreadLocal.Rent(-1));
        }

        private struct CacheProbe
        {
            public byte B;
        }

        [Fact]
        public void CachesOnlyUpToStackSizePerBucket()
        {
            STArrayPool<CacheProbe> pool = STArrayPool<CacheProbe>.ThreadLocal;
            pool.ResetForTesting();

            var arrays1 = new CacheProbe[32 + 2][]; // 1 for the cache, 32 for the stack, and 1 for overflow
            var weak1 = new WeakReference[32 + 2];
            var arrays2 = new CacheProbe[32 + 2][];
            var weak2 = new WeakReference[32 + 2];

            for (int i = 0; i < arrays1.Length; i++)
            {
                arrays1[i] = pool.Rent(32);
                weak1[i] = new WeakReference(arrays1[i]);
                arrays2[i] = pool.Rent(64);
                weak2[i] = new WeakReference(arrays2[i]);
            }

            for (int i = 0; i < arrays1.Length; i++)
            {
                pool.Return(arrays1[i]);
                arrays1[i] = null;
                pool.Return(arrays2[i]);
                arrays2[i] = null;
            }

            GC.Collect();

            for (int i = 0; i < weak1.Length; i++)
            {
                // When the last one is returned, the one right before it is dropped.
                Assert.Equal(i != weak1.Length - 2, weak1[i].IsAlive);
                Assert.Equal(i != weak2.Length - 2, weak2[i].IsAlive);
            }
        }

        private struct ThreadProbe
        {
            public int I;
        }

        [Fact]
        public void EachThreadHasItsOwnPool()
        {
            STArrayPool<ThreadProbe> mine = STArrayPool<ThreadProbe>.ThreadLocal;
            STArrayPool<ThreadProbe> other = null;
            var t = new Thread(() => other = STArrayPool<ThreadProbe>.ThreadLocal);
            t.Start();
            t.Join();

            Assert.NotNull(other);
            Assert.NotSame(mine, other);
            Assert.Same(mine, STArrayPool<ThreadProbe>.ThreadLocal);
        }

        [Fact]
        public void ReturnOfForeignSizeThrows()
        {
            Assert.Throws<ArgumentException>(() => STArrayPool<byte>.ThreadLocal.Return(new byte[100]));
        }

        private struct TrimProbe
        {
            public long L;
        }

        [Fact]
        public void Gen2TrimRunsOnTheOwnerOnNextRent()
        {
            STArrayPool<TrimProbe> pool = STArrayPool<TrimProbe>.ThreadLocal;
            pool.Return(pool.Rent(16));
            long trims = pool.Trims;

            int epoch = Gen2Epoch.Value;

            for (int i = 0; i < 10 && Gen2Epoch.Value == epoch; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }

            Assert.NotEqual(epoch, Gen2Epoch.Value);
            pool.Return(pool.Rent(16));
            Assert.Equal(trims + 1, pool.Trims);
        }
    }

    [Collection(nameof(LargeBufferPoolCollection))]
    public class LargeBufferPoolTests : IDisposable
    {
        public LargeBufferPoolTests() => LargeBufferPool.ResetForTesting();

        public void Dispose() => LargeBufferPool.ResetForTesting();

        [Theory]
        [InlineData(1, 64 << 10)]
        [InlineData((64 << 10) + 1, 80 << 10)]
        [InlineData(80 << 10, 80 << 10)]
        [InlineData(100 << 10, 112 << 10)]
        [InlineData(1 << 20, 1 << 20)]
        [InlineData((1 << 20) + 1, 1280 << 10)]
        [InlineData(3_300_000, 3584 << 10)]
        [InlineData(64 << 20, 64 << 20)]
        public void ClassesAreFourPerOctave(int bytes, long classBytes)
        {
            Assert.Equal(classBytes, LargeBufferPool.ClassBytes(LargeBufferPool.ClassOf(bytes)));
        }

        [Fact]
        public void AboveLargestClassIsExactAndNotPooled()
        {
            Assert.Equal(-1, LargeBufferPool.ClassOf((64L << 20) + 1));
            byte[] a = LargeBufferPool.Rent<byte>((64 << 20) + 1);
            Assert.Equal((64 << 20) + 1, a.Length);
            LargeBufferPool.Return(a);
            Assert.Equal(1, LargeBufferPool.Dropped);
            Assert.Equal(0, LargeBufferPool.RetainedBytes);
        }

        [Fact]
        public void RentAfterReturnReusesAndCounts()
        {
            uint[] a = LargeBufferPool.Rent<uint>(300_000);
            Assert.Equal(LargeBufferPool.PooledLength<uint>(300_000), a.Length);
            Assert.Equal(1, LargeBufferPool.Misses);
            Assert.Equal((long)a.Length * 4, LargeBufferPool.OutstandingBytes);

            LargeBufferPool.Return(a);
            Assert.Equal(0, LargeBufferPool.OutstandingBytes);
            Assert.Equal((long)a.Length * 4, LargeBufferPool.RetainedBytes);

            uint[] b = LargeBufferPool.Rent<uint>(290_000);
            Assert.Same(a, b);
            Assert.Equal(1, LargeBufferPool.Hits);
            Assert.Equal((long)a.Length * 4, LargeBufferPool.HighWaterBytes);
            LargeBufferPool.Return(b);
        }

        [Fact]
        public void ElementTypesDoNotMix()
        {
            byte[] a = LargeBufferPool.Rent<byte>(1 << 20);
            LargeBufferPool.Return(a);
            uint[] b = LargeBufferPool.Rent<uint>(1 << 18); // same byte size, other type
            Assert.Equal(1 << 18, b.Length);
            Assert.Equal(2, LargeBufferPool.Misses);
        }

        [Fact]
        public void BudgetEvictsLeastRecentlyReturned()
        {
            LargeBufferPool.SetBudget(3 << 20);
            byte[] a = LargeBufferPool.Rent<byte>(1 << 20);
            byte[] b = LargeBufferPool.Rent<byte>(2 << 20);
            byte[] c = LargeBufferPool.Rent<byte>(1536 << 10);
            LargeBufferPool.Return(a);
            LargeBufferPool.Return(b);
            Assert.Equal(3 << 20, LargeBufferPool.RetainedBytes);

            LargeBufferPool.Return(c); // 1.5 MiB more: evicts a (oldest), then still over: evicts b
            Assert.True(LargeBufferPool.RetainedBytes <= 3 << 20);
            Assert.Equal(1536 << 10, LargeBufferPool.RetainedBytes);
            Assert.Equal(2, LargeBufferPool.Evicted);
            Assert.NotSame(a, LargeBufferPool.Rent<byte>(1 << 20));
        }

        [Fact]
        public void ForeignSizeIsDropped()
        {
            LargeBufferPool.Return(new byte[100_000]);
            Assert.Equal(1, LargeBufferPool.Dropped);
            Assert.Equal(0, LargeBufferPool.RetainedBytes);
        }

        [Fact]
        public void TrimDropsEverything()
        {
            LargeBufferPool.Return(LargeBufferPool.Rent<byte>(1 << 20));
            LargeBufferPool.Trim();
            Assert.Equal(0, LargeBufferPool.RetainedBytes);
        }

        [Fact]
        public void TrimIdleKeepsRecentReturns()
        {
            LargeBufferPool.Return(LargeBufferPool.Rent<byte>(1 << 20));
            LargeBufferPool.TrimIdle(60_000);
            Assert.Equal(1 << 20, LargeBufferPool.RetainedBytes);
            LargeBufferPool.TrimIdle(-1, 0);
            Assert.Equal(0, LargeBufferPool.RetainedBytes);
        }

        [Fact]
        public void TrimIdleKeepsTheFloor()
        {
            LargeBufferPool.Return(LargeBufferPool.Rent<byte>(1 << 20));
            LargeBufferPool.TrimIdle(-1);
            Assert.Equal(1 << 20, LargeBufferPool.RetainedBytes);

            var held = new byte[40][];
            for (int i = 0; i < held.Length; i++)
                held[i] = LargeBufferPool.Rent<byte>(1 << 20);
            foreach (byte[] b in held)
                LargeBufferPool.Return(b);

            LargeBufferPool.TrimIdle(-1);
            Assert.InRange(LargeBufferPool.RetainedBytes, LargeBufferPool.IdleFloorBytes, LargeBufferPool.IdleFloorBytes + (1 << 20));
        }

        [Fact]
        public void IdleTrimDefaults()
        {
            Assert.Equal(112L << 20, LargeBufferPool.DefaultBudgetBytes);
            Assert.Equal(112L << 20, LargeBufferPool.BudgetBytes);
            Assert.Equal(16L << 20, LargeBufferPool.IdleFloorBytes);
            Assert.Equal(180_000, LargeBufferPool.DefaultIdleMs);
        }

        [Fact]
        public void ConcurrentRentReturnKeepsCountsConsistent()
        {
            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 200; i++)
                {
                    byte[] a = LargeBufferPool.Rent<byte>(100_000 + (i % 7) * 50_000);
                    a[0] = 1;
                    LargeBufferPool.Return(a);
                }
            });

            Assert.Equal(0, LargeBufferPool.OutstandingBytes);
            Assert.Equal(1600, LargeBufferPool.Rents);
            Assert.Equal(1600, LargeBufferPool.Hits + LargeBufferPool.Misses);
            Assert.True(LargeBufferPool.RetainedBytes <= LargeBufferPool.BudgetBytes);
        }
    }

    [Collection(nameof(LargeBufferPoolCollection))]
    public class BufferPoolFacadeTests : IDisposable
    {
        public BufferPoolFacadeTests() => LargeBufferPool.ResetForTesting();

        public void Dispose() => LargeBufferPool.ResetForTesting();

        [Fact]
        public void SmallGoesToTheThreadPoolLargeToTheLargePool()
        {
            byte[] small = BufferPool.Rent<byte>(BufferPool.SmallMaxBytes);
            Assert.Equal(BufferPool.SmallMaxBytes, small.Length);
            Assert.Equal(0, LargeBufferPool.Rents);

            byte[] large = BufferPool.Rent<byte>(BufferPool.SmallMaxBytes + 1);
            Assert.Equal(1, LargeBufferPool.Rents);
            Assert.Equal(80 << 10, large.Length);

            BufferPool.Return(small);
            BufferPool.Return(large);
            Assert.Equal(80 << 10, LargeBufferPool.RetainedBytes);

            uint[] u = BufferPool.Rent<uint>(BufferPool.SmallMaxBytes / 4 + 1);
            Assert.Equal(2, LargeBufferPool.Rents);
            BufferPool.Return(u);
        }

        [Fact]
        public void SharedFamilyCrossesThreads()
        {
            uint[] small = null, large = null;
            var t = new Thread(() =>
            {
                small = BufferPool.RentShared<uint>(1000);
                large = BufferPool.RentShared<uint>(500_000);
            });
            t.Start();
            t.Join();

            BufferPool.ReturnShared(small);
            BufferPool.ReturnShared(large);
            Assert.Same(large, BufferPool.RentShared<uint>(500_000));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 16)]
        [InlineData(1000, 1024)]
        [InlineData(16384, 16384)]
        [InlineData(16385, 20480)]
        [InlineData(1 << 20, 1 << 20)]
        public void PooledLengthMatchesRent(int n, int expected)
        {
            Assert.Equal(expected, BufferPool.PooledLength<uint>(n));

            if (n > 0)
            {
                uint[] a = BufferPool.RentShared<uint>(n);
                Assert.Equal(expected, a.Length);
                BufferPool.ReturnShared(a);
            }
        }

        [Fact]
        public void PooledRefListUsesThreadPool()
        {
            using var list = PooledRefList<int>.Create();

            for (int i = 0; i < 100; i++)
            {
                list.Add(i);
            }

            Assert.Equal(100, list.Count);
            Assert.Equal(99, list[99]);
        }

        // The ref collections rent through the facade: an array above 64 KiB goes to the large pool, never to the
        // per-thread STArrayPool (which keeps up to 33 arrays per bucket for the thread's life).
        [Fact]
        public void PooledRefListAboveSmallMaxUsesLargePool()
        {
            const int n = BufferPool.SmallMaxBytes / sizeof(int) * 2;
            long rents = LargeBufferPool.Rents;
            var list = PooledRefList<int>.Create();

            for (int i = 0; i < n; i++)
            {
                list.Add(i);
            }

            Assert.True(LargeBufferPool.Rents > rents);
            long returns = LargeBufferPool.Returns;
            list.Dispose();
            Assert.True(LargeBufferPool.Returns > returns);
        }

        [Fact]
        public void PooledRefQueueAboveSmallMaxUsesLargePool()
        {
            const int n = BufferPool.SmallMaxBytes / sizeof(long) + 1;
            long rents = LargeBufferPool.Rents;
            var queue = PooledRefQueue<long>.Create(n);
            Assert.True(LargeBufferPool.Rents > rents);

            queue.Enqueue(1);
            Assert.Equal(1, queue.Dequeue());
            long returns = LargeBufferPool.Returns;
            queue.Dispose();
            Assert.True(LargeBufferPool.Returns > returns);
        }

        [Fact]
        public void ValueStringBuilderAboveSmallMaxUsesLargePool()
        {
            long rents = LargeBufferPool.Rents;
            var sb = ValueStringBuilder.Create(BufferPool.SmallMaxBytes / sizeof(char) + 1);
            Assert.True(LargeBufferPool.Rents > rents);
            sb.Append('x');
            long returns = LargeBufferPool.Returns;
            sb.Dispose();
            Assert.True(LargeBufferPool.Returns > returns);
        }

        // The client members kept from the earlier builder: Replace swaps the first occurrence only (spell display
        // format), Insert takes a span (cliloc arguments), ToString leaves the builder usable until Dispose.
        [Fact]
        public void ValueStringBuilderClientMembers()
        {
            var sb = new ValueStringBuilder("{power} [{spell}] {spell}".AsSpan());
            sb.Replace("{power}".AsSpan(), "In Mani".AsSpan());
            sb.Replace("{spell}".AsSpan(), "Heal".AsSpan());
            Assert.Equal("In Mani [Heal] {spell}", sb.ToString());

            sb.Replace("In Mani".AsSpan(), "X".AsSpan());
            Assert.Equal("X [Heal] {spell}", sb.ToString());

            sb.Insert(1, "yz".AsSpan());
            sb.Append('!');
            Assert.Equal("Xyz [Heal] {spell}!", sb.ToString());
            sb.Dispose();

            Span<char> stack = stackalloc char[2];
            var small = new ValueStringBuilder(stack);
            small.Append('a');
            small.Append('b');
            small.Append('c');
            Assert.Equal("abc", small.ToString());
            small.Dispose();
        }

        [Fact]
        public void ValueStringBuilderBuilds()
        {
            using var sb = ValueStringBuilder.Create();
            sb.Append("abc");
            sb.Append(12);
            Assert.Equal("abc12", sb.ToString());
        }
    }
}
