// SPDX-License-Identifier: BSD-2-Clause

using System.Text;

namespace Uo.Buffers;

/// <summary>
/// Counters of the buffer pools for the stats line: the sum over the live per-thread ST pools and the large pool.
/// Object pools elsewhere register a counter source with <see cref="RegisterSource"/>.
/// </summary>
public static class PoolStats
{
    private static readonly object _lock = new object();
    private static readonly List<WeakReference<object>> _stPools = new List<WeakReference<object>>();
    private static readonly List<(string Name, Func<string> Read)> _sources = new List<(string, Func<string>)>();

    internal static void Register<T>(STArrayPool<T> pool)
    {
        lock (_lock)
        {
            _stPools.RemoveAll(static w => !w.TryGetTarget(out _));
            _stPools.Add(new WeakReference<object>(pool));
        }
    }

    /// <summary>Adds a named counter source (an object pool's cap, size and drops) to <see cref="Format"/>.</summary>
    public static void RegisterSource(string name, Func<string> read)
    {
        lock (_lock)
        {
            _sources.RemoveAll(s => s.Name == name);
            _sources.Add((name, read));
        }
    }

    public readonly record struct StSummary(int Pools, long Rents, long Misses, long AllocatedBytes, long RetainedBytes);

    /// <summary>Sum over the live ST pools. Reads other threads' plain counters: approximate, for stats only.</summary>
    public static StSummary SummarizeSt()
    {
        int pools = 0;
        long rents = 0, misses = 0, alloc = 0, retained = 0;

        lock (_lock)
        {
            foreach (WeakReference<object> w in _stPools)
            {
                if (!w.TryGetTarget(out object o) || o is not IStPoolStats p)
                {
                    continue;
                }

                pools++;
                rents += p.Rents;
                misses += p.Misses;
                alloc += p.AllocatedBytes;
                retained += p.RetainedBytes;
            }
        }

        return new StSummary(pools, rents, misses, alloc, retained);
    }

    /// <summary>One line: large pool, ST pools, and each registered source.</summary>
    public static string Format()
    {
        StSummary st = SummarizeSt();
        var sb = new StringBuilder(256);
        sb.Append("large rents=").Append(LargeBufferPool.Rents)
          .Append(" hits=").Append(LargeBufferPool.Hits)
          .Append(" miss=").Append(LargeBufferPool.Misses)
          .Append(" allocMB=").Append(Mb(LargeBufferPool.AllocatedBytes))
          .Append(" outMB=").Append(Mb(LargeBufferPool.OutstandingBytes))
          .Append(" hwMB=").Append(Mb(LargeBufferPool.HighWaterBytes))
          .Append(" freeMB=").Append(Mb(LargeBufferPool.RetainedBytes)).Append('/').Append(Mb(LargeBufferPool.BudgetBytes))
          .Append(" drop=").Append(LargeBufferPool.Dropped)
          .Append(" evict=").Append(LargeBufferPool.Evicted)
          .Append(" | st pools=").Append(st.Pools)
          .Append(" rents=").Append(st.Rents)
          .Append(" miss=").Append(st.Misses)
          .Append(" allocMB=").Append(Mb(st.AllocatedBytes))
          .Append(" freeMB=").Append(Mb(st.RetainedBytes));

        lock (_lock)
        {
            foreach ((string name, Func<string> read) in _sources)
            {
                sb.Append(" | ").Append(name).Append(' ').Append(read());
            }
        }

        return sb.ToString();
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}

internal interface IStPoolStats
{
    long Rents { get; }
    long Misses { get; }
    long AllocatedBytes { get; }
    long RetainedBytes { get; }
}
