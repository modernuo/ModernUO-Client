#if CUO_DEVTOOLS
using System.Diagnostics;
using ClassicUO.Loop;
using Xunit;

namespace ClassicUO.UnitTests.Loop;

public class LoopIdleCheckTests
{
    [Theory]
    [InlineData(17, 16, false)]
    [InlineData(18, 16, false)]
    [InlineData(18.5, 16, true)]
    [InlineData(200, 16, true)]
    public void LateIsBeyondTheCadenceAndSlack(double lateness, double cadence, bool expected)
        => Assert.Equal(expected, LoopIdleCheck.Late(lateness, cadence));

    [Theory]
    [InlineData(1, true, 100, false, true)] // a send after a long timed wake with nothing tracked due: untracked
    [InlineData(0, true, 100, false, false)] // no send
    [InlineData(1, false, 100, false, false)] // a socket or input wake caused it
    [InlineData(1, true, 17, false, false)] // within the old cadence
    [InlineData(1, true, 100, true, false)] // a tracked deadline (queued action, LoopDue) came due
    public void UntrackedSend(int sends, bool timedWake, double gap, bool trackedDue, bool expected)
        => Assert.Equal(expected, LoopIdleCheck.UntrackedSend(sends, timedWake, gap, 16, trackedDue));

    // Both switch states: a wait planned for a deadline, served on time, is no mismatch; served 100 ms late it is one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannedDeadline(bool switchOn)
    {
        LoopIdleCheck.UpdateStart(1, 1000, !switchOn, false, 16, false);
        LoopIdleCheck.UpdateStart(2, 1000, switchOn, switchOn, 16, false);
        long before = LoopIdleCheck.Mismatches;

        LoopIdleCheck.Wait(3, 16, false, 100, false, LoopDueSource.None, true, false, 1100, LoopDueSource.UseQueue);
        LoopIdleCheck.UpdateStart(4, 1101, switchOn, switchOn, 16, false);
        Assert.Equal(before, LoopIdleCheck.Mismatches);

        LoopIdleCheck.Wait(5, 16, false, 100, false, LoopDueSource.None, true, false, 1200, LoopDueSource.UseQueue);
        LoopIdleCheck.UpdateStart(6, 1301, switchOn, switchOn, 16, false);
        Assert.Equal(before + 1, LoopIdleCheck.Mismatches);
    }

    // Both switch states: a send from a cap wake (gap 250 ms) is tracked when the deadline came due by then (due == cap: the
    // world map's party query at event_loop_idle_max_wait_ms 250), and untracked when the deadline is still ahead.
    [Theory]
    [InlineData(false, 1250, 0)]
    [InlineData(true, 1250, 0)]
    [InlineData(false, 5000, 1)]
    [InlineData(true, 5000, 1)]
    public void CapWakeAtTheDeadline(bool switchOn, long dueTick, long expected)
    {
        long quarter = Stopwatch.Frequency / 4;
        LoopIdleCheck.UpdateStart(1, 1000, !switchOn, false, 16, false);
        LoopIdleCheck.UpdateStart(2, 1000, switchOn, switchOn, 16, false);
        long before = LoopIdleCheck.Mismatches;

        LoopIdleCheck.Wait(3, 16, false, 250, false, LoopDueSource.None, false, true, dueTick, LoopDueSource.WorldMap);
        LoopIdleCheck.Woke(true);
        LoopIdleCheck.UpdateStart(2 + quarter, 1251, switchOn, switchOn, 16, false);
        LoopIdleCheck.Send(0xF0);
        LoopIdleCheck.Wait(3 + quarter, 16, false, 250, false, LoopDueSource.None, false, true, long.MaxValue, LoopDueSource.None);
        Assert.Equal(before + expected, LoopIdleCheck.Mismatches);
    }
}
#endif
