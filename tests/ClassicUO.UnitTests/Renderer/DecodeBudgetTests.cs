using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // The in-flight decode byte budget: requests over the cap wait in order and are started by the release that makes
    // room; an oversized item runs alone; nothing is dropped; failed, cancelled and stale decodes give their bytes back
    // exactly once, with or without a later drain; uncounted requests (gumps) never wait.
    public class DecodeBudgetTests
    {
        private sealed class Item : DecodeBudget.Item
        {
            public readonly int Id;
            public Item(int id) => Id = id;
            public override void Execute() { }
        }

        private static (DecodeBudget budget, List<int> started) Make(long cap)
        {
            var started = new List<int>();

            return (new DecodeBudget(cap, item => started.Add(((Item)item).Id)), started);
        }

        [Fact]
        public void Items_Within_The_Cap_Start_Immediately()
        {
            var (b, started) = Make(100);
            b.Submit(new Item(1), 40);
            b.Submit(new Item(2), 60);

            started.Should().Equal(1, 2);
            b.InFlightBytes.Should().Be(100);
            b.Waiting.Should().Be(0);
        }

        [Fact]
        public void Items_Over_The_Cap_Wait_And_Start_In_Order_As_Room_Appears()
        {
            var (b, started) = Make(100);
            var first = new Item(1);
            b.Submit(first, 60);
            b.Submit(new Item(2), 60); // 120 > 100: waits
            b.Submit(new Item(3), 10);  // would fit, but must not overtake 2

            started.Should().Equal(1);
            b.Waiting.Should().Be(2);

            b.Release(first).Should().BeTrue();
            started.Should().Equal(1, 2, 3);
            b.InFlightBytes.Should().Be(70);
            b.Waiting.Should().Be(0);
        }

        [Fact]
        public void An_Oversized_Item_Is_Admitted_Alone_And_Nothing_Starves_Behind_It()
        {
            var (b, started) = Make(100);
            var i1 = new Item(1);
            var i2 = new Item(2);
            var i3 = new Item(3);
            b.Submit(i1, 30);
            b.Submit(i2, 500); // over the cap on its own: waits for the budget to empty
            b.Submit(i3, 20);

            started.Should().Equal(1);

            b.Release(i1);
            started.Should().Equal(1, 2); // admitted alone; 3 waits behind it
            b.InFlightBytes.Should().Be(500);
            b.Waiting.Should().Be(1);

            b.Release(i2);
            started.Should().Equal(1, 2, 3);
            b.Release(i3);
            b.InFlightBytes.Should().Be(0);
        }

        [Fact]
        public void An_Oversized_Item_Starts_At_Once_When_Nothing_Is_In_Flight()
        {
            var (b, started) = Make(100);
            b.Submit(new Item(1), 500);

            started.Should().Equal(1);
        }

        [Fact]
        public void Release_Is_Exactly_Once_Per_Submission()
        {
            var (b, started) = Make(100);
            var a = new Item(1);
            var other = new Item(2);
            b.Submit(a, 40);
            b.Submit(other, 30);

            b.Release(a).Should().BeTrue();
            b.Release(a).Should().BeFalse("the token is spent");
            b.InFlightBytes.Should().Be(30);

            // the item may be submitted again after its release (decoder jobs are pooled), with a fresh token
            b.Submit(a, 50);
            b.InFlightBytes.Should().Be(80);
            b.Release(a).Should().BeTrue();
            b.Release(a).Should().BeFalse();
            b.InFlightBytes.Should().Be(30);

            // a waiting item holds nothing to release
            var big = new Item(3);
            var waiter = new Item(4);
            b.Submit(big, 70);
            b.Submit(waiter, 50);
            b.Release(waiter).Should().BeFalse();
            b.InFlightBytes.Should().Be(100);
        }

        [Fact]
        public void Every_Submitted_Item_Starts_Exactly_Once_In_Order_Under_A_Random_Mix()
        {
            var (b, started) = Make(1000);
            var rnd = new Random(7);
            var items = new List<Item>();
            int released = 0; // items are released in start order

            for (int step = 0; step < 5000; step++)
            {
                if (rnd.Next(2) == 0)
                {
                    long bytes = rnd.Next(10) == 0 ? 3000 : rnd.Next(1, 400);
                    var item = new Item(items.Count);
                    items.Add(item);
                    b.Submit(item, bytes);
                }
                else if (released < started.Count)
                {
                    b.Release(items[started[released++]]).Should().BeTrue();
                }

                // over the cap only when a single oversized item runs alone
                if (b.InFlightBytes > b.CapBytes)
                {
                    (started.Count - released).Should().Be(1);
                }
            }

            while (released < started.Count)
            {
                b.Release(items[started[released++]]);
            }

            started.Should().HaveCount(items.Count);
            started.Should().Equal(System.Linq.Enumerable.Range(0, items.Count));
            b.InFlightBytes.Should().Be(0);
            b.Waiting.Should().Be(0);
        }

        [Fact]
        public void Uncapped_Budget_Only_Counts()
        {
            var (b, started) = Make(0);
            var i1 = new Item(1);
            var i2 = new Item(2);
            b.Submit(i1, 1L << 30);
            b.Submit(i2, 1L << 30);

            started.Should().Equal(1, 2);
            b.PeakBytes.Should().Be(2L << 30);
            b.TakeFramePeak().Should().Be(2L << 30);
            b.TakeFramePeak().Should().Be(2L << 30); // restarts from the current level
            b.Release(i1);
            b.Release(i2);
            b.TakeFramePeak().Should().Be(2L << 30);
            b.TakeFramePeak().Should().Be(0);
        }

        [Fact]
        public void Cancel_Waiting_Removes_Only_That_Owners_Waiters_And_Starts_What_Now_Fits()
        {
            var (b, started) = Make(100);
            object ownerA = new object(), ownerB = new object();
            b.Submit(new Item(1), 90, ownerB);
            b.Submit(new Item(2), 50, ownerA); // waits
            b.Submit(new Item(3), 10, ownerB); // waits behind 2

            var removed = new List<DecodeBudget.Item>();
            b.CancelWaiting(ownerA, removed);

            removed.Should().HaveCount(1);
            ((Item)removed[0]).Id.Should().Be(2);
            started.Should().Equal(new[] { 1, 3 }, "with the head gone, 3 fits");
            b.Waiting.Should().Be(0);
            b.InFlightBytes.Should().Be(100);
        }

        [Fact]
        public void Pooled_Bytes_Match_What_The_Shared_Pool_Rents()
        {
            foreach (int n in new[] { 1, 15, 16, 17, 100, 1000, 1024, 1025, 44 * 44, 128 * 128, 640 * 480, (1 << 20) + 1 })
            {
                uint[] a = ArrayPool<uint>.Shared.Rent(n);
                DecodeBudget.PooledBytes(n).Should().Be(a.Length * 4L, $"Rent({n})");
                ArrayPool<uint>.Shared.Return(a);
            }

            DecodeBudget.PooledBytes(0).Should().Be(0);
        }

        // ---- FirstSightDecoder through the budget, with the thread pool replaced by a list the test runs by hand ----

        private sealed class ManualStarter
        {
            public readonly List<IThreadPoolWorkItem> Started = new List<IThreadPoolWorkItem>();
            private int _run;

            public void Start(IThreadPoolWorkItem item) => Started.Add(item);

            public int NotRun => Started.Count - _run;

            /// <summary>Runs the started items not run yet, on this thread ("the workers finish").</summary>
            public void RunAll()
            {
                while (_run < Started.Count)
                {
                    Started[_run++].Execute();
                }
            }

            public void RunNext() => Started[_run++].Execute();
        }

        private static bool Fill(FirstSightDecoder.Job job)
        {
            job.Width = 4;
            job.Height = 4;
            job.Pixels = ArrayPool<uint>.Shared.Rent(16);

            return true;
        }

        [Fact]
        public void Cancel_With_No_Later_Drain_Leaves_Nothing_Charged()
        {
            // the audit's scenario: cap 64, two 64-byte requests, the first admitted and the second waiting; the first
            // finishes, the scene goes, and nobody ever drains this decoder again
            var starter = new ManualStarter();
            var budget = new DecodeBudget(64, starter.Start);
            var jobs = new List<FirstSightDecoder.Job>();
            int uploads = 0;
            var d = new FirstSightDecoder("art", 8, job =>
            {
                jobs.Add(job);

                return Fill(job);
            }, _ => uploads++, _ => 64, budget);

            d.Request(0);
            d.Request(1);
            starter.Started.Should().HaveCount(1);
            budget.Waiting.Should().Be(1);

            starter.RunAll(); // the first decode finishes and is published
            d.ReadyCount.Should().Be(1);

            d.Cancel();

            budget.InFlightBytes.Should().Be(0);
            budget.Waiting.Should().Be(0);
            starter.Started.Should().HaveCount(1, "the waiting request was taken out, not started stale");
            jobs[0].Pixels.Should().BeNull("its buffer went back to the pool");
            d.ReadyCount.Should().Be(0);
            d.Pending.Should().Be(0);
            d.IsRequested(0).Should().BeFalse();
            d.IsRequested(1).Should().BeFalse();
            uploads.Should().Be(0);
        }

        [Fact]
        public void Cancel_While_One_Is_Admitted_And_One_Waits_Releases_When_The_Worker_Finishes()
        {
            var starter = new ManualStarter();
            var budget = new DecodeBudget(128, starter.Start);
            var other = new FirstSightDecoder("texmap", 8, Fill, _ => { }, _ => 64, budget);
            var jobs = new List<FirstSightDecoder.Job>();
            int uploads = 0;
            FirstSightDecoder d = null;
            d = new FirstSightDecoder("art", 8, job =>
            {
                jobs.Add(job);
                d.Cancel(); // the scene goes while this worker decodes

                return Fill(job);
            }, _ => uploads++, _ => 64, budget);

            other.Request(0); // 64 admitted, owned by another decoder: must stay charged throughout
            d.Request(0);     // 128: admitted
            d.Request(1);     // waits
            budget.InFlightBytes.Should().Be(128);
            budget.Waiting.Should().Be(1);

            starter.RunNext(); // other's decode
            starter.RunNext(); // d's job 0: cancels mid-decode, then publishes stale

            budget.Waiting.Should().Be(0, "the waiter was taken out by the cancel");
            budget.InFlightBytes.Should().Be(64, "the cancelled worker released its own bytes when it handed back the buffer");
            jobs[0].Pixels.Should().BeNull();
            starter.NotRun.Should().Be(0, "nothing stale was started");

            // a later drain only does the bookkeeping, and does not release again
            d.Drain(long.MaxValue);
            budget.InFlightBytes.Should().Be(64);
            d.Pending.Should().Be(0);
            uploads.Should().Be(0);
            d.IsRequested(0).Should().BeFalse();

            other.Drain(long.MaxValue);
            budget.InFlightBytes.Should().Be(0);
        }

        [Fact]
        public void A_Cancelled_Worker_Frees_Room_For_Another_Decoders_Waiter_Without_A_Drain()
        {
            var starter = new ManualStarter();
            var budget = new DecodeBudget(64, starter.Start);
            int otherUploads = 0;
            var art = new FirstSightDecoder("art", 8, Fill, _ => { }, _ => 64, budget);
            var other = new FirstSightDecoder("texmap", 8, Fill, _ => otherUploads++, _ => 64, budget);

            art.Request(0);   // admitted, not run yet
            other.Request(0); // waits behind it
            art.Cancel();
            budget.Waiting.Should().Be(1, "another owner's waiter stays queued");

            starter.RunNext(); // the cancelled art job: skips the decode, releases
            budget.InFlightBytes.Should().Be(64);
            budget.Waiting.Should().Be(0);
            starter.NotRun.Should().Be(1, "the other decoder's request started");

            starter.RunAll();
            other.Drain(long.MaxValue);
            otherUploads.Should().Be(1);
            budget.InFlightBytes.Should().Be(0);
        }

        [Fact]
        public void A_Gump_Is_Never_Blocked_By_An_Art_Waiter()
        {
            var starter = new ManualStarter();
            var budget = new DecodeBudget(64, starter.Start);
            int gumpUploads = 0;
            var art = new FirstSightDecoder("art", 8, Fill, _ => { }, _ => 64, budget);
            var gump = new FirstSightDecoder("gump", 8, Fill, _ => gumpUploads++, bytesOf: null, budget: budget);

            art.Request(0);
            art.Request(1); // waits
            budget.Waiting.Should().Be(1);

            gump.Request(5);
            starter.Started.Should().HaveCount(2, "the gump started at once, past the art waiter");
            budget.Waiting.Should().Be(1);
            budget.InFlightBytes.Should().Be(64, "gumps are not counted");

            starter.RunNext();
            starter.RunNext();
            gump.Drain(long.MaxValue);
            gumpUploads.Should().Be(1);
            budget.InFlightBytes.Should().Be(64);
            budget.Waiting.Should().Be(1, "the art waiter still waits for art's release, not the gump's");

            art.Drain(long.MaxValue);
            budget.Waiting.Should().Be(0);
            starter.RunAll();
            art.Drain(long.MaxValue);
            budget.InFlightBytes.Should().Be(0);
        }

        // ---- the animation lifecycle (Animations' DirectionJob: several pooled buffers per job, drained by drawing,
        // cancelled at scene teardown), on the same DecodeQueue Animations uses ----

        private sealed class DirJob : DecodeBudget.Item
        {
            public DecodeQueue<DirJob> Queue;
            public int Frames;
            public uint[][] Pixels;
            public bool Decoded;

            public override void Execute()
            {
                if (Queue.IsCurrent(this))
                {
                    Pixels = new uint[Frames][];

                    for (int i = 0; i < Frames; i++)
                    {
                        Pixels[i] = ArrayPool<uint>.Shared.Rent(100);
                    }

                    Decoded = true;
                }

                Queue.Publish(this);
            }

            public void ReturnPixels()
            {
                if (Pixels != null)
                {
                    foreach (var p in Pixels)
                    {
                        ArrayPool<uint>.Shared.Return(p);
                    }

                    Pixels = null;
                }
            }
        }

        [Fact]
        public void Animation_Lifecycle_Complete_Cancel_Waiting_Cancel_Running_And_Cancel_Finished()
        {
            var starter = new ManualStarter();
            var budget = new DecodeBudget(5 * 512, starter.Start); // two directions fit, the third waits
            var q = new DecodeQueue<DirJob>(budget, j => j.ReturnPixels());
            var retired = new List<DirJob>();
            long perJob = 2 * DecodeBudget.PooledBytes(100); // 2 frames of 100 pixels = 2 x 512 bytes

            DirJob Submit()
            {
                var j = new DirJob { Queue = q, Frames = 2 };
                q.Submit(j, perJob, counted: true);

                return j;
            }

            // normal: decode, drain (upload), release once
            var done = Submit();
            starter.RunAll();
            q.TryDequeue(out var got).Should().BeTrue();
            got.Should().BeSameAs(done);
            done.ReturnPixels();
            q.Release(done);
            q.Release(done);
            budget.InFlightBytes.Should().Be(0);

            // cancel: one finished (published, not drawn yet), one running, one waiting
            var finished = Submit();
            var running = Submit();
            var waiting = Submit();
            budget.Waiting.Should().Be(1);
            starter.RunNext(); // finished's worker

            q.Cancel(retired.Add);

            retired.Should().HaveCount(2).And.Contain(waiting).And.Contain(finished);
            finished.Pixels.Should().BeNull();
            budget.Waiting.Should().Be(0);
            budget.InFlightBytes.Should().Be(perJob, "only the running direction is still charged");

            // the running worker finishes after the cancel, and nothing ever draws again: it hands back everything itself
            starter.RunNext();
            running.Decoded.Should().BeFalse("it saw the cancel before decoding");
            budget.InFlightBytes.Should().Be(0);
            starter.NotRun.Should().Be(0);

            // a later drain finds it stale; releasing again changes nothing
            q.TryDequeue(out var stale).Should().BeTrue();
            q.IsCurrent(stale).Should().BeFalse();
            q.Release(stale);
            budget.InFlightBytes.Should().Be(0);
        }

        [Fact]
        public void Animation_Worker_Cancelled_Mid_Decode_Returns_Its_Frames_And_Bytes()
        {
            var starter = new ManualStarter();
            var budget = new DecodeBudget(0, starter.Start);
            var q = new DecodeQueue<DirJob>(budget, j => j.ReturnPixels());
            var job = new DirJob { Queue = q, Frames = 3 };
            q.Submit(job, 3 * DecodeBudget.PooledBytes(100), counted: true);

            q.BeforePublishForTests = () => q.Cancel(_ => { }); // teardown lands between the decode and the publish
            starter.RunAll();

            job.Decoded.Should().BeTrue();
            job.Pixels.Should().BeNull("the worker returned its frames");
            budget.InFlightBytes.Should().Be(0);
            q.ReadyCount.Should().Be(1, "the empty job still reaches the owner's bookkeeping");
        }

        // ---- real thread pool ----

        private static void DrainUntil(FirstSightDecoder d, Func<bool> done)
        {
            var sw = Stopwatch.StartNew();

            while (!done() && sw.ElapsedMilliseconds < 10000)
            {
                d.Drain(long.MaxValue);
                Thread.Sleep(1);
            }
        }

        [Fact]
        public void Decoder_Keeps_In_Flight_Bytes_Under_The_Cap_And_Uploads_Everything()
        {
            var budget = new DecodeBudget(10 * 64);
            int uploads = 0;
            long peakSeen = 0;
            FirstSightDecoder d = null;
            d = new FirstSightDecoder("test", 200, job =>
            {
                Interlocked.Exchange(ref peakSeen, Math.Max(Interlocked.Read(ref peakSeen), budget.InFlightBytes));

                return Fill(job);
            }, job => uploads++, idx => 64, budget);

            for (uint i = 0; i < 100; i++)
            {
                d.Request(i);
            }

            budget.InFlightBytes.Should().BeLessOrEqualTo(640);
            budget.Waiting.Should().Be(90);

            DrainUntil(d, () => uploads == 100);

            uploads.Should().Be(100);
            d.Pending.Should().Be(0);
            budget.InFlightBytes.Should().Be(0);
            budget.Waiting.Should().Be(0);
            budget.PeakBytes.Should().BeLessOrEqualTo(640);
            peakSeen.Should().BeLessOrEqualTo(640);
        }

        [Fact]
        public void Failed_Decodes_Release_Their_Bytes()
        {
            var budget = new DecodeBudget(128);
            int uploads = 0;
            var d = new FirstSightDecoder("test", 50, job => job.Index % 2 == 0 ? false : throw new InvalidOperationException("boom"),
                job => uploads++, idx => 64, budget);

            for (uint i = 0; i < 20; i++)
            {
                d.Request(i);
            }

            DrainUntil(d, () => d.Pending == 0);

            d.Pending.Should().Be(0);
            uploads.Should().Be(0);
            budget.InFlightBytes.Should().Be(0);
            budget.Waiting.Should().Be(0);
        }

        [Fact]
        public void Cancel_On_The_Thread_Pool_Settles_Without_A_Drain()
        {
            // the earlier version of this test drained after the cancel, which hid stranded charges: here nothing drains
            var budget = new DecodeBudget(128);
            int decodes = 0;
            var d = new FirstSightDecoder("test", 50, job =>
            {
                Interlocked.Increment(ref decodes);

                return Fill(job);
            }, _ => { }, idx => 64, budget);

            for (uint i = 0; i < 20; i++)
            {
                d.Request(i);
            }

            d.Cancel();
            budget.Waiting.Should().Be(0, "every waiting request was taken out");

            var sw = Stopwatch.StartNew();

            while (budget.InFlightBytes != 0 && sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(1);
            }

            budget.InFlightBytes.Should().Be(0, "the running workers released their bytes themselves");
            decodes.Should().BeLessOrEqualTo(2);
        }
    }
}
