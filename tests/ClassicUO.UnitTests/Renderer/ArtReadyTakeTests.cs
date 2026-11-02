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

    // perf-5: art missing from the pack gets the placeholder (Get(0)); the miss is remembered per id, so it is logged
    // once instead of every frame.
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
