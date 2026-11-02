using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Arts;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // perf-5: Art.Get takes a finished worker decode of the sprite it needs (FirstSightDecoder.TryTakeReady) instead of
    // decoding it again; the taken result releases its budget bytes and buffer once and is not uploaded again by Drain.
    public class FirstSightDecoderTakeTests
    {
        private static bool Fill(FirstSightDecoder.Job job)
        {
            job.Width = 2;
            job.Height = 2;
            job.Pixels = ArrayPool<uint>.Shared.Rent(4);

            return true;
        }

        // the budget starts nothing: the test runs each decode when it wants it finished
        private static (FirstSightDecoder Decoder, DecodeBudget Budget, List<IThreadPoolWorkItem> Started, List<uint> Uploads) Make(
            Func<FirstSightDecoder.Job, bool> decode = null)
        {
            var started = new List<IThreadPoolWorkItem>();
            var budget = new DecodeBudget(1L << 20, started.Add);
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("art", 64, decode ?? Fill, j => uploads.Add(j.Index), _ => 16, budget);

            return (d, budget, started, uploads);
        }

        [Fact]
        public void Takes_A_Finished_Decode_Once_And_Drain_Does_Not_Upload_It_Again()
        {
            var (d, budget, started, uploads) = Make();
            d.Request(5);
            started[0].Execute();

            d.TryTakeReady(5).Should().BeTrue();

            uploads.Should().Equal(5u);
            d.IsRequested(5).Should().BeFalse();
            d.Pending.Should().Be(0);
            d.Completed.Should().Be(1);
            d.TakenReady.Should().Be(1);
            budget.InFlightBytes.Should().Be(0, "the taken decode gives its bytes back");

            d.Drain(long.MaxValue);

            uploads.Should().Equal(new uint[] { 5 }, "the drain only retires the taken job");
            d.Pending.Should().Be(0);
            d.Completed.Should().Be(1);
            d.HasReady.Should().BeFalse();

            d.Request(5);
            d.Pending.Should().Be(1, "the sprite can be requested again");
        }

        // Codex audit P2: the take's upload is main-thread art time; while the frame allowance is active it is charged
        // there, so a later Prefetch or drain in the same frame sees it spent
        [Fact]
        public void A_Take_Spends_The_Active_Frame_Allowance()
        {
            var started = new List<IThreadPoolWorkItem>();
            long uploadTicks = System.Diagnostics.Stopwatch.Frequency / 200; // 5 ms
            var d = new FirstSightDecoder("art", 64, Fill, _ =>
            {
                long end = System.Diagnostics.Stopwatch.GetTimestamp() + uploadTicks;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < end) { }
            }, _ => 16, new DecodeBudget(1L << 20, started.Add));
            d.Request(5);
            started[0].Execute();
            var allowance = new FrameAllowance();
            long granted = System.Diagnostics.Stopwatch.Frequency; // 1 s
            allowance.Begin(granted);

            d.TryTakeReady(5, allowance).Should().BeTrue();

            allowance.Remaining.Should().BeLessOrEqualTo(granted - uploadTicks);
        }

        [Fact]
        public void A_Take_Leaves_An_Inactive_Allowance_Alone()
        {
            var (d, _, started, uploads) = Make();
            d.Request(5);
            started[0].Execute();
            var allowance = new FrameAllowance();

            d.TryTakeReady(5, allowance).Should().BeTrue();

            uploads.Should().Equal(5u);
            allowance.IsActive.Should().BeFalse();
            allowance.Remaining.Should().Be(0);
        }

        [Fact]
        public void Does_Not_Take_A_Decode_Still_In_Flight()
        {
            var (d, _, started, uploads) = Make();
            d.Request(5);

            d.TryTakeReady(5).Should().BeFalse();
            d.IsRequested(5).Should().BeTrue();
            d.Pending.Should().Be(1);

            started[0].Execute();
            d.Drain(long.MaxValue);
            uploads.Should().Equal(5u);
            d.Pending.Should().Be(0);
        }

        [Fact]
        public void Does_Not_Take_Another_Sprite_Or_A_Failed_Decode()
        {
            var (d, _, started, uploads) = Make(job => job.Index != 7 && Fill(job));
            d.Request(5);
            d.Request(7);
            started[0].Execute();
            started[1].Execute();

            d.TryTakeReady(6).Should().BeFalse("never requested");
            d.TryTakeReady(7).Should().BeFalse("nothing to upload: the lookup decodes as before");
            uploads.Should().BeEmpty();

            d.Drain(long.MaxValue);
            uploads.Should().Equal(5u);
            d.IsFailed(7).Should().BeTrue();
            d.Pending.Should().Be(0);
        }

        [Fact]
        public void A_Taken_Job_Does_Not_Use_Up_The_Drains_At_Least_One_Upload()
        {
            var (d, _, started, uploads) = Make();
            d.Request(5);
            d.Request(6);
            started[0].Execute();
            started[1].Execute();
            d.TryTakeReady(5).Should().BeTrue();

            d.Drain(0); // deadline passed: one upload still happens

            uploads.Should().Equal(5u, 6u);
            d.Pending.Should().Be(0);
            d.Completed.Should().Be(2);
        }

        [Fact]
        public void Cancel_After_A_Take_Retires_The_Job_Without_Counting_It_Twice()
        {
            var (d, budget, started, uploads) = Make();
            d.Request(5);
            started[0].Execute();
            d.TryTakeReady(5).Should().BeTrue();

            d.Cancel();

            d.Pending.Should().Be(0);
            budget.InFlightBytes.Should().Be(0);
            d.HasReady.Should().BeFalse();
            uploads.Should().Equal(5u);

            d.Request(5);
            d.IsRequested(5).Should().BeTrue();
            d.Pending.Should().Be(1);
        }
    }

    // perf-7: Gump.GetGump takes only the gump it needs (Gump.TakeReady over FirstSightDecoder.TryTakeReady) instead of
    // draining the whole ready queue to reach it; the other finished decodes stay for DrainReady and are uploaded once there.
    public class GumpReadyTakeTests
    {
        private static bool Fill(FirstSightDecoder.Job job)
        {
            job.Width = 2;
            job.Height = 2;
            job.Pixels = ArrayPool<uint>.Shared.Rent(4);

            return true;
        }

        // as Gump's decoder: no byte counting (bytesOf null); the test runs each decode when it wants it finished
        private static (FirstSightDecoder Decoder, List<IThreadPoolWorkItem> Started, List<uint> Uploads) Make(Action<uint> onUpload = null)
        {
            var started = new List<IThreadPoolWorkItem>();
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("gump", 64, Fill, j =>
            {
                uploads.Add(j.Index);
                onUpload?.Invoke(j.Index);
            }, null, new DecodeBudget(1L << 20, started.Add));

            return (d, started, uploads);
        }

        [Fact]
        public void GetGump_Takes_Only_Its_Gump_And_The_Others_Are_Drained_Later()
        {
            var (d, started, uploads) = Make();
            d.Request(3);
            d.Request(5);
            d.Request(6);

            foreach (IThreadPoolWorkItem w in started)
            {
                w.Execute();
            }

            // the needed gump (5) is behind 3 in the ready queue: the old loop uploaded 3 to reach it
            ClassicUO.Renderer.Gumps.Gump.TakeReady(d, 5).Should().BeTrue();

            uploads.Should().Equal(new uint[] { 5 }, "only the gump GetGump needs");
            d.IsRequested(5).Should().BeFalse();
            d.IsRequested(3).Should().BeTrue("3 and 6 wait for the normal drain");
            d.IsRequested(6).Should().BeTrue();
            d.HasReady.Should().BeTrue();

            d.Drain(long.MaxValue);

            uploads.Should().Equal(new uint[] { 5, 3, 6 }, "the drain uploads the others once, and not 5 again");
            d.Pending.Should().Be(0);
            d.Completed.Should().Be(3);
            d.HasReady.Should().BeFalse();
        }

        [Fact]
        public void GetGump_Does_Not_Take_Or_Drain_When_Its_Gump_Is_Not_Finished()
        {
            var (d, started, uploads) = Make();
            d.Request(3);
            d.Request(5);
            started[0].Execute(); // 3 finished, 5 still running

            ClassicUO.Renderer.Gumps.Gump.TakeReady(d, 5).Should().BeFalse("GetGump decodes 5 on the spot, as before");
            uploads.Should().BeEmpty("3 is not uploaded by GetGump");
            d.IsRequested(3).Should().BeTrue();

            d.Drain(long.MaxValue);
            uploads.Should().Equal(3u);
        }

        [Fact]
        public void A_Take_Charges_The_Gump_Upload_Time()
        {
            long uploadTicks = System.Diagnostics.Stopwatch.Frequency / 500; // 2 ms
            var (d, started, _) = Make(_ =>
            {
                long end = System.Diagnostics.Stopwatch.GetTimestamp() + uploadTicks;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < end) { }
            });
            d.Request(5);
            started[0].Execute();
            long before = ClassicUO.Renderer.Gumps.Gump.FrameUploadTicks;

            ClassicUO.Renderer.Gumps.Gump.TakeReady(d, 5).Should().BeTrue();

            (ClassicUO.Renderer.Gumps.Gump.FrameUploadTicks - before).Should().BeGreaterThanOrEqualTo(uploadTicks);
        }

        // perf-8: Gump.Prefetch's wait takes only its batch's finished decodes; another window's finished decodes stay for
        // DrainReady (the old wait drained the whole ready queue)
        [Fact]
        public void Prefetch_Wait_Takes_Only_The_Batch()
        {
            var (d, started, uploads) = Make();
            d.Request(3);
            d.Request(5);
            d.Request(6);
            d.Request(9);

            foreach (IThreadPoolWorkItem w in started)
            {
                w.Execute();
            }

            ClassicUO.Renderer.Gumps.Gump.AwaitBatch(d, new uint[] { 6, 5 }, long.MaxValue);

            uploads.Should().Equal(new uint[] { 6, 5 }, "only the batch's gumps");
            d.IsRequested(3).Should().BeTrue("3 and 9 wait for the normal drain");
            d.IsRequested(9).Should().BeTrue();

            d.Drain(long.MaxValue);

            uploads.Should().Equal(new uint[] { 6, 5, 3, 9 }, "the drain uploads the others once");
            d.Pending.Should().Be(0);
        }

        [Fact]
        public void Prefetch_Wait_Stops_At_A_Failed_Decode_And_Leaves_It_To_The_Drain()
        {
            var started = new List<IThreadPoolWorkItem>();
            var uploads = new List<uint>();
            var d = new FirstSightDecoder("gump", 64, job => job.Index != 7 && Fill(job), j => uploads.Add(j.Index), null,
                new DecodeBudget(1L << 20, started.Add));
            d.Request(5);
            d.Request(7);

            foreach (IThreadPoolWorkItem w in started)
            {
                w.Execute();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ClassicUO.Renderer.Gumps.Gump.AwaitBatch(d, new uint[] { 5, 7 },
                System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 10);

            sw.ElapsedMilliseconds.Should().BeLessThan(5000, "a finished failed decode is not waited for");
            uploads.Should().Equal(5u);
            d.IsRequested(7).Should().BeTrue();

            d.Drain(long.MaxValue);
            d.IsFailed(7).Should().BeTrue();
            d.Pending.Should().Be(0);
        }

        // perf-9: Prefetch's wait blocks for the next finished decode (no Thread.Yield spin). Decodes already finished and left
        // for the drain must not end that wait, or it would spin as before.
        [Fact]
        public void A_Wait_For_A_New_Decode_Ignores_Those_Waiting_For_A_Drain()
        {
            var (d, started, _) = Make();
            d.Request(3);
            d.Request(5);
            started[0].Execute(); // 3 finished, left for the drain
            int seen = d.PublishedCount;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            d.WaitPublishedSince(seen, 60).Should().BeFalse("nothing finished since");
            sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(40, "it blocked instead of returning on 3");
            d.HasReady.Should().BeTrue();

            started[1].Execute();
            sw.Restart();
            d.WaitPublishedSince(seen, 5000).Should().BeTrue();
            sw.ElapsedMilliseconds.Should().BeLessThan(1000);
        }

        [Fact]
        public void Prefetch_Wait_Wakes_When_A_Worker_Finishes_The_Batch()
        {
            var (d, started, uploads) = Make();
            d.Request(3);
            d.Request(5);
            started[0].Execute();
            var worker = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(50);
                started[1].Execute();
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            worker.Start();
            ClassicUO.Renderer.Gumps.Gump.AwaitBatch(d, new uint[] { 5 },
                System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 10);
            worker.Join();

            sw.ElapsedMilliseconds.Should().BeLessThan(5000, "the finished decode ends the wait, not the deadline");
            uploads.Should().Equal(5u);
            d.IsRequested(3).Should().BeTrue("3 waits for the normal drain");
        }

        [Fact]
        public void Prefetch_Wait_Gives_Up_At_The_Deadline_And_Takes_What_Finishes()
        {
            var (d, started, uploads) = Make();
            d.Request(5);
            d.Request(6);
            started[0].Execute(); // 6 still running

            ClassicUO.Renderer.Gumps.Gump.AwaitBatch(d, new uint[] { 5, 6 },
                System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 100);

            uploads.Should().Equal(5u);
            d.IsRequested(6).Should().BeTrue();
            d.HasFinished(6).Should().BeFalse();

            started[1].Execute();
            d.HasFinished(6).Should().BeTrue();
            ClassicUO.Renderer.Gumps.Gump.TakeReady(d, 6).Should().BeTrue();
            d.HasFinished(6).Should().BeFalse("taken");
        }
    }

    // perf-5: art missing from the pack gets the placeholder (Get(0)); the miss is remembered per id, so it is logged
    // once instead of every frame.
    [Collection("ArtStatics")]
    public class ArtMissingSpriteTests
    {
        [Fact]
        public void A_Missing_Sprite_Is_Logged_Once()
        {
            var sprites = new SpriteEntry[0x4000 + 0x100];
            var arts = new ArtLoader(new UOFileManager(default, Path.GetTempPath(), null));
            arts.SetTablesForTests(sprites);
            var art = new Art(arts, null, null);
            int before = Art.TotalMissingLogs;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    art.GetArt(0x42);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // no device here: the placeholder (sprite 0, absent in this table) can't go into the atlas
                }
            }

            (Art.TotalMissingLogs - before).Should().Be(1);
        }
    }
}
