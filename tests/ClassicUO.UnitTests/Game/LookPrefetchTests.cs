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

            for (uint i = 1; i <= LookPrefetch.ItemCapacity + 3; i++)
            {
                LookPrefetch.Note(i, LookPrefetch.Kind.Item);
            }

            LookPrefetch.Count.Should().Be(LookPrefetch.ItemCapacity);
            LookPrefetch.Dropped.Should().Be(3);
            LookPrefetch.Items.HighWater.Should().Be(LookPrefetch.ItemCapacity);
            LookPrefetch.TryTake(out uint s, out _).Should().BeTrue();
            s.Should().Be(4u);
        }

        [Fact]
        public void On_MobileAndEquipNotesComeBeforeItemNotes()
        {
            LookPrefetch.OverrideForTests = true;
            LookPrefetch.Note(1, LookPrefetch.Kind.Item);
            LookPrefetch.Note(2, LookPrefetch.Kind.Mobile);
            LookPrefetch.Note(3, LookPrefetch.Kind.Item);
            LookPrefetch.Note(4, LookPrefetch.Kind.Equip);

            LookPrefetch.TryTake(out uint s, out var k).Should().BeTrue();
            (s, k).Should().Be((2u, LookPrefetch.Kind.Mobile));
            LookPrefetch.TryTake(out s, out k).Should().BeTrue();
            (s, k).Should().Be((4u, LookPrefetch.Kind.Equip));
            LookPrefetch.TryTake(out s, out k).Should().BeTrue();
            (s, k).Should().Be((1u, LookPrefetch.Kind.Item));
            LookPrefetch.TryTake(out s, out k).Should().BeTrue();
            (s, k).Should().Be((3u, LookPrefetch.Kind.Item));
            LookPrefetch.TryTake(out _, out _).Should().BeFalse();
        }

        [Fact]
        public void On_CrowdArrivalBurstFitsWithoutDrops()
        {
            // the large scene's crowd (p5 cw:lswalk): ~5k Mobile + ~3k Equip and 29.2k Item notes before a drain
            LookPrefetch.OverrideForTests = true;

            for (uint i = 1; i <= 8000; i++)
            {
                LookPrefetch.Note(i, i % 8 < 5 ? LookPrefetch.Kind.Mobile : LookPrefetch.Kind.Equip);
            }

            for (uint i = 1; i <= 29238; i++)
            {
                LookPrefetch.Note(0x40000000 + i, LookPrefetch.Kind.Item);
            }

            LookPrefetch.Dropped.Should().Be(0);
            LookPrefetch.Count.Should().Be(8000 + 29238);
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
