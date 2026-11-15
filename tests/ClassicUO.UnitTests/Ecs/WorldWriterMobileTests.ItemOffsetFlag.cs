using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // perf-17 (item_offset_flag): ItemOffsets.Push sets Item.ItemOffsetHeld (a non-zero offset written), and with the switch
    // on the readers test it instead of the mirror's count. Both switch states read the component's value at every step of
    // a boat run with a second, unrelated item in the world; CUO_ITEM_OFFSET_FLAG_CHECK runs both paths and counts a
    // difference (none here; one for a component planted without Push).
    public partial class WorldWriterMobileTests
    {
        private static void ResetItemOffsetFlag()
        {
#if CUO_DEVTOOLS
            ItemOffsets.ResetForTests();
#else
            ItemOffsets.OverrideForTests = null;
            ItemOffsets.BeginFrame();
#endif
        }

        private static void SetItemOffsetFlag(bool on, bool check)
        {
            ItemOffsets.OverrideForTests = on;
#if CUO_DEVTOOLS
            ItemOffsets.CheckOverrideForTests = check;
#endif
            ItemOffsets.BeginFrame();
        }

        // the readers against the component, and the flag set exactly when the item has one
        private static void AssertFlagReads(World world, string because)
        {
            foreach (uint serial in new[] { BOAT, BAG })
            {
                Item it = world.Items.Get(serial);

                if (it == null)
                {
                    continue;
                }

                world.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e).Should().BeTrue();
                bool has = e.TryGetComponent(out ItemOffset c);
                Vector3 value = has ? new Vector3(c.X, c.Y, c.Z) : Vector3.Zero;

                it.ItemOffsetHeld.Should().Be(has, $"0x{serial:X8}: the flag follows the component ({because})");
                ItemOffsets.Read(it).Should().Be(value, $"0x{serial:X8} read ({because})");
                it.ScreenOffset.Should().Be(value, $"0x{serial:X8} screen offset ({because})");
                ItemOffsets.ReadRow(it, true, EcsRead.Row(e)).Should().Be(value, $"0x{serial:X8} row read ({because})");
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Item_Offset_Flag_Reads_The_Component_In_Both_Switch_States(bool on, bool check)
        {
            try
            {
                SetItemOffsetFlag(on, check);
                World world = BuildOffsetBoat();
                BoatMovingManager boats = world.BoatMovingManager;
                Item boat = world.Items.Get(BOAT), bag = world.Items.Get(BAG);
                AssertFlagReads(world, "start");

                Time.Ticks = NOW + 100;
                boats.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);
                boats.PushItemToList(BOAT, MOB, 1, 0, -2);
                boats.PushItemToList(BOAT, BAG, -1, 1, 0);
                Time.Ticks = NOW;

                foreach (uint t in new uint[] { 200, 300 })
                {
                    Frame(world, NOW + t);
                    ItemOffsets.Read(boat).Should().NotBe(Vector3.Zero, "mid-step");
                    AssertFlagReads(world, $"mid-step {t}");
                }

                Frame(world, NOW + 700);
                (ItemOffsets.Read(boat), ItemOffsets.Read(bag)).Should().Be((Vector3.Zero, Vector3.Zero));
                AssertFlagReads(world, "stopped");

                // a passenger left behind keeps its offset; the boat moves on
                Time.Ticks = NOW + 1100;
                boats.AddStep(BOAT, 3, Direction.East, Direction.East, 1002, 1001, 1);
                boats.PushItemToList(BOAT, MOB, 1, 0, 0);
                boats.PushItemToList(BOAT, BAG, -1, 1, 0);
                Frame(world, NOW + 1300);
                boats.ClearEntities(BOAT);
                boats.PushItemToList(BOAT, MOB, 1, 0, 0);
                Frame(world, NOW + 1400);
                bag.ItemOffsetHeld.Should().BeTrue("left with an offset");
                AssertFlagReads(world, "passenger left");

                world.Writer.ResetBoatOffset(bag);
                AssertFlagReads(world, "bag reset");
                bag.ItemOffsetHeld.Should().BeFalse();

#if CUO_DEVTOOLS
                ItemOffsets.Mismatches.Should().Be(0);
#endif
                world.Writer.Violations.Should().Be(0);
            }
            finally
            {
                ResetItemOffsetFlag();
            }
        }

        [Fact]
        public void Item_Offset_Flag_Is_Cleared_For_Reuse()
        {
            World world = BuildOffsetBoat();
            Item bag = world.Items.Get(BAG);
            world.Writer.FollowBoat(bag, new Vector3(1, 2, 3));
            bag.ItemOffsetHeld.Should().BeTrue();

            bag.ResetForReuse();
            bag.ItemOffsetHeld.Should().BeFalse("a reused instance starts without an offset");
        }

        [Fact]
        public void Item_Offset_Flag_Switch_Reads_The_Setting()
        {
            try
            {
                ItemOffsets.OverrideForTests = null;
                ItemOffsets.Enabled.Should().Be(ClassicUO.Configuration.Settings.GlobalSettings.ItemOffsetFlag);
                new ClassicUO.Configuration.Settings().ItemOffsetFlag.Should().BeFalse("default off");
            }
            finally
            {
                ResetItemOffsetFlag();
            }
        }

#if CUO_DEVTOOLS
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Item_Offset_Flag_Checker_Counts_A_Component_The_Flag_Misses(bool on)
        {
            try
            {
                SetItemOffsetFlag(on, check: true);
                World world = BuildOffsetBoat();
                Item bag = world.Items.Get(BAG);

                // planted past Push: the count path sees it, the flag does not
                world.Ecs.Mirror.TryGetEntity(BAG, false, out EcsEntity e).Should().BeTrue();
                e.AddComponent(new ItemOffset { X = 4, Y = 5, Z = 0 });
                world.Ecs.Mirror.ItemOffsetCount++;

                Vector3 read = ItemOffsets.Read(bag);
                read.Should().Be(on ? Vector3.Zero : new Vector3(4, 5, 0), "the switch's answer");
                ItemOffsets.Mismatches.Should().BeGreaterThan(0);
            }
            finally
            {
                ResetItemOffsetFlag();
            }
        }
#endif
    }
}
