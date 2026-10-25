using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClassicUO.Assets;
using ClassicUO.Game.Map;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // Incremental chunk loading (Map.UpdateLoading) and region prefetch (MapLoader.PrefetchRegion / IsBlockReady)
    public class ChunkLoadingTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        private static bool HavePack => File.Exists(Path.Combine(PackDir, "manifest.json"));

        // a clock that moves forward by `step` ticks every time a chunk loads
        private sealed class FakeClock
        {
            public long Now;
            public long Step;
            public long Read() => Now;
        }

        private static List<(int X, int Y)> Drain(ChunkLoadQueue q, int cx, int cy, uint frame, long budget, FakeClock clock,
            Func<int, int, ChunkLoadResult> result = null)
        {
            var loaded = new List<(int, int)>();
            q.Drain(cx, cy, frame, clock.Now + budget, clock.Read, (x, y) =>
            {
                var r = result?.Invoke(x, y) ?? ChunkLoadResult.Loaded;

                if (r == ChunkLoadResult.Loaded)
                {
                    loaded.Add((x, y));
                    clock.Now += clock.Step;
                }

                return r;
            });

            return loaded;
        }

        [Fact]
        public void Drain_Loads_Nearest_First_Within_Budget()
        {
            var q = new ChunkLoadQueue(ringRadius: 1);
            var clock = new FakeClock { Step = 10 };

            // 11x11 chunks around (100, 100), requested in scan order (as the draw walk does)
            for (int x = 95; x <= 105; x++)
            {
                for (int y = 95; y <= 105; y++)
                {
                    q.Request(x, y, 1);
                }
            }

            // the 9 ring chunks load regardless and their time counts against the budget: 140 ticks leave room for 5 more
            var loaded = Drain(q, 100, 100, 1, 140, clock);

            loaded.Count.Should().Be(9 + 5);
            q.LastRingLoaded.Should().Be(9);

            for (int i = 0; i < 9; i++)
            {
                Math.Max(Math.Abs(loaded[i].X - 100), Math.Abs(loaded[i].Y - 100)).Should().BeLessOrEqualTo(1);
            }

            // the budgeted ones are the next ring out, the four edge-centres first (squared distance 4 before 5 and 8)
            for (int i = 9; i < loaded.Count; i++)
            {
                Math.Max(Math.Abs(loaded[i].X - 100), Math.Abs(loaded[i].Y - 100)).Should().Be(2);
            }

            for (int i = 9; i < 13; i++)
            {
                var (x, y) = loaded[i];
                ((x - 100) * (x - 100) + (y - 100) * (y - 100)).Should().Be(4);
            }

            q.Count.Should().Be(121 - 14);

            // following frames drain the rest, never going back outwards
            int lastRing = 2;

            for (uint frame = 2; q.Count > 0; frame++)
            {
                for (int x = 95; x <= 105; x++)
                {
                    for (int y = 95; y <= 105; y++)
                    {
                        if (q.Contains(x, y))
                        {
                            q.Request(x, y, frame);
                        }
                    }
                }

                foreach (var (x, y) in Drain(q, 100, 100, frame, 50, clock))
                {
                    int ring = Math.Max(Math.Abs(x - 100), Math.Abs(y - 100));
                    ring.Should().BeGreaterOrEqualTo(lastRing);
                    lastRing = ring;
                }

                frame.Should().BeLessThan(40);
            }
        }

        [Fact]
        public void Drain_Always_Makes_Progress_And_Keeps_Not_Ready_Requests()
        {
            var q = new ChunkLoadQueue();
            var clock = new FakeClock { Step = 1000 };

            q.Request(10, 0, 1);
            q.Request(20, 0, 1);
            q.Request(30, 0, 1);

            // budget already spent: still one load per frame
            Drain(q, 0, 0, 1, 0, clock).Should().Equal((10, 0));

            // the nearest one's data is not decoded yet: it stays queued and the next one loads instead
            Drain(q, 0, 0, 1, 0, clock, (x, y) => x == 20 ? ChunkLoadResult.NotReady : ChunkLoadResult.Loaded).Should().Equal((30, 0));
            q.LastNotReady.Should().Be(1);
            q.Contains(20, 0).Should().BeTrue();

            // Done drops the request without counting as a load
            Drain(q, 0, 0, 1, 0, clock, (x, y) => ChunkLoadResult.Done).Should().BeEmpty();
            q.Count.Should().Be(0);
        }

        [Fact]
        public void Ring_Chunks_Ignore_The_Budget_But_Others_Wait()
        {
            // the map's queue: only the player's own chunk ignores the budget
            var own = new ChunkLoadQueue();
            var ownClock = new FakeClock { Step = 1000 };
            own.Request(5, 5, 1);
            own.Request(4, 5, 1);
            own.Request(6, 6, 1);
            Drain(own, 5, 5, 1, 0, ownClock).Should().Equal((5, 5), (4, 5));
            own.LastRingLoaded.Should().Be(1);

            var q = new ChunkLoadQueue(ringRadius: 1);
            var clock = new FakeClock { Step = 1000 };

            q.Request(5, 5, 1);
            q.Request(6, 6, 1);
            q.Request(4, 5, 1);
            q.Request(9, 9, 1);
            q.Request(10, 10, 1);

            var loaded = Drain(q, 5, 5, 1, 0, clock);

            // three ring chunks, then one budgeted (the minimum), then out of time
            loaded.Should().Equal((5, 5), (4, 5), (6, 6), (9, 9));
            q.LastRingLoaded.Should().Be(3);
            q.Contains(10, 10).Should().BeTrue();
        }

        [Fact]
        public void Stale_Requests_Drop_But_Sticky_Ones_Stay()
        {
            var q = new ChunkLoadQueue();
            var clock = new FakeClock { Step = 1 };

            q.Request(50, 50, 1);
            q.Request(60, 60, 1, sticky: true);

            // nothing loads (not ready) and nobody asks again
            for (uint f = 2; f <= 2 + ChunkLoadQueue.StaleFrames; f++)
            {
                Drain(q, 0, 0, f, 100, clock, (x, y) => ChunkLoadResult.NotReady);
            }

            q.Contains(50, 50).Should().BeFalse();
            q.Contains(60, 60).Should().BeTrue();

            // a repeated request keeps a chunk alive
            q.Request(70, 70, 10);

            for (uint f = 11; f < 10 + ChunkLoadQueue.StaleFrames + 5; f++)
            {
                q.Request(70, 70, f);
                Drain(q, 0, 0, f, 100, clock, (x, y) => ChunkLoadResult.NotReady);
            }

            q.Contains(70, 70).Should().BeTrue();
        }

        [Fact]
        public void Drain_Does_Not_Allocate_Per_Frame()
        {
            var q = new ChunkLoadQueue();
            var clock = new FakeClock { Step = 1 };
            Func<int, int, ChunkLoadResult> notReady = (x, y) => ChunkLoadResult.NotReady;
            Func<long> read = clock.Read;

            for (int i = 0; i < 100; i++)
            {
                q.Request(i, i, 1);
            }

            q.Drain(0, 0, 1, 100, read, notReady); // warm up the scratch arrays
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (uint f = 1; f < 3; f++)
            {
                for (int i = 0; i < 100; i++)
                {
                    q.Request(i, i, f);
                }

                q.Drain(0, 0, f, 100, read, notReady);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        }

        // ---- MapLoader prefetch ----

        private static MapLoader OpenMaps(out IDisposable pack)
        {
            ClientVersionHelper.IsClientVersionValid("7.0.103.0", out var cv);
            var p = PackAssets.Open(PackDir);
            pack = p;
            var fm = new UOFileManager(cv, Path.GetTempPath(), p);
            fm.Maps.Load();

            return fm.Maps;
        }

        [Fact]
        public void Prefetched_Regions_Give_The_Same_Blocks_And_Never_Block_IsBlockReady()
        {
            if (!HavePack)
            {
                return;
            }

            var sync = OpenMaps(out var p1);
            var pre = OpenMaps(out var p2);

            try
            {
                bool old = MapLoader.PrefetchEnabled;
                MapLoader.PrefetchEnabled = true;

                // Britain, Moonglow and Yew, plus a block at a region corner (its neighbours span four regions)
                var blocks = new[] { (1495 / 8, 1629 / 8), (4442 / 8, 1172 / 8), (542 / 8, 985 / 8), (64, 64), (192, 192) };

                foreach (var (bx, by) in blocks)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool ready = pre.IsBlockReady(0, bx, by);
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    // first sight: the decode runs on the pool, the call returns at once
                    ready.Should().BeFalse();
                    ms.Should().BeLessThan(2.0);
                }

                // wait for the workers, then everything is ready without a single decode on this thread
                var deadline = DateTime.UtcNow.AddSeconds(30);

                foreach (var (bx, by) in blocks)
                {
                    while (!pre.IsBlockReady(0, bx, by))
                    {
                        DateTime.UtcNow.Should().BeBefore(deadline);
                        Thread.Sleep(5);
                    }
                }

                pre.SyncLoads.Should().Be(0);
                pre.PendingCount.Should().Be(0);

                foreach (var (bx, by) in blocks)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            bool a = sync.TryGetBlock(0, bx + dx, by + dy, out var ba);
                            bool b = pre.TryGetBlock(0, bx + dx, by + dy, out var bb);
                            a.Should().Be(b);

                            if (a)
                            {
                                System.Runtime.InteropServices.MemoryMarshal.AsBytes(ba.Land)
                                      .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(bb.Land)).Should().BeTrue();
                                ba.Statics.Length.Should().Be(bb.Statics.Length);
                                System.Runtime.InteropServices.MemoryMarshal.AsBytes(ba.Statics)
                                      .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(bb.Statics)).Should().BeTrue();
                            }
                        }
                    }
                }

                pre.SyncLoads.Should().Be(0);

                // a lookup of a region still being decoded waits for that decode instead of starting another
                pre.PrefetchRegion(0, 5, 5);
                pre.TryGetBlock(0, 5 * 64 + 5, 5 * 64 + 5, out _).Should().Be(sync.TryGetBlock(0, 5 * 64 + 5, 5 * 64 + 5, out _));
                pre.SyncLoads.Should().Be(0);
                pre.PrefetchesStarted.Should().BeGreaterOrEqualTo(blocks.Length);

                MapLoader.PrefetchEnabled = old;
            }
            finally
            {
                p1.Dispose();
                p2.Dispose();
            }
        }

        [Fact]
        public void Prefetch_Across_Many_Regions_Keeps_Land_Z_Correct()
        {
            if (!HavePack)
            {
                return;
            }

            var sync = OpenMaps(out var p1);
            var pre = OpenMaps(out var p2);

            try
            {
                // prefetch 40 regions (more than the 24 cached) in bursts, as a long glide would, and check z through both
                for (int r = 0; r < 40; r++)
                {
                    int x = (r % 8) * 512 + 37, y = (r / 8) * 512 + 11;
                    pre.PrefetchAround(0, x, y, 104);

                    while (!pre.TryGetLandZ(0, x, y, out _))
                    {
                        Thread.Sleep(1);
                    }

                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (8, 8), (-1, -1), (7, 7) })
                    {
                        pre.GetLandZ(0, x + dx, y + dy).Should().Be(sync.GetLandZ(0, x + dx, y + dy));
                    }
                }
            }
            finally
            {
                p1.Dispose();
                p2.Dispose();
            }
        }
    }
}
