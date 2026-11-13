using ClassicUO.Loop;
using Xunit;
using ClassicUO.Configuration;
using ClassicUO.Game.Scenes;
using System.Text.Json;

namespace ClassicUO.UnitTests.Loop;

public class DisplayRateTests
{
    [Fact]
    public void SettingIsOnByDefaultAndRoundTrips()
    {
        Assert.True(new Settings().PresentAtDisplayRate);
        Assert.True(JsonSerializer.Deserialize<Settings>("{\"present_at_display_rate\":true}").PresentAtDisplayRate);
    }

    [Fact]
    public void FadeCarriesMissedStepsOnlyWhenDisplayPaced()
    {
        long next = 100;
        Assert.Equal(2, GameScene.FadeSteps(134, ref next, true));
        Assert.Equal(140, next);
        Assert.Equal(0, GameScene.FadeSteps(139, ref next, true));
        Assert.Equal(1, GameScene.FadeSteps(151, ref next, true));
        Assert.Equal(160, next);
        Assert.Equal(1, GameScene.FadeSteps(194, ref next, false));
        Assert.Equal(214, next);
    }

#if CUO_DEVTOOLS
    [Fact]
    public void CheckerFlagsLateAndOverRateOnlyAboveThresholds()
    {
        Assert.False(PresentCapCheck.Late(11000, 10000, 1000000));
        Assert.True(PresentCapCheck.Late(11001, 10000, 1000000));
        Assert.False(PresentCapCheck.OverRate(315, 5, 60));
        Assert.True(PresentCapCheck.OverRate(316, 5, 60));
    }

    [Fact]
    public void CheckerAcceptsLatePresentsButRejectsInvalidDrawPresentPairs()
    {
        // Changing eligibility resets the static checker between scenarios.
        PresentCapCheck.Update(1, false, false, 60, 10000);
        PresentCapCheck.Update(2, true, true, 60, 10000);
        PresentCapCheck.Draw(true);
        PresentCapCheck.Present(3);
        PresentCapCheck.Draw(true);
        PresentCapCheck.Present(System.Diagnostics.Stopwatch.Frequency / 2);
        Assert.Equal(0, PresentCapCheck.Mismatches);
        PresentCapCheck.Draw(false);
        PresentCapCheck.Present(4);
        Assert.Equal(1, PresentCapCheck.Mismatches);
        PresentCapCheck.Present(5);
        Assert.Equal(2, PresentCapCheck.Mismatches);
        PresentCapCheck.Draw(true);
        PresentCapCheck.Draw(true);
        Assert.Equal(3, PresentCapCheck.Mismatches);
        PresentCapCheck.Update(6, false, false, 60, 10000);
        PresentCapCheck.Draw(false); // cap off: ordinary draw is eligible
        PresentCapCheck.Present(7);
        Assert.Equal(0, PresentCapCheck.Mismatches);
    }
#endif
    [Theory]
    [InlineData(false, 0, false, false, false)]
    [InlineData(true, 0, false, false, true)]
    [InlineData(true, 240, false, false, false)]
    [InlineData(true, 0, true, false, false)]
    [InlineData(true, 0, false, true, false)]
    public void PolicyHonorsSwitchAndExistingCaps(bool on, int fps, bool vsync, bool inactive, bool expected)
        => Assert.Equal(expected, DisplayRateSchedule.Enabled(on, fps, vsync, inactive));

    [Fact]
    public void DeadlineCarriesPhaseAndResyncsAfterMissedPeriod()
    {
        var schedule = new DisplayRateSchedule();
        Assert.True(schedule.Due(100, 10));
        Assert.Equal(110, schedule.Next);
        Assert.False(schedule.Due(109, 10));
        Assert.True(schedule.Due(112, 10));
        Assert.Equal(120, schedule.Next);
        Assert.True(schedule.Due(145, 10));
        Assert.Equal(155, schedule.Next);
        Assert.False(schedule.Due(145, 10));
    }

    [Fact]
    public void RefreshChangeAndDisableResetDeadline()
    {
        var schedule = new DisplayRateSchedule();
        Assert.True(schedule.Due(100, 10));
        Assert.True(schedule.Due(101, 20));
        Assert.Equal(121, schedule.Next);
        Assert.True(schedule.Due(102, 0));
        Assert.Equal(0, schedule.Next);
        Assert.True(schedule.Due(103, 20));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(60, 100000)]
    [InlineData(59.94, 100100)]
    public void RefreshUsesStopwatchTicks(double hz, long expected)
        => Assert.Equal(expected, DisplayRateSchedule.PeriodTicks(hz, 6000000));
}
