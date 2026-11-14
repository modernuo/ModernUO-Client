using System.Diagnostics;
using System.Text.Json;
using ClassicUO.Configuration;
using ClassicUO.Loop;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // evloop-2: present_on_change's scheduling (rate-limited from the last present) against the display grid it replaces.
    public class PresentOnChangeTests
    {
        private const double Interval = 1000.0 / 60;

        [Fact]
        public void Setting_Is_Off_By_Default_And_Round_Trips()
        {
            var s = new Settings();
            s.PresentOnChange.Should().BeFalse();
            s.EventLoopIdleRedrawMs.Should().Be(0, "0 = idle frames at the display interval (no visual change)");

            var r = JsonSerializer.Deserialize<Settings>("{\"present_on_change\":true,\"event_loop_idle_redraw_ms\":100}");
            r.PresentOnChange.Should().BeTrue();
            r.EventLoopIdleRedrawMs.Should().Be(100);
        }

        [Fact]
        public void A_Change_After_An_Idle_Interval_Presents_At_Once()
        {
            LoopTiming.MsUntilPresent(true, 40, Interval, 50).Should().Be(0);
            LoopTiming.PresentDue(true, 40, Interval, 50).Should().BeTrue();
        }

        [Fact]
        public void A_Change_Soon_After_A_Present_Waits_For_The_Interval()
        {
            LoopTiming.MsUntilPresent(true, 5, Interval, 50).Should().BeApproximately(Interval - 5, 1e-9);
            LoopTiming.PresentDue(true, Interval - LoopTiming.DrawSlackMs - 0.01, Interval, 50).Should().BeFalse();
            LoopTiming.PresentDue(true, Interval - LoopTiming.DrawSlackMs + 0.01, Interval, 50).Should().BeTrue();
        }

        [Fact]
        public void No_Change_Redraws_After_The_Idle_Period_Never_Faster_Than_The_Interval()
        {
            LoopTiming.MsUntilPresent(false, 20, Interval, 50).Should().Be(30);
            LoopTiming.PresentDue(false, 20, Interval, 50).Should().BeFalse();
            LoopTiming.PresentDue(false, 50, Interval, 50).Should().BeTrue();
            LoopTiming.MsUntilPresent(false, 5, Interval, 1).Should().BeApproximately(Interval - 5, 1e-9);
        }

        [Fact]
        public void Default_Idle_Redraw_Zero_Means_The_Display_Interval()
        {
            LoopTiming.MsUntilPresent(false, 5, Interval, 0).Should().BeApproximately(Interval - 5, 1e-9);
            LoopTiming.PresentDue(false, Interval, Interval, 0).Should().BeTrue();
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

        [Fact]
        public void The_First_Present_Is_Due_At_Once()
        {
            LoopTiming.PresentDue(false, double.PositiveInfinity, Interval, 50).Should().BeTrue();
        }

        // Both switch states on one timeline: idle, then one packet 100.301 ms in. Off (the grid) presents at the next display
        // slot; on presents at once. A burst of changes afterwards stays at the display rate in both.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Idle_Then_Event_Latency_And_Busy_Rate_In_Both_Switch_States(bool presentOnChange)
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
                    due = LoopTiming.PresentDue(change, since, period / 1000.0, 1000); // idle redraw out of the way
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

#if CUO_DEVTOOLS
        [Fact]
        public void Checker_Flags_Gaps_Under_The_Interval_Less_The_Slack()
        {
            long f = Stopwatch.Frequency;
            long period = f / 60;
            long slack = (long)((LoopTiming.DrawSlackMs + 0.1) * f / 1000.0);
            PresentChangeCheck.UnderGap(period - slack, period, f).Should().BeFalse();
            PresentChangeCheck.UnderGap(period - slack - 1, period, f).Should().BeTrue();
        }

        [Fact]
        public void Checker_Counts_A_Draw_Later_Than_The_Grid_And_A_Short_Draw_Gap()
        {
            long f = Stopwatch.Frequency;
            long period = f / 60;
            long t = 10 * f;
            PresentChangeCheck.Tick(t, false, false, false, false, period); // reset
            PresentChangeCheck.Tick(t + 1, true, false, false, false, period);
            long later0 = PresentChangeCheck.Later, mism0 = PresentChangeCheck.Mismatches;

            PresentChangeCheck.Tick(t + 2, true, true, false, true, period); // change pending, grid due, new holds
            PresentChangeCheck.Tick(t + 2 + f / 100, true, false, true, true, period); // new draws 10 ms later
            PresentChangeCheck.Later.Should().Be(later0 + 1);
            PresentChangeCheck.Mismatches.Should().Be(mism0);

            // presents closer than the interval (draw time varies) are not a mismatch; draws closer than it are
            PresentChangeCheck.Present(t + f / 50);
            PresentChangeCheck.Present(t + f / 50 + period / 2);
            PresentChangeCheck.Mismatches.Should().Be(mism0);
            PresentChangeCheck.Tick(t + 2 + f / 100 + period / 2, true, false, true, true, period);
            PresentChangeCheck.Mismatches.Should().Be(mism0 + 1);
            PresentChangeCheck.Tick(t + 2 + f / 100 + period / 2 + period, true, false, true, true, period);
            PresentChangeCheck.Mismatches.Should().Be(mism0 + 1);
        }
#endif
    }
}
