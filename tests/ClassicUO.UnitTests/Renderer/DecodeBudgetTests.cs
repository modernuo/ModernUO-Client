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
    // room; an oversized item runs alone; nothing is dropped; failed, cancelled and stale decodes give their bytes back.
    public class DecodeBudgetTests
    {
        private sealed class Item : IThreadPoolWorkItem
        {
            public readonly int Id;
            public Item(int id) => Id = id;
            public void Execute() { }
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
            b.Submit(new Item(1), 60);
            b.Submit(new Item(2), 60); // 120 > 100: waits
            b.Submit(new Item(3), 10);  // would fit, but must not overtake 2

            started.Should().Equal(1);
            b.Waiting.Should().Be(2);

            b.Release(60);
            started.Should().Equal(1, 2, 3);
            b.InFlightBytes.Should().Be(70);
            b.Waiting.Should().Be(0);
        }

        [Fact]
        public void An_Oversized_Item_Is_Admitted_Alone_And_Nothing_Starves_Behind_It()
        {
            var (b, started) = Make(100);
            b.Submit(new Item(1), 30);
            b.Submit(new Item(2), 500); // over the cap on its own: waits for the budget to empty
            b.Submit(new Item(3), 20);

            started.Should().Equal(1);

            b.Release(30);
            started.Should().Equal(1, 2); // admitted alone; 3 waits behind it
            b.InFlightBytes.Should().Be(500);
            b.Waiting.Should().Be(1);

            b.Release(500);
            started.Should().Equal(1, 2, 3);
            b.Release(20);
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
        public void Every_Submitted_Item_Starts_Exactly_Once_In_Order_Under_A_Random_Mix()
        {
            var (b, started) = Make(1000);
            var rnd = new Random(7);
            var bytesOf = new List<long>();
            int released = 0; // items are released in start order

            for (int step = 0; step < 5000; step++)
            {
                if (rnd.Next(2) == 0)
                {
                    long bytes = rnd.Next(10) == 0 ? 3000 : rnd.Next(1, 400);
                    bytesOf.Add(bytes);
                    b.Submit(new Item(bytesOf.Count - 1), bytes);
                }
                else if (released < started.Count)
                {
                    b.Release(bytesOf[started[released++]]);
                }

                // over the cap only when a single oversized item runs alone
                if (b.InFlightBytes > b.CapBytes)
                {
                    (started.Count - released).Should().Be(1);
                }
            }

            while (released < started.Count)
            {
                b.Release(bytesOf[started[released++]]);
            }

            started.Should().HaveCount(bytesOf.Count);
            started.Should().Equal(System.Linq.Enumerable.Range(0, bytesOf.Count));
            b.InFlightBytes.Should().Be(0);
            b.Waiting.Should().Be(0);
        }

        [Fact]
        public void Uncapped_Budget_Only_Counts()
        {
            var (b, started) = Make(0);
            b.Submit(new Item(1), 1L << 30);
            b.Submit(new Item(2), 1L << 30);

            started.Should().Equal(1, 2);
            b.PeakBytes.Should().Be(2L << 30);
            b.TakeFramePeak().Should().Be(2L << 30);
            b.TakeFramePeak().Should().Be(2L << 30); // restarts from the current level
            b.Release(1L << 30);
            b.Release(1L << 30);
            b.TakeFramePeak().Should().Be(2L << 30);
            b.TakeFramePeak().Should().Be(0);
        }

        private static bool Fill(FirstSightDecoder.Job job)
        {
            job.Width = 4;
            job.Height = 4;
            job.Pixels = ArrayPool<uint>.Shared.Rent(16);

            return true;
        }

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
        public void Cancel_Releases_Finished_And_Waiting_Decodes()
        {
            var budget = new DecodeBudget(128);
            int uploads = 0;
            int decodes = 0;
            var d = new FirstSightDecoder("test", 50, job =>
            {
                Interlocked.Increment(ref decodes);

                return Fill(job);
            }, job => uploads++, idx => 64, budget);

            for (uint i = 0; i < 20; i++)
            {
                d.Request(i);
            }

            // let the two admitted decodes finish, then cancel with 18 still waiting in the budget
            var sw = Stopwatch.StartNew();

            while (d.ReadyCount < 2 && sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(1);
            }

            d.Cancel();
            DrainUntil(d, () => d.Pending == 0);

            d.Pending.Should().Be(0);
            uploads.Should().Be(0);
            budget.InFlightBytes.Should().Be(0);
            budget.Waiting.Should().Be(0);
            decodes.Should().BeLessThan(20); // waiting requests skip the decode after the cancel
        }
    }
}
