using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // perf-4, Codex rounds 2-3: the art frame allowance (art_container_prefetch) bounds the chunk loader's upload drain
    // while active and is valid only in the frame that granted it; inactive (switch off), the drain keeps its deadline.
    public class FrameAllowanceTests
    {
        [Fact]
        public void Inactive_Leaves_The_Drain_Deadline_Unchanged()
        {
            var a = new FrameAllowance();

            a.IsActive.Should().BeFalse();
            a.Remaining.Should().Be(0);
            a.TryClampDrain(5000, 1000, out long clamped).Should().BeTrue();
            clamped.Should().Be(5000);
        }

        [Fact]
        public void Active_Clamps_The_Drain_To_What_Is_Left()
        {
            var a = new FrameAllowance();
            a.Begin(400);
            a.Spend(100);

            a.TryClampDrain(5000, 1000, out long clamped).Should().BeTrue();
            clamped.Should().Be(1300);
            a.TryClampDrain(1200, 1000, out clamped).Should().BeTrue();
            clamped.Should().Be(1200, "an earlier chunk deadline stays");
        }

        [Fact]
        public void Spent_Allowance_Starts_No_Upload()
        {
            var a = new FrameAllowance();
            a.Begin(400);
            a.Spend(450);

            a.Remaining.Should().Be(0);
            a.TryClampDrain(5000, 1500, out _).Should().BeFalse("the unconditional first upload must not apply once it is spent");
        }

        // Codex round 3 (bc8cfe0cd), P2: validity follows the frame boundary, not elapsed time
        [Fact]
        public void A_Stale_Frame_Is_Inactive()
        {
            var a = new FrameAllowance();
            a.Begin(400);
            a.AdvanceFrame();

            a.IsActive.Should().BeFalse("nobody renewed it this frame (login, switch turned off)");
            a.Remaining.Should().Be(0);
            a.TryClampDrain(9000, 1001, out long clamped).Should().BeTrue();
            clamped.Should().Be(9000);
        }

        [Fact]
        public void Exhausted_Stays_Exhausted_Within_The_Frame_However_Long_It_Takes()
        {
            var a = new FrameAllowance();
            a.Begin(400);
            a.Spend(500);

            a.TryClampDrain(1_000_000_000, 1_000_000, out _).Should().BeFalse("a long pause must not restore unrestricted draining");
        }

        [Fact]
        public void Clear_Makes_It_Inactive()
        {
            var a = new FrameAllowance();
            a.Begin(400);
            a.Clear();

            a.IsActive.Should().BeFalse();
            a.Remaining.Should().Be(0);
        }

        [Fact]
        public void Renewed_Each_Frame()
        {
            var a = new FrameAllowance();
            a.Begin(400);
            a.Spend(500);
            a.AdvanceFrame();
            a.Begin(400);

            a.Remaining.Should().Be(400);
        }
    }
}
