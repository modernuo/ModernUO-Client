using System.Collections.Generic;
using System.Threading;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Gumps;
using FluentAssertions;
using Uo.Buffers;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // switch gump_decode_budget: on, gump worker decodes are counted in their budget (and wait for room there); off, they
    // bypass every budget, as before. CUO_GUMP_BUDGET_CHECK's comparison is exercised in both states.
    [Collection(nameof(GumpBudgetCheckCollection))]
    public class GumpDecodeBudgetTests
    {
        private sealed class ManualStarter
        {
            public readonly List<IThreadPoolWorkItem> Started = new List<IThreadPoolWorkItem>();
            private int _run;

            public void Start(IThreadPoolWorkItem item) => Started.Add(item);

            public void RunAll()
            {
                while (_run < Started.Count)
                {
                    Started[_run++].Execute();
                }
            }
        }

        // a 100 x 100 gump: 10,000 pixels, 40,000 bytes (pooled: 16,384 x 4 = 64 KiB)
        private static bool Decode(FirstSightDecoder.Job job)
        {
            job.Width = 100;
            job.Height = 100;
            job.Pixels = BufferPool.RentShared<uint>(10_000);

            return true;
        }

        private static long BytesOf(uint idx) => DecodeBudget.PooledBytes(10_000);

        [Fact]
        public void On_Counts_Decodes_And_Waits_For_Room()
        {
            var starter = new ManualStarter();
            var budget = new DecodeBudget(2 * BytesOf(0), starter.Start);
            int uploads = 0;
            var d = Gump.CreateDecoder(8, Decode, _ => uploads++, BytesOf, budget);

            d.CountedIn.Should().BeSameAs(budget);

            for (uint i = 0; i < 3; i++)
            {
                d.Request(i);
            }

            starter.Started.Should().HaveCount(2, "the third waits for room");
            budget.Waiting.Should().Be(1);
            budget.InFlightBytes.Should().Be(2 * BytesOf(0));

            starter.RunAll();
            d.HeldBytes.Should().Be(2 * BytesOf(0), "held bytes are exactly what was admitted");

            GumpBudgetCheck.ResetForTests();
            GumpBudgetCheck.Check(d.HeldBytes, d.CountedIn, out string mismatch);
            mismatch.Should().BeNull();

            d.Drain(long.MaxValue);
            starter.RunAll();
            d.Drain(long.MaxValue);

            uploads.Should().Be(3);
            budget.InFlightBytes.Should().Be(0);
            d.HeldBytes.Should().Be(0);
        }

        [Fact]
        public void Off_Bypasses_Every_Budget()
        {
            DecodeBudget shared = DecodeBudget.Shared;
            long sharedBefore = shared.InFlightBytes;
            var d = Gump.CreateDecoder(8, Decode, _ => { }, null);

            d.CountedIn.Should().BeNull();
            d.Request(0);

            // not counted: the request went straight to the thread pool; wait for its decode
            SpinWait.SpinUntil(() => d.HasReady, 5000).Should().BeTrue();
            shared.InFlightBytes.Should().Be(sharedBefore);
            DecodeBudget.Gumps.InFlightBytes.Should().Be(0);
            d.HeldBytes.Should().Be(BytesOf(0));

            GumpBudgetCheck.ResetForTests();
            GumpBudgetCheck.Check(d.HeldBytes, d.CountedIn, out string mismatch);
            mismatch.Should().BeNull("off has nothing to compare against");

            d.Drain(long.MaxValue);
            d.HeldBytes.Should().Be(0);
        }

        [Fact]
        public void Check_Reports_Held_Above_Admitted()
        {
            var budget = new DecodeBudget(1 << 20, _ => { });
            GumpBudgetCheck.ResetForTests();
            GumpBudgetCheck.Check(4096, budget, out string mismatch);
            mismatch.Should().Contain("held 4096 B > admitted 0 B");
        }
    }

    [CollectionDefinition(nameof(GumpBudgetCheckCollection), DisableParallelization = true)]
    public class GumpBudgetCheckCollection
    {
    }
}
