using System;
using ClassicUO.Configuration;
using ClassicUO.IO.Audio;
using ClassicUO.Loop;
using Xunit;

namespace ClassicUO.UnitTests.Loop;

// loop-idle: event_loop_idle_wait's longest wait, its deadlines (LoopDue) and the music refill time, in both switch states.
public class LoopIdleTests
{
    [Fact]
    public void SwitchIsOffByDefault()
    {
        var s = new Settings();

        Assert.False(s.EventLoopIdleWait);
        Assert.Equal(16, s.EventLoopMaxWaitMs);
        Assert.Equal(250, s.EventLoopIdleMaxWaitMs);
    }

    [Theory]
    [InlineData(false, false, 16, 250, 16)] // off: the cadence, idle or not
    [InlineData(false, true, 16, 250, 16)]
    [InlineData(true, false, 16, 250, 250)] // on and idle: the idle cap
    [InlineData(true, true, 16, 250, 16)] // on, something polls every Update: the cadence
    [InlineData(true, false, 16, 5, 16)] // an idle cap below the cadence never shortens it
    [InlineData(true, false, 0, 0, 1)]
    public void MaxWait(bool idleWait, bool busy, int maxWait, int idleMaxWait, double expected)
        => Assert.Equal(expected, LoopTiming.MaxWaitMs(idleWait, busy, maxWait, idleMaxWait));

    [Fact]
    public void NoDeadlineIsInfinite()
    {
        Assert.Equal(double.PositiveInfinity, LoopTiming.MsUntilLoopDue(long.MaxValue, 1000, 0, out bool stale));
        Assert.False(stale);
    }

    [Fact]
    public void AFutureDeadlineIsMeasuredFromNow()
    {
        // due 1100: served in the first Update with Ticks > 1100, i.e. 101 ms after an Update at 1000, 1 ms already gone
        Assert.Equal(100, LoopTiming.MsUntilLoopDue(1100, 1000, 1, out bool stale));
        Assert.False(stale);
    }

    [Theory]
    [InlineData(999)] // already past at the Update start, and that Update did not serve it: keep the cadence, no spin
    [InlineData(500)]
    public void APassedDeadlineIsStale(long due)
    {
        Assert.Equal(double.PositiveInfinity, LoopTiming.MsUntilLoopDue(due, 1000, 0, out bool stale));
        Assert.True(stale);
    }

    [Fact]
    public void DueAtTheUpdateTickIsNotStale()
    {
        // Ticks == due: the work runs in the next Update (Ticks > due), 1 ms on
        Assert.Equal(1, LoopTiming.MsUntilLoopDue(1000, 1000, 0, out bool stale));
        Assert.False(stale);
    }

    [Fact]
    public void LoopDueKeepsTheEarliestDeadlineAndTheBusySources()
    {
        LoopDue.Begin();
        Assert.Equal(long.MaxValue, LoopDue.NextTick);
        Assert.Equal(LoopDueSource.None, LoopDue.Busy);

        LoopDue.At(500, LoopDueSource.UseQueue);
        LoopDue.At(300, LoopDueSource.Music);
        LoopDue.At(400, LoopDueSource.DelayedClick);
        LoopDue.Now(LoopDueSource.Input);
        LoopDue.Now(LoopDueSource.Opl);

        Assert.Equal(300, LoopDue.NextTick);
        Assert.Equal(LoopDueSource.Music, LoopDue.NextSource);
        Assert.Equal(LoopDueSource.Input | LoopDueSource.Opl, LoopDue.Busy);

        LoopDue.Begin();
        Assert.Equal(long.MaxValue, LoopDue.NextTick);
        Assert.Equal(LoopDueSource.None, LoopDue.Busy);
    }

    // The wait an idle client gets with the switch off and on: the earliest of the present, the timers and the cap.
    [Theory]
    [InlineData(false, 16)]
    [InlineData(true, 180)]
    public void IdleWaitBothStates(bool idleWait, double expected)
    {
        double present = 600, timers = Math.Min(900, LoopTiming.MsUntilLoopDue(1180, 1000, 1, out bool stale));
        double wait = LoopTiming.WaitMs(present, timers, LoopTiming.MaxWaitMs(idleWait, stale, 16, 250));

        Assert.Equal(expected, wait);
    }

    [Fact]
    public void MusicWithoutAStreamNeedsNoUpdate()
    {
        var music = new UOMusic(0, "test", false, () => null, "test");

        Assert.Equal(double.PositiveInfinity, music.MsUntilUpdateNeeded());
        Assert.False(music.Starved);
    }
}
