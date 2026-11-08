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
    // ecs_item_offset: the boat operations write an item's offset (the boat's, the bag's on its deck) into its ItemOffset
    // component from the values they write to the Item; ItemOffsets.Read serves the extract's Gather and the item pick from it,
    // and CUO_ITEM_OFFSET_CHECK compares the two. The boat runs through the real BoatMovingManager with the switch off or on:
    // under way (mid-step), stop (a step completes), a turn (ClearSteps' reset), a passenger leaving the list, the boat removed.
    public partial class WorldWriterMobileTests
    {
        private static World BuildOffsetBoat(bool on)
        {
            World world = BuildBoat(Start.Standing);
            world.ItemOffsetOverride = on;
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        private static bool TryItemOffset(World world, uint serial, out ItemOffset c)
        {
            c = default;

            return world.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e) && e.TryGetComponent(out c);
        }

        // every item of the boat: the component equals the Item's offset (absent = zero) with the switch on, is never added
        // with it off; the readers' answer is the Item's offset either way, and the checker finds nothing
        private static void AssertItemOffsets(World world, bool on, string because)
        {
            foreach (uint serial in new[] { BOAT, BAG })
            {
                Item it = world.Items.Get(serial);

                if (it == null)
                {
                    TryItemOffset(world, serial, out _).Should().BeFalse($"0x{serial:X8} removed: no record ({because})");

                    continue;
                }

                world.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e).Should().BeTrue($"0x{serial:X8} has a record ({because})");
                bool has = e.TryGetComponent(out ItemOffset c);

                if (on)
                {
                    new Vector3(c.X, c.Y, c.Z).Should().Be(it.Offset, $"0x{serial:X8} component ({because})");
                }
                else
                {
                    has.Should().BeFalse($"0x{serial:X8} switch off: no component ({because})");
                }

                ItemOffsets.Read(it).Should().Be(it.Offset, $"0x{serial:X8} read ({because})");
            }

            // a mobile passenger's offset is its Steps component (T6c), never an ItemOffset
            world.Ecs.Mirror.TryGetEntity(MOB, true, out EcsEntity m).Should().BeTrue();
            m.HasComponent<ItemOffset>().Should().BeFalse(because);
        }

        private static void Frame(World world, uint ticks)
        {
            Time.Ticks = ticks;
            world.BoatMovingManager.Update();
            Time.Ticks = NOW;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Boat_Operations_Write_The_ItemOffset_Component_With_The_Switch_On(bool on)
        {
            ItemOffsets.CheckOverride = true;
            ItemOffsets.ResetCounters();

            try
            {
                World world = BuildOffsetBoat(on);
                BoatMovingManager boats = world.BoatMovingManager;
                Item boat = world.Items.Get(BOAT), bag = world.Items.Get(BAG);
                AssertItemOffsets(world, on, "start");
                TryItemOffset(world, BOAT, out _).Should().BeFalse("no component before the first non-zero offset");

                // under way: a step east with the mobile and the bag on deck, two frames mid-step
                Time.Ticks = NOW + 100;
                boats.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);
                boats.PushItemToList(BOAT, MOB, 1, 0, -2);
                boats.PushItemToList(BOAT, BAG, -1, 1, 0);
                Time.Ticks = NOW;

                foreach (uint t in new uint[] { 200, 300 })
                {
                    Frame(world, NOW + t);
                    boat.Offset.Should().NotBe(Vector3.Zero, "mid-step");
                    bag.Offset.Should().Be(boat.Offset);
                    AssertItemOffsets(world, on, $"mid-step {t}");
                }

                // stop: the step completes, every offset back to zero (the component stays, zero)
                Frame(world, NOW + 700);
                boat.X.Should().Be(1001, "the step completed");
                (boat.Offset, bag.Offset).Should().Be((Vector3.Zero, Vector3.Zero));
                AssertItemOffsets(world, on, "stopped");
                TryItemOffset(world, BOAT, out _).Should().Be(on, "the component is kept once added");

                // a second step, mid-way, then a turn: the multi reload's ClearSteps resets the boat and its passengers
                Time.Ticks = NOW + 800;
                boats.AddStep(BOAT, 3, Direction.Down, Direction.Down, 1002, 1001, 1);
                boats.PushItemToList(BOAT, MOB, 1, 0, 0);
                boats.PushItemToList(BOAT, BAG, -1, 1, 0);
                Frame(world, NOW + 1000);
                boat.Offset.Should().NotBe(Vector3.Zero, "mid-step");
                AssertItemOffsets(world, on, "second step");
                boats.ClearSteps(BOAT);
                (boat.Offset, bag.Offset).Should().Be((Vector3.Zero, Vector3.Zero));
                AssertItemOffsets(world, on, "turned");

                // a passenger leaves: the next 0xF6 lists only the mobile; the bag keeps the offset it had, in both places
                Time.Ticks = NOW + 1100;
                boats.AddStep(BOAT, 3, Direction.East, Direction.East, 1003, 1001, 1);
                boats.PushItemToList(BOAT, MOB, 1, 0, 0);
                boats.PushItemToList(BOAT, BAG, -1, 1, 0);
                Frame(world, NOW + 1300);
                Vector3 left = bag.Offset;
                left.Should().NotBe(Vector3.Zero);
                boats.ClearEntities(BOAT);
                boats.PushItemToList(BOAT, MOB, 1, 0, 0);
                Frame(world, NOW + 1400);
                bag.Offset.Should().Be(left, "no longer carried");
                boat.Offset.Should().NotBe(left, "the boat moved on");
                AssertItemOffsets(world, on, "passenger left");

                // the boat removed (0x1D) mid-step: its record goes, the manager drops its steps, the bag keeps its offset
                world.Writer.RemoveWorldItem(boat, out _);
                Frame(world, NOW + 1500);
                world.Items.Get(BOAT).Should().BeNull();
                AssertItemOffsets(world, on, "boat removed");

                ItemOffsets.Diffs.Should().Be(0);
                (ItemOffsets.Checks > 0).Should().Be(on, "the checker compares only what the ECS served");
                (ItemOffsets.Reads > 0).Should().Be(on, "ECS reads only with the switch on");
                (ItemOffsets.Writes > 0).Should().Be(on, "ECS writes only with the switch on");
                ItemOffsets.Fallbacks.Should().Be(0);
                world.Writer.Violations.Should().Be(0);
                world.Writer.InScope.Should().BeFalse();
            }
            finally
            {
                ItemOffsets.CheckOverride = null;
                ItemOffsets.ResetCounters();
            }
        }

        [Fact]
        public void The_Mobile_Passenger_Gets_No_ItemOffset_And_A_Zero_Offset_Adds_None()
        {
            World world = BuildOffsetBoat(true);
            Mobile m = world.Mobiles.Get(MOB);
            Item bag = world.Items.Get(BAG);

            world.Writer.FollowBoat(m, new Vector3(3, 4, 5));
            m.Offset.Should().Be(new Vector3(3, 4, 5));
            world.Writer.FollowBoat(bag, Vector3.Zero);
            world.Writer.ResetBoatOffset(bag);
            world.Writer.MoveBoatPassenger(bag, 1001, 1000, 0);
            TryItemOffset(world, BAG, out _).Should().BeFalse("a zero offset never adds the component");
            world.Ecs.Mirror.TryGetEntity(MOB, true, out EcsEntity e).Should().BeTrue();
            e.HasComponent<ItemOffset>().Should().BeFalse();

            world.Writer.FollowBoat(bag, new Vector3(-2, 1, 0));
            TryItemOffset(world, BAG, out ItemOffset c).Should().BeTrue();
            (c.X, c.Y, c.Z).Should().Be((-2f, 1f, 0f));
            world.Writer.MoveBoatPassenger(bag, 1001, 1000, 0);
            TryItemOffset(world, BAG, out c).Should().BeTrue();
            (c.X, c.Y, c.Z).Should().Be((0f, 0f, 0f));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_Serves_The_Component_With_The_Switch_On_And_The_Checker_Reports_A_Difference(bool on)
        {
            ItemOffsets.CheckOverride = true;
            ItemOffsets.ResetCounters();

            try
            {
                World world = BuildOffsetBoat(on);
                Item bag = world.Items.Get(BAG);
                world.Writer.FollowBoat(bag, new Vector3(1, 2, 3));

                // a write that bypassed the funnel: only the Item changes
                bag.Offset = new Vector3(7, 8, 9);
                Vector3 read = ItemOffsets.Read(bag);

                if (on)
                {
                    read.Should().Be(new Vector3(1, 2, 3), "the component is the source");
                    (ItemOffsets.Reads, ItemOffsets.Checks, ItemOffsets.Diffs).Should().Be((1L, 1L, 1L));
                }
                else
                {
                    read.Should().Be(new Vector3(7, 8, 9), "the Item is the source");
                    (ItemOffsets.Reads, ItemOffsets.Checks, ItemOffsets.Diffs).Should().Be((0L, 0L, 0L));
                }

                // the checker off: no comparison
                ItemOffsets.CheckOverride = false;
                ItemOffsets.Read(bag);
                ItemOffsets.Checks.Should().Be(on ? 1 : 0);
                ItemOffsets.Summary(world).Should().Contain(on ? "ecs_item_offset on" : "ecs_item_offset off");
            }
            finally
            {
                ItemOffsets.CheckOverride = null;
                ItemOffsets.ResetCounters();
            }
        }

        [Fact]
        public void Read_Falls_Back_To_The_Item_Without_A_Record()
        {
            ItemOffsets.CheckOverride = true;
            ItemOffsets.ResetCounters();

            try
            {
                var world = new World { ItemOffsetOverride = true };

                // a detached instance (the house placement preview): TryScalarEntity has nothing to find
                Item preview = Item.Create(world, 0);
                preview.Offset = new Vector3(4, 0, 0);
                ItemOffsets.Read(preview).Should().Be(new Vector3(4, 0, 0));
                (ItemOffsets.Reads, ItemOffsets.Fallbacks, ItemOffsets.Diffs).Should().Be((0L, 1L, 0L));

                ItemOffsets.Push(world, preview, new Vector3(1, 1, 1));
                ItemOffsets.Writes.Should().Be(0, "no record to write");
            }
            finally
            {
                ItemOffsets.CheckOverride = null;
                ItemOffsets.ResetCounters();
            }
        }
    }
}
