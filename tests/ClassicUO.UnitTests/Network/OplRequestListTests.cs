using System;
using System.Collections.Generic;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // opl_request_budget 0: the old path's request list debounces like the queue (one request per serial and revision)
    public class OplRequestListTests
    {
        private readonly OplRequestList _l = new OplRequestList();
        private readonly List<uint> _sent = new List<uint>();

        private int Take(uint now)
        {
            Span<uint> batch = stackalloc uint[OplRequestQueue.MaxPerPacket];
            int n = _l.Take(batch, now);

            for (int i = 0; i < n; i++)
            {
                _sent.Add(batch[i]);
            }

            return n;
        }

        [Fact]
        public void Requests_Go_Out_In_Order_Fifteen_Per_Take()
        {
            for (uint s = 1; s <= 20; s++)
            {
                _l.Request(s, OplRequestKind.Revision, 1, 0);
            }

            Take(0).Should().Be(15);
            Take(16).Should().Be(5);
            Take(32).Should().Be(0);
            _sent.Should().Equal(Seq(1, 20));
        }

        [Fact]
        public void A_Repeated_0xDC_With_The_Same_Revision_Is_Not_Requested_Again_While_In_Flight()
        {
            // ModernUO sends the unchanged revision again on every step of a mobile (Mobile.ProcessDelta)
            _l.Request(1, OplRequestKind.Revision, 0x40000005, 0).Should().BeTrue();
            _l.Request(1, OplRequestKind.Revision, 0x40000005, 0).Should().BeFalse();
            Take(0);
            _l.IsInFlight(1).Should().BeTrue();

            _l.Request(1, OplRequestKind.Revision, 0x40000005, 100).Should().BeFalse();
            _l.Request(1, OplRequestKind.Revision, 0x00000005, 200).Should().BeFalse();
            Take(300).Should().Be(0);
            _sent.Should().Equal(1u);
        }

        [Fact]
        public void A_New_Revision_Is_Requested_Again_While_In_Flight_And_After_The_Answer()
        {
            _l.Request(1, OplRequestKind.Revision, 1, 0);
            Take(0);
            _l.Request(1, OplRequestKind.Revision, 2, 10).Should().BeTrue();
            Take(20);
            _l.Answered(1, 2);
            _l.Request(1, OplRequestKind.Revision, 3, 30).Should().BeTrue();
            Take(40);
            _sent.Should().Equal(1u, 1u, 1u);
        }

        [Fact]
        public void A_Hover_Retries_Only_After_The_Retry_Time()
        {
            _l.Request(7, OplRequestKind.Hover, 0, 0);
            Take(0);

            for (uint t = 16; t < OplRequestQueue.RetryMs; t += 16)
            {
                _l.Request(7, OplRequestKind.Hover, 0, t).Should().BeFalse();
            }

            _l.Request(7, OplRequestKind.Hover, 0, OplRequestQueue.RetryMs).Should().BeTrue();
            Take(OplRequestQueue.RetryMs);
            _sent.Should().Equal(7u, 7u);
        }

        [Fact]
        public void A_Removed_Or_Satisfied_Request_Is_Skipped_And_Can_Come_Back()
        {
            _l.Request(1, OplRequestKind.Revision, 1, 0);
            _l.Request(2, OplRequestKind.Revision, 1, 0);
            _l.Request(3, OplRequestKind.Hover, 0, 0);
            _l.Remove(1);
            // a pushed answer (vendor buy list) satisfies the pending hover
            _l.Answered(3, 9);
            _l.PendingCount.Should().Be(1);
            Take(0);
            _sent.Should().Equal(2u);

            // back in range: requested again, once, though its old slot was skipped
            _l.Request(1, OplRequestKind.Revision, 1, 10).Should().BeTrue();
            Take(10);
            _sent.Should().Equal(2u, 1u);
        }

        [Fact]
        public void An_Answer_For_Another_Revision_Keeps_The_Pending_Request()
        {
            _l.Request(1, OplRequestKind.Revision, 1, 0);
            Take(0);
            _l.Request(1, OplRequestKind.Revision, 2, 5);
            _l.Answered(1, 1);
            _l.IsPending(1).Should().BeTrue();
            Take(10);
            _sent.Should().Equal(1u, 1u);
        }

        [Fact]
        public void A_Long_Session_Keeps_The_Order_List_Bounded_And_Sends_Each_Once()
        {
            uint now = 0;

            for (uint s = 1; s <= 30_000; s++)
            {
                _l.Request(s, OplRequestKind.Revision, 1, now);

                if (s % 10 == 0)
                {
                    Take(now += 16);
                }
            }

            while (Take(now += 16) != 0)
            {
            }

            _sent.Should().Equal(Seq(1, 30_000));
            _l.PendingCount.Should().Be(0);
        }

        [Fact]
        public void Taking_Allocates_Nothing()
        {
            for (uint s = 1; s <= 5000; s++)
            {
                _l.Request(s, OplRequestKind.Revision, 1, 0);
            }

            Span<uint> batch = stackalloc uint[OplRequestQueue.MaxPerPacket];
            _l.Take(batch, 0);
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (uint f = 1; f < 200; f++)
            {
                _l.Take(batch, f * 16);
            }

            GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
        }

        [Fact]
        public void Clear_Forgets_Everything()
        {
            _l.Request(1, OplRequestKind.Revision, 1, 0);
            Take(0);
            _l.Request(2, OplRequestKind.Revision, 1, 0);
            _l.Clear();
            _l.PendingCount.Should().Be(0);
            Take(0).Should().Be(0);
            _l.Request(1, OplRequestKind.Revision, 1, 0).Should().BeTrue();
        }

        private static IEnumerable<uint> Seq(uint from, uint to)
        {
            for (uint s = from; s <= to; s++)
            {
                yield return s;
            }
        }
    }
}
