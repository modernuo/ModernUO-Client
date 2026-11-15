using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Renderer;
using FluentAssertions;

namespace ClassicUO.UnitTests.Renderer
{
    // Worker threads owned by one test, standing in for the thread pool behind a DecodeBudget. On a loaded machine, or while
    // the suite's parallel tests hold pool threads, the pool can take longer than any wall-clock budget to start an item
    // (10 s budgets saw 51 of 100 and 161 of 1255 decodes). These threads are real and concurrent, but they belong to the test.
    internal sealed class DecodeTestWorkers : IDisposable
    {
        private readonly BlockingCollection<IThreadPoolWorkItem> _items = new BlockingCollection<IThreadPoolWorkItem>();
        private readonly Thread[] _threads;

        public DecodeTestWorkers(int count)
        {
            _threads = new Thread[count];

            for (int i = 0; i < count; i++)
            {
                _threads[i] = new Thread(Run) { IsBackground = true, Name = $"decode-test-{i}" };
                _threads[i].Start();
            }
        }

        public void Start(IThreadPoolWorkItem item) => _items.Add(item);

        /// <summary>A private budget (not <see cref="DecodeBudget.Shared"/>) whose admitted items run on these threads.</summary>
        public DecodeBudget Budget(long capBytes = 0) => new DecodeBudget(capBytes, Start);

        private void Run()
        {
            foreach (var item in _items.GetConsumingEnumerable())
            {
                item.Execute();
            }
        }

        public void Dispose()
        {
            _items.CompleteAdding();

            // not asserted: a failed test may leave a worker blocked in its decode (background threads)
            foreach (var t in _threads)
            {
                t.Join(TimeSpan.FromSeconds(10));
            }
        }
    }

    internal static class DecodeWait
    {
        /// <summary>Only catches a hang; no test property depends on it.</summary>
        public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Waits on the decoder's publish signal until <paramref name="done"/> holds, draining first when
        /// <paramref name="drain"/> (main thread). Fails only past <see cref="HangGuard"/>.
        /// </summary>
        public static void Until(FirstSightDecoder d, Func<bool> done, bool drain)
        {
            var sw = Stopwatch.StartNew();

            while (true)
            {
                // read before the check: a publish after it ends the wait, one before it is seen by the check
                int seen = d.PublishedCount;

                if (drain)
                {
                    d.Drain(long.MaxValue);
                }

                if (done())
                {
                    return;
                }

                sw.Elapsed.Should().BeLessThan(HangGuard, "the decodes hang");
                d.WaitPublishedSince(seen, 100);
            }
        }
    }
}
