using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // perf-4 (Codex audit of 496cdd056): FirstSightDecoder.RequestAndWait, the wait behind Art.Prefetch (container and
    // paperdoll slot art). It must not wait when the budget can't admit the batch (bytes held by owners only the main
    // thread drains), must give up when nothing finishes within a stall slice, and must start no upload after its deadline.
    public class FirstSightDecoderWaitTests
    {
        private static bool Fill(FirstSightDecoder.Job job)
        {
            job.Width = 2;
            job.Height = 2;
            job.Pixels = ArrayPool<uint>.Shared.Rent(4);

            return true;
        }

        // a thread per decode: the suite's parallel tests can starve the thread pool longer than a stall slice
        private static void OwnThread(System.Threading.IThreadPoolWorkItem item) => new Thread(item.Execute) { IsBackground = true }.Start();

        private static long Ms(int ms) => Stopwatch.GetTimestamp() + Stopwatch.Frequency * ms / 1000;

        // Deadlines and stall slices that only a hang reaches. A loaded machine can leave a below-normal thread without CPU
        // for seconds (a 2 ms stall slice measured 1.8 s), so a wait that should succeed gets these, and a wait that
        // should end early is told apart from one that runs out by the deadline or slice it did not reach.
        private static readonly int GuardMs = (int)DecodeWait.HangGuard.TotalMilliseconds;

        [Fact]
        public void Does_Not_Wait_When_The_Budget_Cannot_Admit_The_Batch()
        {
            // admitted items never run: the bytes stay held, as by finished decodes of an owner nobody drains meanwhile
            var budget = new DecodeBudget(100, _ => { });
            var other = new FirstSightDecoder("other", 8, Fill, _ => { }, _ => 80, budget);
            other.Request(1);
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 8, Fill, j => uploads.Add(j.Index), _ => 80, budget);

            // nothing ever publishes here, so a wait would last a whole stall slice (the guard) before giving up
            var sw = Stopwatch.StartNew();
            int pending = d.RequestAndWait(new uint[] { 2, 3 }, Ms(2 * GuardMs), stallMs: GuardMs);

            sw.ElapsedMilliseconds.Should().BeLessThan(GuardMs, "a batch that can't start must not block the main thread");
            pending.Should().Be(2);
            d.IsRequested(2).Should().BeTrue("the requests stay queued");
            d.IsRequested(3).Should().BeTrue();
            uploads.Should().BeEmpty();
        }

        [Fact]
        public void Gives_Up_When_Nothing_Finishes_Within_A_Stall_Slice()
        {
            // unlimited budget, but the workers never run the decodes (busy elsewhere)
            var budget = new DecodeBudget(0, _ => { });
            var d = new FirstSightDecoder("art", 8, Fill, _ => { }, _ => 16, budget);

            // returning before the deadline means the stall slice ended the wait (at the deadline, now >= deadline)
            long deadline = Ms(GuardMs);
            int pending = d.RequestAndWait(new uint[] { 2, 3 }, deadline, stallMs: 2);

            Stopwatch.GetTimestamp().Should().BeLessThan(deadline, "one stall slice without progress ends the wait");
            pending.Should().Be(2);
        }

        [Fact]
        public void Waits_For_The_Batch_And_Uploads_It()
        {
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 64, Fill, j => uploads.Add(j.Index), _ => 16, new DecodeBudget(1L << 20, OwnThread));
            var batch = new uint[] { 5, 6, 7, 8 };

            int pending = d.RequestAndWait(batch, Ms(GuardMs), stallMs: GuardMs);

            pending.Should().Be(0);
            uploads.Should().BeEquivalentTo(batch);
        }

        [Fact]
        public void Starts_No_Upload_After_The_Deadline()
        {
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 64, Fill, j => uploads.Add(j.Index), _ => 16, new DecodeBudget(1L << 20, OwnThread));
            d.Request(5);
            d.Request(6);
            WaitReady(d, 2);

            int pending = d.RequestAndWait(new uint[] { 5, 6 }, Stopwatch.GetTimestamp() - 1, stallMs: 2);

            pending.Should().Be(2);
            uploads.Should().BeEmpty("Drain's at-least-one upload must not apply to the prefetch wait");
        }

        // decode-stall: under load a normal-priority thread whose wait ends queues behind the machine's other runnable
        // threads (a 2 ms slice measured a 600 ms median). The wait runs above normal priority so its timeout or the
        // worker's signal preempts them, and the caller's priority is restored afterwards (DecodeQueue.BoostWaits).
        [Fact]
        public void Waits_Above_Normal_Priority_And_Restores_It()
        {
            // the kill switch (CUO_DECODE_WAIT_BOOST=0) must not decide this test: boost explicitly, restore after
            bool boost = DecodeQueue<FirstSightDecoder.Job>.BoostWaits;
            DecodeQueue<FirstSightDecoder.Job>.BoostWaits = true;

            try
            {
                WaitAboveNormal();
            }
            finally
            {
                DecodeQueue<FirstSightDecoder.Job>.BoostWaits = boost;
            }
        }

        private void WaitAboveNormal()
        {
            var started = new ConcurrentQueue<IThreadPoolWorkItem>();
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 8, Fill, j => uploads.Add(j.Index), _ => 16, new DecodeBudget(0, started.Enqueue));
            int pending = -1;
            var after = ThreadPriority.Lowest;
            var main = new Thread(() =>
            {
                pending = d.RequestAndWait(new uint[] { 2 }, Ms(GuardMs), stallMs: GuardMs);
                after = Thread.CurrentThread.Priority;
            }) { IsBackground = true, Priority = ThreadPriority.Normal };
            main.Start();

            AwaitBlocked(main);
            main.Priority.Should().Be(ThreadPriority.AboveNormal, "the blocked wait runs above normal priority");

            // the worker's publish ends the wait
            started.TryDequeue(out var item).Should().BeTrue();
            item.Execute();
            main.Join(DecodeWait.HangGuard).Should().BeTrue();

            pending.Should().Be(0);
            uploads.Should().Equal(2u);
            after.Should().Be(ThreadPriority.Normal, "the caller's priority is restored");
        }

        [Fact]
        public void A_Caller_Already_Above_Normal_Keeps_Its_Priority()
        {
            var started = new ConcurrentQueue<IThreadPoolWorkItem>();
            var d = new FirstSightDecoder("art", 8, Fill, _ => { }, _ => 16, new DecodeBudget(0, started.Enqueue));
            var after = ThreadPriority.Lowest;
            var main = new Thread(() =>
            {
                d.RequestAndWait(new uint[] { 2 }, Ms(GuardMs), stallMs: GuardMs);
                after = Thread.CurrentThread.Priority;
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            main.Start();

            AwaitBlocked(main);
            main.Priority.Should().Be(ThreadPriority.Highest);

            started.TryDequeue(out var item).Should().BeTrue();
            item.Execute();
            main.Join(DecodeWait.HangGuard).Should().BeTrue();
            after.Should().Be(ThreadPriority.Highest);
        }

        // blocked in the wait (ManualResetEventSlim spins briefly before it blocks)
        private static void AwaitBlocked(Thread t)
        {
            var sw = Stopwatch.StartNew();

            while ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            {
                sw.Elapsed.Should().BeLessThan(DecodeWait.HangGuard, "the wait never blocks");
                Thread.Sleep(1);
            }
        }

        private static void WaitReady(FirstSightDecoder d, int count)
        {
            DecodeWait.Until(d, () => d.ReadyCount >= count, drain: false);
            d.ReadyCount.Should().Be(count);
        }

        // Codex round 2 (6f84ce836), P2: a blocked budget must not skip uploads of batch sprites that already finished
        [Fact]
        public void Uploads_Finished_Batch_Sprites_Even_When_Admission_Is_Blocked()
        {
            var budget = new DecodeBudget(100, OwnThread);
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 8, Fill, j => uploads.Add(j.Index), _ => 16, budget);
            d.Request(2);
            d.Request(3);
            WaitReady(d, 2);

            // another owner's decode waits for room: 32 held by the finished art + 80 > 100
            var other = new FirstSightDecoder("other", 8, Fill, _ => { }, _ => 80, budget);
            other.Request(1);
            budget.Waiting.Should().Be(1);

            int pending = d.RequestAndWait(new uint[] { 2, 3 }, Ms(GuardMs), stallMs: GuardMs);

            pending.Should().Be(0);
            uploads.Should().BeEquivalentTo(new uint[] { 2, 3 });
        }

        // ... and admission is reassessed after that drain: releasing the finished art's bytes admits the waiting decode,
        // and the batch's new sprite fits beside it
        [Fact]
        public void Reassesses_Admission_After_Draining()
        {
            var budget = new DecodeBudget(100, OwnThread);
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 8, Fill, j => uploads.Add(j.Index), _ => 16, budget);
            d.Request(2);
            d.Request(3);
            WaitReady(d, 2);
            var other = new FirstSightDecoder("other", 8, Fill, _ => { }, _ => 80, budget);
            other.Request(1);
            budget.Waiting.Should().Be(1);

            int pending = d.RequestAndWait(new uint[] { 2, 3, 4 }, Ms(GuardMs), stallMs: GuardMs);

            pending.Should().Be(0);
            uploads.Should().BeEquivalentTo(new uint[] { 2, 3, 4 });
        }

        // Codex round 2, P3: the wait's uploads count as upload time (Art adds DrainTicks to FrameUploadTicks)
        [Fact]
        public void Counts_The_Upload_Time_Of_The_Wait()
        {
            long oneMs = Stopwatch.Frequency / 1000;
            var d = new FirstSightDecoder("art", 64, Fill, _ =>
            {
                long end = Stopwatch.GetTimestamp() + oneMs;

                while (Stopwatch.GetTimestamp() < end)
                {
                }
            }, _ => 16, new DecodeBudget(1L << 20, OwnThread));
            long before = d.DrainTicks;

            int pending = d.RequestAndWait(new uint[] { 5, 6, 7 }, Ms(GuardMs), stallMs: GuardMs);

            pending.Should().Be(0);
            (d.DrainTicks - before).Should().BeGreaterOrEqualTo(3 * oneMs);
        }

        [Fact]
        public void Drain_Without_At_Least_One_Starts_Nothing_After_The_Deadline()
        {
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 64, Fill, j => uploads.Add(j.Index), _ => 16, new DecodeBudget(1L << 20, OwnThread));
            d.Request(5);
            WaitReady(d, 1);

            d.Drain(Stopwatch.GetTimestamp() - 1, atLeastOne: false);
            uploads.Should().BeEmpty();

            d.Drain(Stopwatch.GetTimestamp() - 1);
            uploads.Should().Equal(5u);
        }
    }
}
