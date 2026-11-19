#if CUO_DEVTOOLS
using ClassicUO.Loop;
using Xunit;

namespace ClassicUO.UnitTests.Loop;

public class VsyncPresentCheckTests
{
    [Theory]
    [InlineData(4, 10, false)]
    [InlineData(6, 10, true)]
    [InlineData(6, 0, false)]
    public void BlockedIsMoreThanHalfAPeriod(long block, long period, bool expected)
        => Assert.Equal(expected, VsyncPresentCheck.Blocked(block, period));

    [Theory]
    [InlineData(15, 10, true, false)]
    [InlineData(16, 10, true, true)]
    [InlineData(30, 10, false, false)]
    public void MissedVblankOnlyAfterAContinuousFrame(long gap, long period, bool previousContinuous, bool expected)
        => Assert.Equal(expected, VsyncPresentCheck.MissedVblank(gap, period, previousContinuous));

    // Both switch states: the summary state resets on a switch change, and a draw-present pair is never a mismatch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawThenPresentIsNoMismatch(bool switchOn)
    {
        VsyncPresentCheck.Update(1, !switchOn, true, false, false, 60, 10);
        VsyncPresentCheck.Update(2, switchOn, true, false, switchOn, 60, 10);
        VsyncPresentCheck.Decision(true, true, true);
        VsyncPresentCheck.Draw(false);
        VsyncPresentCheck.Present(3, 4);
        Assert.Equal(0, VsyncPresentCheck.Mismatches);

        VsyncPresentCheck.Present(5, 6);
        Assert.Equal(1, VsyncPresentCheck.Mismatches);
    }

    [Fact]
    public void DrawNotDueWhileEngagedIsAMismatch()
    {
        VsyncPresentCheck.Update(1, false, true, false, false, 60, 10);
        VsyncPresentCheck.Update(2, true, true, false, true, 60, 10);
        VsyncPresentCheck.Decision(true, true, false);
        Assert.Equal(1, VsyncPresentCheck.Mismatches);
    }

    // vsync_mailbox engages the schedule with vsync_present_on_change off; a mailbox change resets the window like a switch change.
    [Fact]
    public void MailboxEngagedWindowCountsLikeTheSwitch()
    {
        VsyncPresentCheck.Update(1, false, true, false, true, 60, 10);
        VsyncPresentCheck.Decision(true, true, false);
        Assert.Equal(1, VsyncPresentCheck.Mismatches);
        VsyncPresentCheck.Update(2, false, true, true, true, 60, 10);
        Assert.Equal(0, VsyncPresentCheck.Mismatches);
        VsyncPresentCheck.Decision(true, true, true);
        VsyncPresentCheck.Draw(false);
        VsyncPresentCheck.Present(3, 4);
        Assert.Equal(0, VsyncPresentCheck.Mismatches);
    }
}
#endif
