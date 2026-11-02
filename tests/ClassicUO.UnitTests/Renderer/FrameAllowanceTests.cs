using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // perf-4, Codex round 2 (6f84ce836): the art frame allowance (art_container_prefetch) bounds the chunk loader's upload
    // drain while active and expires with its frame; inactive (switch off), the drain keeps its old deadline.
    public class FrameAllowanceTests
    {
        [Fact]
        public void Inactive_Leaves_The_Drain_Deadline_Unchanged()
        {
            var a = new FrameAllowance();

            a.IsActive(1000).Should().BeFalse();
            a.Remaining(1000).Should().Be(0);
            a.TryClampDrain(5000, 1000, out long clamped).Should().BeTrue();
            clamped.Should().Be(5000);
        }

        [Fact]
        public void Active_Clamps_The_Drain_To_What_Is_Left()
        {
            var a = new FrameAllowance();
            a.Begin(400, now: 1000, lifetimeTicks: 10_000);
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
            a.Begin(400, now: 1000, lifetimeTicks: 10_000);
            a.Spend(450);

            a.Remaining(1500).Should().Be(0);
            a.TryClampDrain(5000, 1500, out _).Should().BeFalse("the unconditional first upload must not apply once it is spent");
        }

        [Fact]
        public void Unused_Allowance_Expires_With_Its_Frame()
        {
            var a = new FrameAllowance();
            a.Begin(400, now: 1000, lifetimeTicks: 2000);

            a.Remaining(2999).Should().Be(400);
            a.IsActive(3000).Should().BeFalse();
            a.Remaining(3000).Should().Be(0, "left over from an earlier frame");
            a.TryClampDrain(9000, 3000, out long clamped).Should().BeTrue();
            clamped.Should().Be(9000);
        }
    }
}
