using ClassicUO.Loop;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // evloop-2: present on change's scheduling (rate-limited from the last present) against the display grid, which still runs
    // where present on change does not (the legacy loop's fallback with the display-rate cap).
    public class PresentOnChangeTests
    {
        private const double Interval = 1000.0 / 60;

        [Fact]
        public void A_Change_After_An_Idle_Interval_Presents_At_Once()
        {
            LoopTiming.MsUntilPresent(true, 40, Interval, double.PositiveInfinity).Should().Be(0);
            LoopTiming.PresentDue(true, 40, Interval, double.PositiveInfinity).Should().BeTrue();
        }

        [Fact]
        public void A_Change_Soon_After_A_Present_Waits_For_The_Interval()
        {
            LoopTiming.MsUntilPresent(true, 5, Interval, double.PositiveInfinity).Should().BeApproximately(Interval - 5, 1e-9);
            LoopTiming.PresentDue(true, Interval - LoopTiming.DrawSlackMs - 0.01, Interval, double.PositiveInfinity).Should().BeFalse();
            LoopTiming.PresentDue(true, Interval - LoopTiming.DrawSlackMs + 0.01, Interval, double.PositiveInfinity).Should().BeTrue();
        }

        [Fact]
        public void A_Packet_Split_Across_Reads_Presents_From_Its_First_Read()
        {
            long partial = 0;
            LoopTiming.DrainChanged(ref partial, 100, 3, 0, false, false, out long from).Should().BeFalse("only a header arrived");
            partial.Should().Be(100);
            LoopTiming.DrainChanged(ref partial, 200, 0, 0, false, false, out _).Should().BeFalse();
            LoopTiming.DrainChanged(ref partial, 300, 40, 1, true, false, out from).Should().BeTrue();
            from.Should().Be(100, "the oldest read that contributed to the dispatched packet");
            partial.Should().Be(300, "the bytes left pending start at this read");
            LoopTiming.DrainChanged(ref partial, 400, 10, 1, false, false, out from).Should().BeTrue();
            from.Should().Be(300);
            partial.Should().Be(0);
        }

        [Fact]
        public void Injected_Packets_And_Connection_Changes_Are_Changes_Without_Bytes()
        {
            long partial = 0;
            LoopTiming.DrainChanged(ref partial, 100, 0, 1, false, false, out long from).Should().BeTrue("a plugin-injected packet was dispatched");
            from.Should().Be(100);
            partial = 50;
            LoopTiming.DrainChanged(ref partial, 100, 0, 0, false, true, out from).Should().BeTrue("the connection ended");
            from.Should().Be(0);
            partial.Should().Be(0);
            LoopTiming.DrainChanged(ref partial, 100, 0, 0, false, false, out _).Should().BeFalse();
        }

        // evloop-3: while present on change runs the loop wakes for the ping deadline (the ping goes out from an Update that does
        // not draw); in the other cadences it does not track it, as before
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_Ping_Deadline_Is_A_Timer_Only_While_Present_On_Change_Runs(bool on)
        {
            // Time.Ticks > due sends it, i.e. at due + 1: 1000 + 1 - 995 - 2 ms since the Update = 4 ms
            double ms = LoopTiming.MsUntilPing(on, 1000, 995, 2);

            if (on)
            {
                ms.Should().Be(4);
            }
            else
            {
                ms.Should().Be(double.PositiveInfinity);
            }
        }

        [Fact]
        public void A_Ping_Deadline_The_Scene_Did_Not_Service_Is_Not_Waited_For()
        {
            // past at the Update start (not in game, scene returned early): waking for it would spin
            LoopTiming.MsUntilPing(true, 1000, 1001, 0).Should().Be(double.PositiveInfinity);
            LoopTiming.MsUntilPing(true, 0, 5, 0).Should().Be(double.PositiveInfinity);
            LoopTiming.MsUntilPing(true, 1000, 1000, 0.5).Should().Be(0.5);
        }

        // evloop-6: a ping due just before a present is held until right after it, so its reply does not wait out a Draw
        [Theory]
        [InlineData(true, 0.0, true)]
        [InlineData(false, 0.4, true)]
        [InlineData(false, 1.0, true)]
        [InlineData(false, 1.5, false)]
        [InlineData(false, Interval, false)]
        public void A_Ping_Due_Just_Before_A_Present_Is_Held(bool drawsNow, double msUntilPresent, bool held)
        {
            LoopTiming.HoldPingForPresent(true, drawsNow, msUntilPresent, 0).Should().Be(held);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_Ping_Is_Never_Held_Outside_Present_On_Change(bool drawsNow)
        {
            LoopTiming.HoldPingForPresent(false, drawsNow, 0, 0).Should().BeFalse();
        }

        [Fact]
        public void A_Held_Ping_Goes_Out_Once_It_Is_Too_Late()
        {
            LoopTiming.HoldPingForPresent(true, true, 0, LoopTiming.PingHoldMaxMs - 1).Should().BeTrue();
            LoopTiming.HoldPingForPresent(true, true, 0, LoopTiming.PingHoldMaxMs).Should().BeFalse();
            LoopTiming.HoldPingForPresent(true, false, 0.5, LoopTiming.PingHoldMaxMs + 10).Should().BeFalse();
        }

        [Fact]
        public void A_Held_Ping_Without_A_Draw_Is_Not_A_Timer()
        {
            // held at the deadline for a present under a ms away: the deadline has passed at the next Update's start, so the
            // wait ends at the present, not at once
            LoopTiming.MsUntilPing(true, 1000, 1001, 0).Should().Be(double.PositiveInfinity);
            LoopTiming.WaitMs(LoopTiming.PingHoldLeadMs, LoopTiming.MsUntilPing(true, 1000, 1001, 0), 50).Should().Be(LoopTiming.PingHoldLeadMs);
        }

        [Fact]
        public void The_First_Present_Is_Due_At_Once()
        {
            LoopTiming.PresentDue(false, double.PositiveInfinity, Interval, double.PositiveInfinity).Should().BeTrue();
        }

        // Both schedules on one timeline: idle, then one packet 100.301 ms in. The grid (the legacy loop's display-rate cap)
        // presents at the next display slot; present on change presents at once. A burst of changes afterwards stays at the
        // display rate in both.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Idle_Then_Event_Latency_And_Busy_Rate_Grid_And_Present_On_Change(bool presentOnChange)
        {
            const long f = 1_000_000; // 1 µs ticks
            long period = DisplayRateSchedule.PeriodTicks(60, f);
            var grid = new DisplayRateSchedule();
            long lastPresent = 0;
            bool change = false;
            long eventAt = 100_301, firstAfterEvent = -1, minGap = long.MaxValue, presents = 0, prev = 0;

            for (long t = 1; t <= 400_000; t += 100) // an Update every 0.1 ms
            {
                if (t == eventAt || t >= 200_000)
                {
                    change = true; // one packet, then a busy stretch with a change on every Update
                }

                bool gridDue = grid.Due(t, period);
                bool due;

                if (presentOnChange)
                {
                    double since = lastPresent == 0 ? double.PositiveInfinity : (t - lastPresent) / 1000.0;
                    due = LoopTiming.PresentDue(change, since, period / 1000.0, double.PositiveInfinity); // nothing animates
                }
                else
                {
                    due = gridDue;
                }

                if (!due)
                {
                    continue;
                }

                if (prev != 0 && t > 200_000)
                {
                    minGap = System.Math.Min(minGap, t - prev);
                    presents++;
                }

                if (change && t >= eventAt && firstAfterEvent < 0)
                {
                    firstAfterEvent = t;
                }

                prev = lastPresent = t;
                change = false;
            }

            long delay = firstAfterEvent - eventAt;

            if (presentOnChange)
            {
                delay.Should().Be(0, "an event after an idle interval presents at once");
                minGap.Should().BeGreaterThanOrEqualTo(period - (long)(LoopTiming.DrawSlackMs * 1000), "the rate limit holds while busy");
            }
            else
            {
                delay.Should().BeGreaterThan(0, "the grid holds the draw for the next display slot");
                delay.Should().BeLessThanOrEqualTo(period);
            }

            // busy: about 60 presents/s in both (200 ms → 12)
            presents.Should().BeInRange(11, 13);
        }
    }
}
