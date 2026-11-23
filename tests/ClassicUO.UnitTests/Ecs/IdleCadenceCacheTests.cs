using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // idle cadence cache (mount-fix item 5): the table itself, then through WorldWriter with idle_on_spawn off.
    public class IdleCadenceCacheTests
    {
        [Fact]
        public void Restore_Returns_The_Due_Time_And_Removes_The_Entry()
        {
            var c = new IdleCadenceCache();
            c.Save(5, 0xC8, 50_000, now: 10_000);

            c.TryRestore(5, 0xC8, 20_000, out long due).Should().BeTrue();
            due.Should().Be(50_000);
            c.Count.Should().Be(0);
            c.TryRestore(5, 0xC8, 20_001, out _).Should().BeFalse("a hit removes the entry");
        }

        [Fact]
        public void Due_Time_That_Passed_Rolls_Forward_In_Idle_Steps()
        {
            var c = new IdleCadenceCache();
            c.Save(5, 0xC8, 20_000, now: 10_000);

            c.TryRestore(5, 0xC8, 59_000, out long due).Should().BeTrue();
            due.Should().BeGreaterThan(59_000);
            due.Should().BeLessThanOrEqualTo(20_000 + 2 * 60_000, "30-60 s per step, at most two steps from 20 s to pass 59 s");
        }

        [Fact]
        public void Entry_Older_Than_60_Seconds_Is_A_Miss_And_Is_Dropped()
        {
            var c = new IdleCadenceCache();
            c.Save(5, 0xC8, 90_000, now: 10_000);

            c.TryRestore(5, 0xC8, 70_001, out _).Should().BeFalse();
            c.Count.Should().Be(0, "dropped at lookup");

            c.Save(6, 0xC8, 90_000, now: 10_000);
            c.TryRestore(6, 0xC8, 70_000, out _).Should().BeTrue("exactly 60 s is still valid");
        }

        [Fact]
        public void Body_Mismatch_Is_A_Fresh_Draw_And_Keeps_The_Entry()
        {
            var c = new IdleCadenceCache();
            c.Save(5, 0xC8, 50_000, now: 10_000);

            c.TryRestore(5, 0xE2, 20_000, out _).Should().BeFalse();
            c.Count.Should().Be(1);
            c.TryRestore(5, 0xC8, 20_000, out _).Should().BeTrue();
        }

        [Fact]
        public void Saving_The_Same_Serial_Replaces_Its_Entry()
        {
            var c = new IdleCadenceCache();
            c.Save(5, 0xC8, 50_000, now: 10_000);
            c.Save(5, 0xE2, 60_000, now: 11_000);

            c.Count.Should().Be(1);
            c.TryRestore(5, 0xE2, 12_000, out long due).Should().BeTrue();
            due.Should().Be(60_000);
        }

        [Fact]
        public void Full_Table_Overwrites_The_Oldest_And_Counts_Unexpired_Overwrites()
        {
            var c = new IdleCadenceCache();

            for (uint i = 1; i <= IdleCadenceCache.Capacity; i++)
            {
                c.Save(i, 0xC8, 900_000, now: 1000 + i);
            }

            c.Count.Should().Be(IdleCadenceCache.Capacity);
            c.HighWater.Should().Be(IdleCadenceCache.Capacity);
            c.UnexpiredOverwrites.Should().Be(0);

            c.Save(10_000, 0xC8, 900_000, now: 10_000);

            c.Count.Should().Be(IdleCadenceCache.Capacity);
            c.UnexpiredOverwrites.Should().Be(1);
            c.TryRestore(1, 0xC8, 10_001, out _).Should().BeFalse("serial 1 was the oldest");
            c.TryRestore(2, 0xC8, 10_001, out _).Should().BeTrue();
            c.TryRestore(10_000, 0xC8, 10_001, out _).Should().BeTrue();
        }

        [Fact]
        public void Overwriting_An_Expired_Entry_Is_Not_Counted()
        {
            var c = new IdleCadenceCache();

            for (uint i = 1; i <= IdleCadenceCache.Capacity; i++)
            {
                c.Save(i, 0xC8, 900_000, now: 1000);
            }

            c.Save(10_000, 0xC8, 900_000, now: 100_000);

            c.UnexpiredOverwrites.Should().Be(0);
            c.HighWater.Should().Be(IdleCadenceCache.Capacity, "the high-water mark keeps the earlier peak");
        }

        [Fact]
        public void Clear_Empties_The_Table()
        {
            var c = new IdleCadenceCache();
            c.Save(5, 0xC8, 50_000, now: 10_000);
            c.Clear();

            c.Count.Should().Be(0);
            c.TryRestore(5, 0xC8, 10_001, out _).Should().BeFalse();
        }
    }

    public partial class WorldWriterMobileTests
    {
        private static void WithIdleOnSpawn(bool on, System.Action body)
        {
            IdleOnSpawn.OverrideForTests = on;

            try
            {
                body();
            }
            finally
            {
                IdleOnSpawn.OverrideForTests = null;
            }
        }

        [Fact]
        public void Idle_Cadence_Is_Restored_After_A_Sweep_When_Idle_On_Spawn_Is_Off()
        {
            WithIdleOnSpawn(false, () =>
            {
                World world = BuildPlayer(out _);
                Time.Ticks = 100_000;
                Mobile m = Spawn(world, NEWMOB, 0xC8);
                var t = m.Timer;
                t.IdleDue = Time.Ticks + 45_000;
                m.WriteTimer(in t);

                world.Writer.SweepMobile(m);
                world.IdleCadence.Count.Should().Be(1);

                Time.Ticks += 10_000;
                Mobile back = Spawn(world, NEWMOB, 0xC8);

                back.Timer.IdleDue.Should().Be(145_000);
                world.IdleCadence.Count.Should().Be(0);

                Mobile other = Spawn(world, NEWMOB + 1, 0xC8);
                world.Writer.RemoveMobile(other);
                Time.Ticks += 5_000;
                Spawn(world, NEWMOB + 1, 0xE2).Timer.IdleDue.Should().BeInRange(Time.Ticks, Time.Ticks + 60_000, "another body is a fresh draw");
            });
        }

        [Fact]
        public void Idle_Cadence_Is_Not_Written_When_Idle_On_Spawn_Is_On()
        {
            WithIdleOnSpawn(true, () =>
            {
                World world = BuildPlayer(out _);
                Mobile m = Spawn(world, NEWMOB, 0xC8);

                world.Writer.RemoveMobile(m);

                world.IdleCadence.Count.Should().Be(0);
                Spawn(world, NEWMOB, 0xC8).Timer.IdleDue.Should().Be(0);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Item_Fade_In_Switch_Marks_Only_New_Items(bool on)
        {
            ItemFadeIn.OverrideForTests = on;

            try
            {
                World world = BuildPlayer(out _);
                Item item = world.GetOrCreateItem(0x4000_7100);

                item.AlphaSnap.Should().Be(!on);

                item.AlphaSnap = false;
                world.GetOrCreateItem(0x4000_7100).AlphaSnap.Should().BeFalse("an item found again is not new");
            }
            finally
            {
                ItemFadeIn.OverrideForTests = null;
            }
        }
    }
}
