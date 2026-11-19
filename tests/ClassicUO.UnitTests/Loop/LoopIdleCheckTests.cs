#if CUO_DEVTOOLS
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
}
#endif
