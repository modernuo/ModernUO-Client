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
        => Assert.Equal(expected, DisplayRateSchedule.Enabled(on, fps, vsync, false, inactive, false));

    // vsync_present_on_change: off, vsync keeps its own cadence; on, vsync runs the schedule like vsync off, and the other caps
    // (present_at_display_rate off, an fps cap, an inactive window) still win.
    [Theory]
    [InlineData(true, 0, true, false, false, false)]
    [InlineData(true, 0, true, false, true, true)]
    [InlineData(true, 0, false, false, true, true)]
    [InlineData(false, 0, true, false, true, false)]
    [InlineData(true, 60, true, false, true, false)]
    [InlineData(true, 0, true, true, true, false)]
    public void VsyncPresentOnChangeEngagesTheScheduleWithVsync(bool on, int fps, bool vsync, bool inactive, bool vsyncSwitch, bool expected)
        => Assert.Equal(expected, DisplayRateSchedule.Enabled(on, fps, vsync, false, inactive, vsyncSwitch));

    // vsync_mailbox: in mailbox mode Present does not block, so the schedule runs as with vsync off, also with
    // present_at_display_rate off (no uncapped drawing) and without vsync_present_on_change; an fps cap and an inactive window
    // still win.
    [Theory]
    [InlineData(true, 0, false, false, true)]
    [InlineData(false, 0, false, false, true)]
    [InlineData(true, 0, false, true, true)]
    [InlineData(true, 60, false, false, false)]
    [InlineData(false, 60, false, false, false)]
    [InlineData(true, 0, true, false, false)]
    [InlineData(false, 0, true, true, false)]
    public void MailboxEngagesTheScheduleWithVsync(bool on, int fps, bool inactive, bool vsyncSwitch, bool expected)
        => Assert.Equal(expected, DisplayRateSchedule.Enabled(on, fps, true, true, inactive, vsyncSwitch));

    // Normal vsync (mailbox false) is the switch-off behaviour: unchanged.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void NormalVsyncKeepsItsCadence(bool on, bool expected)
        => Assert.Equal(expected, DisplayRateSchedule.Enabled(on, 0, true, false, false, false));

    [Fact]
    public void VsyncIsOnForNewSettingsAndASavedOffIsKept()
    {
        Assert.True(new Settings().VSync);
        Assert.False(JsonSerializer.Deserialize<Settings>("{\"vsync\":false}").VSync);
    }

    [Fact]
    public void VsyncMailboxIsOffByDefaultAndRoundTrips()
    {
        Assert.False(new Settings().VsyncMailbox);
        Assert.True(JsonSerializer.Deserialize<Settings>("{\"vsync_mailbox\":true}").VsyncMailbox);
        Assert.Contains("\"vsync_mailbox\":false", JsonSerializer.Serialize(new Settings()));
    }

    // FNA3D's choice (XNAToSDL_PresentMode) by driver: SDL_GPU honours the hint where the window supports mailbox, else vsync;
    // D3D11/OpenGL (Legacy fallback) have no mailbox.
    [Theory]
    // SDL_GPU, vsync on
    [InlineData(true, true, true, true, true, (int)PresentModeKind.Mailbox)]
    [InlineData(true, true, true, false, true, (int)PresentModeKind.Vsync)]
    [InlineData(true, true, false, true, true, (int)PresentModeKind.Vsync)]
    // SDL_GPU, vsync off: immediate where supported, else vsync
    [InlineData(false, true, true, true, true, (int)PresentModeKind.Immediate)]
    [InlineData(false, true, false, false, false, (int)PresentModeKind.Vsync)]
    // D3D11/OpenGL: the hint and SDL_GPU support do not apply
    [InlineData(true, false, true, true, true, (int)PresentModeKind.Vsync)]
    [InlineData(false, false, true, true, false, (int)PresentModeKind.Immediate)]
    public void PresentModeFollowsFna3dChoice(bool vsync, bool sdlGpu, bool hint, bool mailboxSupported, bool immediateSupported, int expected)
        => Assert.Equal((PresentModeKind)expected, PresentModeRule.Choose(vsync, sdlGpu, hint, mailboxSupported, immediateSupported));

    // SDL_GPU on Metal (macOS): METAL_SupportsPresentMode reports VSYNC and IMMEDIATE, never MAILBOX. With the switch on, vsync
    // stays normal vsync, the schedule keeps vsync's cadence and Options shows plain "VSync".
    // owner 2026-11-19: under mailbox an fps cap above the refresh rate is held to the display rate
    [Theory]
    [InlineData((int)PresentModeKind.Mailbox, 1000.0 / 240, 144.0, 1000.0 / 144)]
    [InlineData((int)PresentModeKind.Mailbox, 1000.0 / 60, 144.0, 1000.0 / 60)]
    [InlineData((int)PresentModeKind.Mailbox, 0.0, 144.0, 0.0)]
    [InlineData((int)PresentModeKind.Mailbox, 1000.0 / 240, 0.0, 1000.0 / 240)]
    [InlineData((int)PresentModeKind.Vsync, 1000.0 / 240, 144.0, 1000.0 / 240)]
    [InlineData((int)PresentModeKind.Immediate, 1000.0 / 240, 144.0, 1000.0 / 240)]
    public void MailboxHoldsAnFpsCapToTheDisplayRate(int mode, double intervalMs, double hz, double expected)
    {
        Assert.Equal(expected, PresentModeRule.CapInterval(intervalMs, (PresentModeKind)mode, hz), 9);
    }

    [Fact]
    public void MetalHasNoMailboxAndShowsPlainVsync()
    {
        var mode = PresentModeRule.Choose(true, true, true, false, true);
        Assert.Equal(PresentModeKind.Vsync, mode);
        Assert.Equal(string.Empty, PresentModeRule.OptionsSuffix(mode));
        Assert.False(DisplayRateSchedule.Enabled(true, 0, true, mode == PresentModeKind.Mailbox, false, false));
        Assert.Equal(PresentModeKind.Immediate, PresentModeRule.Choose(false, true, true, false, true));
    }

    // Players see "VSync (True Triple Buffering)" only while mailbox is in force; "mailbox" stays in logs.
    [Theory]
    [InlineData((int)PresentModeKind.Mailbox, "(True Triple Buffering)")]
    [InlineData((int)PresentModeKind.Vsync, "")]
    [InlineData((int)PresentModeKind.Immediate, "")]
    public void OptionsShowTripleBufferingOnlyForMailbox(int mode, string expected)
        => Assert.Equal(expected, PresentModeRule.OptionsSuffix((PresentModeKind)mode));

    // The switch only matters through the mode: Mailbox engages the schedule, the Vsync fallback keeps the old cadence.
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ScheduleFollowsTheChosenMode(bool switchOn, bool mailboxSupported, bool expected)
    {
        var mode = PresentModeRule.Choose(true, true, switchOn, mailboxSupported, true);
        Assert.Equal(expected, DisplayRateSchedule.Enabled(true, 0, true, mode == PresentModeKind.Mailbox, false, false));
    }

    [Fact]
    public void VsyncPresentOnChangeIsOnByDefaultAndRoundTrips()
    {
        Assert.True(new Settings().VsyncPresentOnChange);
        Assert.False(JsonSerializer.Deserialize<Settings>("{\"vsync_present_on_change\":false}").VsyncPresentOnChange);
        Assert.Contains("\"vsync_present_on_change\":true", JsonSerializer.Serialize(new Settings()));
    }

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
