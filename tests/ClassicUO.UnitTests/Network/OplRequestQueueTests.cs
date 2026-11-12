using System;
using System.Collections.Generic;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // opl_request_budget: the property-list request queue (order, dedupe, removal, revision re-requests, hover priority,
    // pacing, no allocations while draining).
    public class OplRequestQueueTests
    {
        private readonly List<uint> _sent = new List<uint>();
        private readonly List<int> _packets = new List<int>();
        private readonly Dictionary<uint, int> _distance = new Dictionary<uint, int>();
        private readonly OplRequestQueue _q;
        private readonly OplBatchSink _sink;

        public OplRequestQueueTests()
        {
            _q = new OplRequestQueue { DistanceOf = s => _distance.TryGetValue(s, out int d) ? d : -1 };
            _sink = Record;
        }

        private void Record(ReadOnlySpan<uint> serials)
        {
            _packets.Add(serials.Length);

            foreach (uint s in serials)
            {
                _sent.Add(s);
            }
        }

        private int Drain(uint now, int budget = 100_000) => _q.Drain(now, budget, _sink, out _);

        [Fact]
        public void Nearer_Objects_Go_First_And_Unknown_Last()
        {
            _distance[1] = 30;
            _distance[2] = 5;
            _distance[3] = 0;
            _distance[4] = 17;

            foreach (uint s in new uint[] { 1, 2, 3, 4, 5 })
            {
                _q.Request(s, OplRequestKind.Revision, 1, 0);
            }

            Drain(1000);

            _sent.Should().Equal(3u, 2u, 4u, 1u, 5u);
            _q.PendingCount.Should().Be(0);
        }

        [Fact]
        public void A_Hovered_Object_Jumps_The_Queue_Even_Without_Budget()
        {
            for (uint s = 100; s < 200; s++)
            {
                _q.Request(s, OplRequestKind.Revision, 1, 0);
            }

            // pending far request moved up, and a new one
            _q.Request(150, OplRequestKind.Hover, 0, 0);
            _q.Request(999, OplRequestKind.Gump, 0, 0);

            _q.Drain(0, 0, _sink, out _).Should().BeGreaterThan(0);
            _sent.GetRange(0, 2).Should().Equal(150u, 999u);

            // the moved request's old slot is skipped, not sent twice
            Drain(10_000);
            _sent.Should().HaveCount(101).And.OnlyHaveUniqueItems();
        }

        [Fact]
        public void A_Serial_Is_Sent_Once_While_Pending_Or_In_Flight()
        {
            _q.Request(7, OplRequestKind.Revision, 5, 0).Should().BeTrue();
            _q.Request(7, OplRequestKind.Revision, 5, 0).Should().BeFalse();
            _q.Request(7, OplRequestKind.Hover, 0, 0).Should().BeFalse();
            Drain(10);
            _sent.Should().Equal(7u);

            // in flight: a hover or the same revision does not send again
            _q.Request(7, OplRequestKind.Hover, 0, 20).Should().BeFalse();
            _q.Request(7, OplRequestKind.Revision, 5, 20).Should().BeFalse();
            Drain(30);
            _sent.Should().Equal(7u);
            _q.IsInFlight(7).Should().BeTrue();
        }

        [Fact]
        public void A_New_Revision_Requests_Again_While_In_Flight_And_After_The_Answer()
        {
            _q.Request(7, OplRequestKind.Revision, 5, 0);
            Drain(10);
            _q.Request(7, OplRequestKind.Revision, 6, 20).Should().BeTrue();
            Drain(30);
            _sent.Should().Equal(7u, 7u);

            _q.Answered(7, 6);
            _q.IsInFlight(7).Should().BeFalse();
            _q.Request(7, OplRequestKind.Revision, 7, 40).Should().BeTrue();
            Drain(50);
            _sent.Should().Equal(7u, 7u, 7u);
        }

        [Fact]
        public void An_Answer_Does_Not_Cancel_A_Newer_Pending_Request()
        {
            _q.Request(7, OplRequestKind.Revision, 5, 0);
            Drain(10);
            _q.Request(7, OplRequestKind.Revision, 6, 20);
            _q.Answered(7, 5);
            _q.IsPending(7).Should().BeTrue();
            Drain(30);
            _sent.Should().Equal(7u, 7u);
        }

        [Fact]
        public void An_Answer_Cancels_A_Pending_Request_It_Satisfies()
        {
            // sent for revision 5, notified of 6, and the answer already carries 6 (0xDC's 0x40000000 flag ignored)
            _q.Request(7, OplRequestKind.Revision, 5, 0);
            Drain(10);
            _q.Request(7, OplRequestKind.Revision, 0x40000006, 20);
            _q.Answered(7, 6);
            _q.IsPending(7).Should().BeFalse();
            _q.PendingCount.Should().Be(0);
            _q.Satisfied.Should().Be(1);

            // a hover request is satisfied by any answer
            _q.Request(8, OplRequestKind.Hover, 0, 20);
            _q.Answered(8, 3);
            _q.PendingCount.Should().Be(0);

            Drain(30);
            _sent.Should().Equal(7u);
        }

        [Fact]
        public void Enqueue_Remove_Cycles_Keep_The_Rings_Bounded()
        {
            int start = _q.RingCapacity;

            for (uint i = 0; i < 200_000; i++)
            {
                uint s = 1 + i % 5000;
                _distance[s] = (int) (i % 25);
                _q.Request(s, OplRequestKind.Revision, i, i);

                if (i % 3 == 0)
                {
                    // promotion leaves a stale slot in the distance ring
                    _q.Request(s, OplRequestKind.Hover, 0, i);
                }

                _q.Remove(s);
            }

            _q.PendingCount.Should().Be(0);
            _q.RingCapacity.Should().Be(start);

            // the same serial queued again and again while pending once: one live slot, the rest compacted away
            for (uint i = 0; i < 100_000; i++)
            {
                _q.Request(9, OplRequestKind.Revision, i, i);
                _q.Remove(9);
            }

            _q.Request(9, OplRequestKind.Revision, 1, 0);
            _q.RingCapacity.Should().Be(start);
            Drain(10);
            _sent.Should().Equal(9u);
        }

        [Fact]
        public void Compaction_Keeps_The_Order_And_Sends_Each_Once()
        {
            // 3,000 far requests with every other one removed: the far ring (1,024) compacts and grows in between
            for (uint s = 1; s <= 3000; s++)
            {
                _q.Request(s, OplRequestKind.Revision, 1, 0);

                if (s % 2 == 0)
                {
                    _q.Remove(s);
                }
            }

            Drain(0);
            Drain(1000);
            var expected = new List<uint>();

            for (uint s = 1; s <= 3000; s += 2)
            {
                expected.Add(s);
            }

            _sent.Should().Equal(expected);
        }

        [Fact]
        public void An_Unanswered_Hover_Is_Retried_After_The_Retry_Time()
        {
            _q.Request(7, OplRequestKind.Hover, 0, 0);
            Drain(0);
            _q.Request(7, OplRequestKind.Hover, 0, OplRequestQueue.RetryMs - 1).Should().BeFalse();
            _q.Request(7, OplRequestKind.Hover, 0, OplRequestQueue.RetryMs).Should().BeTrue();
            Drain(OplRequestQueue.RetryMs);
            _sent.Should().Equal(7u, 7u);
        }

        [Fact]
        public void A_Removed_Object_Is_Not_Requested()
        {
            _q.Request(7, OplRequestKind.Revision, 1, 0);
            _q.Request(8, OplRequestKind.Hover, 0, 0);
            _q.Request(9, OplRequestKind.Revision, 1, 0);
            _q.Remove(7);
            _q.Remove(8);
            _q.PendingCount.Should().Be(1);
            Drain(10);
            _sent.Should().Equal(9u);
            _q.Dropped.Should().Be(2);

            // back in range: requested again, once
            _q.Request(7, OplRequestKind.Revision, 1, 20).Should().BeTrue();
            Drain(30);
            _sent.Should().Equal(9u, 7u);
        }

        [Fact]
        public void The_Budget_Paces_Full_Packets_And_Everything_Is_Sent_Once()
        {
            const int total = 2000;

            for (uint s = 1; s <= total; s++)
            {
                _distance[s] = (int) (s % 40);
                _q.Request(s, OplRequestKind.Revision, 1, 0);
            }

            int maxFrame = 0;
            uint now = 0;

            while (_q.PendingCount != 0 && now < 60_000)
            {
                now += 16;
                int before = _sent.Count;
                _q.Drain(now, 600, _sink, out _);
                maxFrame = Math.Max(maxFrame, _sent.Count - before);
            }

            _sent.Should().HaveCount(total).And.OnlyHaveUniqueItems();
            maxFrame.Should().BeLessOrEqualTo(60);
            // 600 per second: about 3.3 s for 2,000
            now.Should().BeInRange(3000u, 4000u);
            _packets.GetRange(0, _packets.Count - 1).Should().OnlyContain(n => n == OplRequestQueue.MaxPerPacket);
        }

        [Fact]
        public void Fewer_Than_A_Packet_Left_Are_Sent_Without_Waiting_For_A_Full_One()
        {
            _q.Drain(0, 60, _sink, out _);
            _q.Request(1, OplRequestKind.Revision, 1, 0);
            _q.Request(2, OplRequestKind.Revision, 1, 0);
            _q.Drain(16, 60, _sink, out _);
            _sent.Should().Equal(1u, 2u);
        }

        [Fact]
        public void Draining_Allocates_Nothing()
        {
            for (uint s = 1; s <= 5000; s++)
            {
                _q.Request(s, OplRequestKind.Revision, 1, 0);
            }

            OplBatchSink noop = static _ => { };
            _q.Drain(0, 600, noop, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (uint f = 1; f < 200; f++)
            {
                _q.Drain(f * 16, 600, noop, out _);
            }

            GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
        }

        [Fact]
        public void Clear_Forgets_Everything()
        {
            _q.Request(1, OplRequestKind.Revision, 1, 0);
            _q.Request(2, OplRequestKind.Hover, 0, 0);
            _q.Clear();
            _q.PendingCount.Should().Be(0);
            Drain(10);
            _sent.Should().BeEmpty();
        }
    }

    [CollectionDefinition(nameof(OplRequestPathCollection), DisableParallelization = true)]
    public class OplRequestPathCollection { }

    // PacketHandlers.AddMegaClilocRequest in both switch states; the checker against the queue and against the old list
    [Collection(nameof(OplRequestPathCollection))]
    public class OplRequestPathTests : IDisposable
    {
        public OplRequestPathTests() => PacketHandlers.ResetMegaClilocRequestsForTests();

        public void Dispose()
        {
            OplRequestQueue.BudgetOverride = null;
            PacketHandlers.ResetMegaClilocRequestsForTests();
#if CUO_DEVTOOLS
            OplSpreadCheck.Override = null;
            OplSpreadCheck.WindowOverride = null;
            OplSpreadCheck.Reset();
#endif
        }

        [Fact]
        public void Switch_Off_Keeps_The_Old_List()
        {
            OplRequestQueue.BudgetOverride = 0;
            PacketHandlers.AddMegaClilocRequest(0x40000001, OplRequestKind.Revision, 3);
            PacketHandlers.AddMegaClilocRequest(0x40000001, OplRequestKind.Revision, 3);
            PacketHandlers.AddMegaClilocRequest(0x40000002);

            PacketHandlers.OldRequests.PendingCount.Should().Be(2);
            PacketHandlers.OplQueue.Should().BeNull();

            // a removed object is not requested; an answer ends the in-flight request
            PacketHandlers.OnOplRemoved(0x40000001);
            PacketHandlers.OldRequests.PendingCount.Should().Be(1);
            PacketHandlers.OldRequests.IsPending(0x40000002).Should().BeTrue();
            PacketHandlers.OnOplCleared();
            PacketHandlers.OldRequests.PendingCount.Should().Be(0);
        }

        [Fact]
        public void Switch_On_Uses_The_Queue()
        {
            OplRequestQueue.BudgetOverride = 300;
            PacketHandlers.AddMegaClilocRequest(0x40000001, OplRequestKind.Revision, 3);
            PacketHandlers.AddMegaClilocRequest(0x40000001, OplRequestKind.Revision, 3);
            PacketHandlers.AddMegaClilocRequest(0x40000002);

            PacketHandlers.OldRequests.PendingCount.Should().Be(0);
            PacketHandlers.OplQueue.PendingCount.Should().Be(2);

            PacketHandlers.OnOplRemoved(0x40000001);
            PacketHandlers.OplQueue.PendingCount.Should().Be(1);
            PacketHandlers.OnOplCleared();
            PacketHandlers.OplQueue.PendingCount.Should().Be(0);
        }

#if CUO_DEVTOOLS
        // a workload of revision, hover and gump requests, removals and answers through the queue: every request is sent
        // once, none twice, none for a removed object
        [Fact]
        public void Checker_Finds_No_Misses_Or_Duplicates_On_The_Queue()
        {
            OplSpreadCheck.Reset();
            OplSpreadCheck.Override = true;
            OplSpreadCheck.WindowOverride = 20_000;
            var mismatches = new List<string>();
            OplSpreadCheck.MismatchObserver = mismatches.Add;

            var q = new OplRequestQueue { DistanceOf = s => (int) (s % 25) };
            OplBatchSink sink = serials =>
            {
                foreach (uint s in serials)
                {
                    OplSpreadCheck.Sent(s, _now);
                }
            };

            var rnd = new Random(1234);
            _now = 0;

            void Demand(uint s, OplRequestKind k, uint rev)
            {
                OplSpreadCheck.Demand(s, k, rev, _now);
                q.Request(s, k, rev, _now);
            }

            for (int frame = 0; frame < 3000; frame++)
            {
                _now += 16;

                if (frame < 300)
                {
                    for (int i = 0; i < 40; i++)
                    {
                        Demand((uint) rnd.Next(1, 20_000), OplRequestKind.Revision, (uint) rnd.Next(1, 3));
                    }
                }

                // a hovered object asks every frame until answered
                uint hovered = (uint) (frame / 50 + 30_000);
                Demand(hovered, OplRequestKind.Hover, 0);

                if (rnd.Next(4) == 0)
                {
                    uint s = (uint) rnd.Next(1, 20_000);
                    q.Remove(s);
                    OplSpreadCheck.Removed(s);
                }

                int sent = q.Drain(_now, 600, sink, out int packets);

                if (frame % 3 == 0)
                {
                    q.Answered(hovered, 0);
                    OplSpreadCheck.Answered(hovered, 0);
                }

                OplSpreadCheck.FrameEnd(_now, packets, sent, true, 600);
            }

            OplSpreadCheck.Misses.Should().Be(0);
            OplSpreadCheck.Duplicates.Should().Be(0);
            OplSpreadCheck.Unrequested.Should().Be(0);
            mismatches.Should().BeEmpty();
            OplSpreadCheck.Sends.Should().Be(OplSpreadCheck.Demands - OplSpreadCheck.Dropped);
            OplSpreadCheck.MaxFrameSerials.Should().BeLessOrEqualTo(60 + OplRequestQueue.MaxPerPacket);
            OplSpreadCheck.MaxUrgentLatencyMs.Should().BeLessOrEqualTo(16);
        }

        private static uint _now;

        [Fact]
        public void Checker_Reports_Duplicates_Unrequested_Sends_And_Misses()
        {
            OplSpreadCheck.Reset();
            OplSpreadCheck.Override = true;
            OplSpreadCheck.WindowOverride = 1000;

            // old path: a hover after the send, while unanswered, re-sends it
            OplSpreadCheck.Demand(1, OplRequestKind.Hover, 0, 0);
            OplSpreadCheck.Sent(1, 0);
            OplSpreadCheck.Demand(1, OplRequestKind.Hover, 0, 16);
            OplSpreadCheck.Sent(1, 16);
            OplSpreadCheck.Duplicates.Should().Be(1);

            // a removed object's request sent anyway
            OplSpreadCheck.Demand(2, OplRequestKind.Revision, 1, 0);
            OplSpreadCheck.Removed(2);
            OplSpreadCheck.Sent(2, 20);
            OplSpreadCheck.Unrequested.Should().Be(1);
            OplSpreadCheck.Dropped.Should().Be(1);

            // a revision change while in flight is a new request, not a duplicate
            OplSpreadCheck.Demand(3, OplRequestKind.Revision, 1, 0);
            OplSpreadCheck.Sent(3, 0);
            OplSpreadCheck.Demand(3, OplRequestKind.Revision, 2, 10);
            OplSpreadCheck.Sent(3, 10);
            OplSpreadCheck.Duplicates.Should().Be(1);

            // an answer that already carries the pending revision satisfies it: a later send is redundant
            OplSpreadCheck.Demand(5, OplRequestKind.Revision, 1, 0);
            OplSpreadCheck.Sent(5, 0);
            OplSpreadCheck.Demand(5, OplRequestKind.Revision, 2, 10);
            OplSpreadCheck.Answered(5, 2);
            OplSpreadCheck.Satisfied.Should().Be(1);
            OplSpreadCheck.Sent(5, 20);
            OplSpreadCheck.Unrequested.Should().Be(2);

            // hover latency counts from the hover that promoted a waiting revision request
            OplSpreadCheck.Demand(6, OplRequestKind.Revision, 1, 0);
            OplSpreadCheck.Demand(6, OplRequestKind.Hover, 0, 3000);
            OplSpreadCheck.Sent(6, 3010);
            OplSpreadCheck.MaxUrgentLatencyMs.Should().Be(10);

            // never sent
            OplSpreadCheck.Demand(4, OplRequestKind.Revision, 1, 0);
            OplSpreadCheck.FrameEnd(5000, 2, 30, true, 0);
            OplSpreadCheck.Misses.Should().Be(1);
            OplSpreadCheck.MaxFramePackets.Should().Be(2);
            OplSpreadCheck.MaxFrameSerials.Should().Be(30);

            // a miss is counted once
            OplSpreadCheck.FrameEnd(20000, 0, 0, true, 0);
            OplSpreadCheck.Misses.Should().Be(1);
        }
#endif
    }
}
