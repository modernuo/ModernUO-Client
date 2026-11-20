using System;
using ClassicUO.Game;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // LookPrefetch is static: one collection, never beside each other
    [Collection("LookPrefetch")]
    public class LookPrefetchTests : IDisposable
    {
        public LookPrefetchTests() => LookPrefetch.ResetForTests();

        public void Dispose()
        {
            LookPrefetch.OverrideForTests = null;
            LookPrefetch.ResetForTests();
        }

        [Fact]
        public void Off_NoteQueuesNothing()
        {
            LookPrefetch.OverrideForTests = false;
            LookPrefetch.Note(0x1234, LookPrefetch.Kind.Mobile);
            LookPrefetch.Count.Should().Be(0);
        }

        [Fact]
        public void On_NotesComeOutInArrivalOrder()
        {
            LookPrefetch.OverrideForTests = true;
            LookPrefetch.Note(1, LookPrefetch.Kind.Mobile);
            LookPrefetch.Note(2, LookPrefetch.Kind.Equip);

            LookPrefetch.TryTake(out uint s, out var k).Should().BeTrue();
            (s, k).Should().Be((1u, LookPrefetch.Kind.Mobile));
            LookPrefetch.TryTake(out s, out k).Should().BeTrue();
            (s, k).Should().Be((2u, LookPrefetch.Kind.Equip));
            LookPrefetch.TryTake(out _, out _).Should().BeFalse();
        }

        [Fact]
        public void On_FullQueueDropsTheOldest()
        {
            LookPrefetch.OverrideForTests = true;

            for (uint i = 1; i <= LookPrefetch.Capacity + 3; i++)
            {
                LookPrefetch.Note(i, LookPrefetch.Kind.Item);
            }

            LookPrefetch.Count.Should().Be(LookPrefetch.Capacity);
            LookPrefetch.Dropped.Should().Be(3);
            LookPrefetch.TryTake(out uint s, out _).Should().BeTrue();
            s.Should().Be(4u);
        }

        [Fact]
        public void Drain_SwitchedOffMeanwhile_ClearsTheQueue()
        {
            LookPrefetch.OverrideForTests = true;
            LookPrefetch.Note(7, LookPrefetch.Kind.Mobile);
            LookPrefetch.OverrideForTests = false;

            LookPrefetch.Drain(null);

            LookPrefetch.Count.Should().Be(0);
        }
    }
}
