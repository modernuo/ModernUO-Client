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

        [Fact]
        public void Does_Not_Wait_When_The_Budget_Cannot_Admit_The_Batch()
        {
            // admitted items never run: the bytes stay held, as by finished decodes of an owner nobody drains meanwhile
            var budget = new DecodeBudget(100, _ => { });
            var other = new FirstSightDecoder("other", 8, Fill, _ => { }, _ => 80, budget);
            other.Request(1);
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 8, Fill, j => uploads.Add(j.Index), _ => 80, budget);

            var sw = Stopwatch.StartNew();
            int pending = d.RequestAndWait(new uint[] { 2, 3 }, Ms(1000), stallMs: 200);

            sw.ElapsedMilliseconds.Should().BeLessThan(100, "a batch that can't start must not block the main thread");
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

            var sw = Stopwatch.StartNew();
            int pending = d.RequestAndWait(new uint[] { 2, 3 }, Ms(2000), stallMs: 2);

            sw.ElapsedMilliseconds.Should().BeLessThan(500, "one stall slice without progress ends the wait");
            pending.Should().Be(2);
        }

        [Fact]
        public void Waits_For_The_Batch_And_Uploads_It()
        {
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 64, Fill, j => uploads.Add(j.Index), _ => 16, new DecodeBudget(1L << 20, OwnThread));
            var batch = new uint[] { 5, 6, 7, 8 };

            int pending = d.RequestAndWait(batch, Ms(5000), stallMs: 1000);

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
            var sw = Stopwatch.StartNew();

            while (d.ReadyCount < 2 && sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1);
            }

            d.ReadyCount.Should().Be(2);

            int pending = d.RequestAndWait(new uint[] { 5, 6 }, Stopwatch.GetTimestamp() - 1, stallMs: 2);

            pending.Should().Be(2);
            uploads.Should().BeEmpty("Drain's at-least-one upload must not apply to the prefetch wait");
        }
    }
}
