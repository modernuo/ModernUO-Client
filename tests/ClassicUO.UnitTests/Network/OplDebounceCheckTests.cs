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

        // an arrival: 3000 objects announced by 0xDC, the mobiles' unchanged revisions repeated every 5 frames (ModernUO's
        // Mobile.ProcessDelta on each step), answers 400 ms after the request; both paths request each serial once
        [Theory]
        [InlineData(0)]
        [InlineData(600)]
        [InlineData(2000)]
        public void An_Arrival_With_Repeated_Notices_Requests_Each_Serial_Once(int budget)
        {
            var list = new OplRequestList();
            var queue = new OplRequestQueue { DistanceOf = s => (int) (s % 20) };
            var held = new Dictionary<uint, uint>();
            var answers = new Queue<(uint Tick, uint Serial)>();
            uint now = 0;
            OplBatchSink sink = serials =>
            {
                foreach (uint s in serials)
                {
                    OplDebounceCheck.Sent(s, now);
                    answers.Enqueue((now + 400, s));
                }
            };

            static uint Revision(uint serial) => 0x40000000u | (serial * 7 & 0x3FFFFFF);
            uint Serial(int i) => i % 3 == 0 ? (uint) (i + 1) : 0x40000000u + (uint) i;

            void Notice(uint serial)
            {
                uint rev = Revision(serial);
                bool isHeld = held.TryGetValue(serial, out uint h) && h == (rev & ~0x40000000u);
                OplDebounceCheck.Notice(serial, rev, isHeld);

                if (!isHeld)
                {
                    if (budget > 0)
                    {
                        queue.Request(serial, OplRequestKind.Revision, rev, now);
                    }
                    else
                    {
                        list.Request(serial, OplRequestKind.Revision, rev, now);
                    }
                }
            }

            uint[] batch = new uint[OplRequestQueue.MaxPerPacket];

            for (int frame = 0; frame < 6000; frame++)
            {
                now += 16;

                if (frame < 30)
                {
                    for (int i = frame * 100; i < frame * 100 + 100; i++)
                    {
                        Notice(Serial(i));
                    }
                }

                if (frame % 5 == 0)
                {
                    for (int i = 0; i < Math.Min(3000, frame * 100); i += 3)
                    {
                        Notice(Serial(i));
                    }
                }

                while (answers.Count != 0 && answers.Peek().Tick <= now)
                {
                    uint s = answers.Dequeue().Serial;
                    OplDebounceCheck.Answered(s);
                    held[s] = Revision(s) & ~0x40000000u;
                    list.Answered(s, held[s]);
                    queue.Answered(s, held[s]);
                }

                if (budget > 0)
                {
                    queue.Drain(now, budget, sink, out _);
                }
                else
                {
                    int n = list.Take(batch, now);
                    sink(new ReadOnlySpan<uint>(batch, 0, n));
                }
            }

            OplDebounceCheck.Repeats.Should().Be(0);
            OplDebounceCheck.Requests.Should().Be(3000);
            OplDebounceCheck.Answers.Should().Be(3000);
            OplDebounceCheck.NoticeRepeats.Should().BeGreaterThan(1000);
            _repeats.Should().BeEmpty();
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
