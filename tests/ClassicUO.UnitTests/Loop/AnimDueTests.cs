using System.Text.Json;
using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Loop;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // evloop-4: present_on_change_anim_due, idle frames at the next visual change instead of every idle redraw period.
    public class AnimDueTests
    {
        private const double Interval = 1000.0 / 144;

        [Fact]
        public void Setting_Is_Off_By_Default_And_Round_Trips()
        {
            new Settings().PresentOnChangeAnimDue.Should().BeFalse();
            JsonSerializer.Deserialize<Settings>("{\"present_on_change_anim_due\":true}").PresentOnChangeAnimDue.Should().BeTrue();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Pending_Change_Presents_One_Interval_After_The_Last_Present_In_Both_States(bool animDue)
        {
            LoopTiming.MsUntilPresent(true, 2, Interval, 0, animDue, double.PositiveInfinity).Should().BeApproximately(Interval - 2, 1e-9);
            LoopTiming.MsUntilPresent(true, 40, Interval, 50, animDue, double.PositiveInfinity).Should().Be(0);
            LoopTiming.PresentDue(true, 40, Interval, 50, animDue, double.PositiveInfinity).Should().BeTrue();
        }

        [Fact]
        public void Off_Keeps_The_Idle_Redraw_Period_And_Ignores_The_Visual_Due()
        {
            LoopTiming.MsUntilPresent(false, 20, Interval, 50, false, 5).Should().Be(30);
            LoopTiming.MsUntilPresent(false, 2, Interval, 0, false, double.PositiveInfinity).Should().BeApproximately(Interval - 2, 1e-9);
            LoopTiming.PresentDue(false, Interval, Interval, 0, false, double.PositiveInfinity).Should().BeTrue();
        }

        [Fact]
        public void On_With_Nothing_Due_Presents_Only_The_Safety_Net()
        {
            // evloop-5: nothing scheduled, so the next idle frame is the safety net, one second after the last present
            LoopTiming.MsUntilPresent(false, 200, Interval, 0, true, double.PositiveInfinity).Should().Be(LoopTiming.AnimDueSafetyNetMs - 200);
            LoopTiming.PresentDue(false, 500, Interval, 50, true, double.PositiveInfinity).Should().BeFalse();
            LoopTiming.PresentDue(false, LoopTiming.AnimDueSafetyNetMs, Interval, 50, true, double.PositiveInfinity).Should().BeTrue();
            LoopTiming.PresentDue(false, 1e6, Interval, 50, true, double.PositiveInfinity).Should().BeTrue();
        }

        [Fact]
        public void The_Safety_Net_Never_Delays_An_Earlier_Due_And_Never_Beats_The_Rate_Limit()
        {
            LoopTiming.MsUntilPresent(false, 100, Interval, 0, true, 30).Should().Be(30);
            LoopTiming.MsUntilPresent(false, 100, Interval, 0, true, 5000).Should().Be(LoopTiming.AnimDueSafetyNetMs - 100);
            LoopTiming.MsUntilPresent(false, 2, Interval, 0, true, double.PositiveInfinity).Should().Be(LoopTiming.AnimDueSafetyNetMs - 2);
            // before the first present there is no last present: present at once
            LoopTiming.MsUntilPresent(false, double.PositiveInfinity, Interval, 0, true, double.PositiveInfinity).Should().Be(0);
        }

        [Fact]
        public void Off_Has_No_Safety_Net_Only_The_Idle_Redraw_Period()
        {
            LoopTiming.MsUntilPresent(false, 200, Interval, 5000, false, double.PositiveInfinity).Should().Be(4800);
        }

        [Fact]
        public void On_Presents_When_The_Visual_Change_Is_Due_But_Not_Within_The_Interval()
        {
            // due in 30 ms, last present 100 ms ago: wait for the due time
            LoopTiming.MsUntilPresent(false, 100, Interval, 50, true, 30).Should().Be(30);
            // due now but the last present was 2 ms ago: the rate limit holds it to the interval
            LoopTiming.MsUntilPresent(false, 2, Interval, 50, true, 0).Should().BeApproximately(Interval - 2, 1e-9);
            LoopTiming.PresentDue(false, 100, Interval, 50, true, 0).Should().BeTrue();
            LoopTiming.PresentDue(false, 100, Interval, 50, true, LoopTiming.DrawSlackMs + 0.1).Should().BeFalse();
        }

        [Fact]
        public void Visual_Due_Is_Reached_Once_The_Game_Time_Is_Past_It()
        {
            LoopTiming.VisualDueReached(long.MaxValue, uint.MaxValue).Should().BeFalse();
            LoopTiming.VisualDueReached(1000, 1000).Should().BeFalse();
            LoopTiming.VisualDueReached(1000, 1001).Should().BeTrue();
            // the wait wakes at due + 1, the first tick the source advances at
            LoopTiming.MsUntilTick(true, 1000, 990, 0).Should().Be(11);
        }

        [Fact]
        public void Visual_Due_Keeps_The_Earliest_Tick_Per_Set_And_The_Union()
        {
            VisualDue.Reset();
            VisualDue.BeginUpdate();
            VisualDue.At(500, VisualSource.MobileAnim);
            VisualDue.At(300, VisualSource.WorldText);
            VisualDue.At(400, VisualSource.Gump);
            VisualDue.NextTick.Should().Be(300);
            VisualDue.NextSource.Should().Be(VisualSource.WorldText);

            VisualDue.BeginDraw();
            VisualDue.At(200, VisualSource.Tooltip);
            VisualDue.Now(VisualSource.Weather);
            VisualDue.EndDraw();
            VisualDue.NextTick.Should().Be(200);
            VisualDue.Continuous.Should().Be(VisualSource.Weather);

            // a new Update clears only the Update set; the Draw set describes the last drawn frame until the next draw
            VisualDue.BeginUpdate();
            VisualDue.NextTick.Should().Be(200);
            VisualDue.At(250, VisualSource.MobileAnim);
            VisualDue.Now(VisualSource.Effects);
            VisualDue.Continuous.Should().Be(VisualSource.Weather | VisualSource.Effects);

            VisualDue.BeginDraw();
            VisualDue.EndDraw();
            VisualDue.NextTick.Should().Be(250);
            VisualDue.Continuous.Should().Be(VisualSource.Effects);
            VisualDue.Reset();
            VisualDue.NextTick.Should().Be(long.MaxValue);
        }

        private static AnimationTimer Timer(byte frames, bool execute = true, bool fromServer = false)
            => new AnimationTimer { ActionFrames = frames, ExecuteAnimation = execute, FromServer = fromServer };

        [Fact]
        public void Mobile_Frame_Timer_Counts_Only_When_The_Frame_Can_Move()
        {
            var clock = new MobileClock { AnimDue = 1080, IdleDue = 40000 };

            MobileClockRules.NextVisualTick(clock, Timer(1), 1000).Should().Be(40000, "one frame: the advance shows the same picture");
            MobileClockRules.NextVisualTick(clock, Timer(5), 1000).Should().Be(1080);
            MobileClockRules.NextVisualTick(clock, Timer(1, fromServer: true), 1000).Should().Be(1080, "a server action can end");
            MobileClockRules.NextVisualTick(clock, Timer(5, execute: false), 1000).Should().Be(40000);
            MobileClockRules.NextVisualTick(clock, Timer(0), 1000).Should().Be(1080, "no advance since the action was set: not known");
        }

        [Fact]
        public void A_Normal_Action_Keeps_Its_Frame_Count_For_The_Due_Time()
        {
            // a normal action's frames cycle with FrameCount 0 (only a server action stores it); the advance records the
            // action's frame count so the next frame is scheduled (bench-000827: a standing mobile's frames were missed)
            var t = new AnimationTimer { Group = 4, ExecuteAnimation = true };
            ClassicUO.Game.AnimationTiming.Advance(ref t, 6, out _);
            t.FrameCount.Should().Be(0);
            t.ActionFrames.Should().Be(6);
            MobileClockRules.NextVisualTick(new MobileClock { AnimDue = 1080, IdleDue = 40000 }, t, 1000).Should().Be(1080);

            ClassicUO.Game.AnimationTiming.Advance(ref t, 1, out _);
            MobileClockRules.NextVisualTick(new MobileClock { AnimDue = 1080, IdleDue = 40000 }, t, 1000).Should().Be(40000);

            ClassicUO.Game.AnimationAdvanceRules.SetAnimation(ref t, 0xFF);
            t.ActionFrames.Should().Be(0, "a new action's frame count is not known until it advances");
        }

        [Fact]
        public void Mobile_Steps_Or_A_Stale_Due_Change_Every_Frame()
        {
            var stepping = new MobileClock { AnimDue = 1080, IdleDue = 40000, Flags = MobileClockFlags.Stepping };
            MobileClockRules.NextVisualTick(stepping, Timer(1), 1000).Should().Be(MobileClockRules.VisualEveryFrame);

            var declined = new MobileClock { AnimDue = 900, IdleDue = 40000 };
            MobileClockRules.NextVisualTick(declined, Timer(5), 1000).Should().Be(MobileClockRules.VisualEveryFrame);
            MobileClockRules.NextVisualTick(declined, Timer(1), 1000).Should().Be(40000, "a stale frame timer that cannot move a frame");
        }

        [Fact]
        public void A_Frame_After_An_Idle_Wait_Is_Not_A_Slow_Frame()
        {
            // the session timeline's frame time leaves out the loop's waits: 100 ms between presents with 97 ms of waiting is a
            // 3 ms frame (switch on: idle presents follow long waits; the slow-frame log line allocated on each of them)
            FrameStats.BusyFrameMs(100_000, 97_000, 1_000_000).Should().BeApproximately(3, 1e-9);
            FrameStats.BusyFrameMs(100_000, 0, 1_000_000).Should().BeApproximately(100, 1e-9);
            FrameStats.BusyFrameMs(5_000, 6_000, 1_000_000).Should().Be(0);
        }
    }
}
