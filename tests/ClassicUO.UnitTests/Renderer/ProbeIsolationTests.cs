using System.Collections.Generic;
using ClassicUO.Game;
using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // E12 (CUO_GPU_ITEM_CHECK): what the probe pass relies on to leave no trace in the drawn frame.
    public class ProbeIsolationTests
    {
        private static PendingOverrideQueue.Entry E(int x, uint value = 128) => new PendingOverrideQueue.Entry { X = x, Y = 1, Z = 2, Graphic = 3, IsStatic = true, Value = value };

        [Fact]
        public void Restoring_a_mark_forgets_the_probes_overrides_so_none_is_queued_twice()
        {
            var q = new PendingOverrideQueue(16);
            q.Add(E(1));
            PendingOverrideQueue.Mark mark = q.GetMark();

            // probe pass
            q.Add(E(10));
            q.Add(E(11));
            q.Restore(mark);

            // reference pass queues the same overrides
            q.Add(E(10));
            q.Add(E(11));

            q.Count.Should().Be(3);
            q[0].X.Should().Be(1);
            q[1].X.Should().Be(10);
            q[2].X.Should().Be(11);
            q.Dropped.Should().Be(0);
        }

        [Fact]
        public void The_probe_filling_the_queue_does_not_make_the_reference_drop_overrides()
        {
            var q = new PendingOverrideQueue(4);
            PendingOverrideQueue.Mark mark = q.GetMark();

            for (int i = 0; i < 6; i++)
            {
                q.Add(E(i));
            }

            q.Dropped.Should().Be(2);
            q.Restore(mark);

            for (int i = 0; i < 4; i++)
            {
                q.Add(E(100 + i));
            }

            q.Count.Should().Be(4);
            q.Dropped.Should().Be(0);
            q[3].X.Should().Be(103);
        }

        [Fact]
        public void Restoring_after_a_clear_throws()
        {
            var q = new PendingOverrideQueue(4);
            q.Add(E(1));
            PendingOverrideQueue.Mark mark = q.GetMark();
            q.Clear();

            q.Invoking(x => x.Restore(mark)).Should().Throw<System.InvalidOperationException>();
        }

        [Fact]
        public void The_reference_pass_replays_the_probes_animation_rolls()
        {
            var tape = new List<int>();

            try
            {
                AnimationGroupRules.RollTape = tape;
                AnimationGroupRules.RollMode = 1;
                int a = AnimationGroupRules.Roll(), b = AnimationGroupRules.Roll(), c = AnimationGroupRules.Roll();

                AnimationGroupRules.RollMode = 2;
                AnimationGroupRules.RollTapeAt = 0;
                AnimationGroupRules.RollTapeShort = 0;
                AnimationGroupRules.Roll().Should().Be(a);
                AnimationGroupRules.Roll().Should().Be(b);
                AnimationGroupRules.Roll().Should().Be(c);
                AnimationGroupRules.RollTapeShort.Should().Be(0);

                // a reference pass that rolls more than the probe did is counted
                AnimationGroupRules.Roll();
                AnimationGroupRules.RollTapeShort.Should().Be(1);
                AnimationGroupRules.RollTapeAt.Should().Be(3);
            }
            finally
            {
                AnimationGroupRules.RollMode = 0;
                AnimationGroupRules.RollTape = null;
            }
        }
    }
}
