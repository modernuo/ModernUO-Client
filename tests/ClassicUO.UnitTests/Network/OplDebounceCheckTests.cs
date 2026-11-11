#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // CUO_OPL_DEBOUNCE_CHECK: what counts as a repeated request, a repeated notice and an unrequested answer
    [Collection(nameof(OplRequestPathCollection))]
    public class OplDebounceCheckTests : IDisposable
    {
        private readonly List<string> _repeats = new List<string>();

        public OplDebounceCheckTests()
        {
            OplDebounceCheck.Reset();
            OplDebounceCheck.Override = true;
            OplDebounceCheck.RepeatObserver = _repeats.Add;
        }

        public void Dispose()
        {
            OplDebounceCheck.Override = null;
            OplDebounceCheck.RepeatObserver = null;
            OplDebounceCheck.Reset();
        }

        [Fact]
        public void A_Second_Request_At_The_Same_Revision_Is_A_Repeat()
        {
            OplDebounceCheck.Notice(1, 0x40000005, false);
            OplDebounceCheck.Sent(1, 0);
            // ModernUO's Mobile.ProcessDelta re-sends the unchanged revision on a step
            OplDebounceCheck.Notice(1, 0x40000005, false);
            OplDebounceCheck.Sent(1, 100);

            OplDebounceCheck.Repeats.Should().Be(1);
            OplDebounceCheck.RepeatsAfterAnswer.Should().Be(0);
            OplDebounceCheck.NoticeRepeats.Should().Be(1);
            _repeats.Should().ContainSingle().Which.Should().Contain("0x00000001").And.Contain("unanswered");

            OplDebounceCheck.Answered(1);
            OplDebounceCheck.Sent(1, 200);
            OplDebounceCheck.RepeatsAfterAnswer.Should().Be(1);
        }

        [Fact]
        public void A_New_Revision_Or_A_Re_Entry_Is_Not_A_Repeat()
        {
            OplDebounceCheck.Notice(2, 0x40000001, false);
            OplDebounceCheck.Sent(2, 0);
            OplDebounceCheck.Answered(2);
            OplDebounceCheck.Notice(2, 0x40000002, false);
            OplDebounceCheck.Sent(2, 10);

            // out of range and back: World removed it, the server sends its 0xDC again
            OplDebounceCheck.Removed(2);
            OplDebounceCheck.Notice(2, 0x40000002, false);
            OplDebounceCheck.Sent(2, 20);

            // logout: everything is forgotten
            OplDebounceCheck.Cleared();
            OplDebounceCheck.Notice(2, 0x40000002, false);
            OplDebounceCheck.Sent(2, 30);

            OplDebounceCheck.Repeats.Should().Be(0);
            OplDebounceCheck.Reentries.Should().Be(2);
            OplDebounceCheck.NoticeRepeats.Should().Be(0);
            _repeats.Should().BeEmpty();
        }

        [Fact]
        public void The_0xD6_Hash_And_The_0xDC_Revision_Compare_Without_The_Flag()
        {
            OplDebounceCheck.Notice(3, 0x40000007, false);
            OplDebounceCheck.Notice(3, 0x00000007, true);

            OplDebounceCheck.NoticeRepeats.Should().Be(1);
            OplDebounceCheck.NoticesHeld.Should().Be(1);
        }

        [Fact]
        public void An_Answer_Without_A_Request_Is_Unrequested()
        {
            // a vendor's buy list pushes 0xD6 for its items
            OplDebounceCheck.Answered(4);
            OplDebounceCheck.Sent(4, 0);
            OplDebounceCheck.Answered(4);
            OplDebounceCheck.Answered(4);

            OplDebounceCheck.Answers.Should().Be(3);
            OplDebounceCheck.UnrequestedAnswers.Should().Be(2);
        }

        [Fact]
        public void The_Summary_Logs_Once_At_Exit()
        {
            OplDebounceCheck.Sent(5, 0);
            OplDebounceCheck.LogSummary(0, "exit");
            OplDebounceCheck.LogSummary(0, "exit");
            OplDebounceCheck.Requests.Should().Be(1);
        }
    }
}
#endif
