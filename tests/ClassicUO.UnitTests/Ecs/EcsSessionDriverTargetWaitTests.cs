using System;
using System.Reflection;
using ClassicUO.Input;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // The driver's `target` waits for a staff command's cursor (up to TargetCursorWaitMs, retrying every TargetCursorPollMs)
    // instead of failing when the server is slower than the script's fixed wait. The driver is CUO_DEVTOOLS only, so it is
    // reached by reflection and the tests pass vacuously in the release variant.
    public class EcsSessionDriverTargetWaitTests
    {
        private static readonly Type DriverType = typeof(Mouse).Assembly.GetType("ClassicUO.Game.EcsSessionDriver");

        private sealed class Driver
        {
            private readonly object _d;

            public Driver(params string[] lines) =>
                _d = DriverType.GetMethod("FromLines", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { lines });

            public int Pc => (int)DriverType.GetProperty("ProgramCounter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_d);

            public bool Retry(bool targeting, uint now) =>
                (bool)DriverType.GetMethod("RetryForTargetCursor", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_d, new object[] { targeting, now });

            public bool Step(Func<string, bool> execute) =>
                (bool)DriverType.GetMethod("Step", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_d, new object[] { execute });
        }

        private static uint WaitMs => (uint)DriverType.GetField("TargetCursorWaitMs", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();

        [Fact]
        public void Target_Line_Reruns_Until_The_Cursor_Arrives()
        {
            if (DriverType == null)
            {
                return;
            }

            var d = new Driver("target ground 2 2", "log after");
            bool targeting = false;
            uint now = 1000;
            int targetRuns = 0;
            Func<string, bool> exec = line =>
            {
                if (!line.StartsWith("target", StringComparison.Ordinal))
                {
                    return false;
                }

                targetRuns++;
                d.Retry(targeting, now);

                return true;
            };

            d.Step(exec).Should().BeTrue();
            d.Pc.Should().Be(0, "no cursor yet: the target line runs again");

            now += 500;
            d.Step(exec).Should().BeTrue();
            d.Pc.Should().Be(0);

            targeting = true;
            now += 500;
            d.Step(exec).Should().BeTrue();
            d.Pc.Should().Be(1, "the cursor arrived: the script moves on");
            targetRuns.Should().Be(3);
        }

        [Fact]
        public void Target_Line_Gives_Up_After_The_Wait()
        {
            if (DriverType == null)
            {
                return;
            }

            var d = new Driver("target ground 2 2", "log after");

            d.Retry(false, 1000).Should().BeTrue();
            d.Retry(false, 1000 + WaitMs - 1).Should().BeTrue();
            d.Retry(false, 1000 + WaitMs).Should().BeFalse("past the wait the line fails as before (warning, script continues)");

            // a later target line starts its own wait
            d.Retry(false, 50000).Should().BeTrue();
        }

        [Fact]
        public void Cursor_Already_Up_Does_Not_Retry()
        {
            if (DriverType == null)
            {
                return;
            }

            new Driver("target self").Retry(true, 1000).Should().BeFalse();
        }
    }
}
