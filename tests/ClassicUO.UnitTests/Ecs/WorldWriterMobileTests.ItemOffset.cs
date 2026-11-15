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
    // The boat operations write an item's offset (the boat's, the bag's on its deck) into its ItemOffset component, its only
    // home (T9 S7 deleted the Item's copy and CUO_ITEM_OFFSET_CHECK): added at a non-zero offset, removed at zero, counted by
    // WorldMirror.ItemOffsetCount (zero lets the readers skip the lookup). The boat runs through the real BoatMovingManager:
    // under way (mid-step), stop (a step completes), a turn (ClearSteps' reset), a passenger leaving the list, the boat
    // removed. The mobile passenger keeps its offset on the Mobile, which follows the boat: the reference for the boat's.
    public partial class WorldWriterMobileTests
    {
        private static World BuildOffsetBoat()
        {
            World world = BuildBoat(Start.Standing);
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        private static bool TryItemOffset(World world, uint serial, out ItemOffset c)
        {
            c = default;

            return world.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e) && e.TryGetComponent(out c);
        }

        // every item of the boat: a component exactly when the offset is not zero, the readers (Read, the extract's ReadRow,
        // ScreenOffset) agree with it, and the mirror's count is the number of components
        private static void AssertItemOffsets(World world, string because)
        {
            int components = 0;

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
                Vector3 value = new Vector3(c.X, c.Y, c.Z);

                if (has)
                {
                    components++;
                    value.Should().NotBe(Vector3.Zero, $"0x{serial:X8}: a zero offset has no component ({because})");
                }

                ItemOffsets.Read(it).Should().Be(value, $"0x{serial:X8} read ({because})");
                it.ScreenOffset.Should().Be(value, $"0x{serial:X8} screen offset ({because})");

                // the item extract's Gather: the component from the row ReadRecord holds
                ItemOffsets.ReadRow(true, EcsRead.Row(e)).Should().Be(value, $"0x{serial:X8} row read ({because})");
            }

            world.Ecs.Mirror.ItemOffsetCount.Should().Be(components, because);

            // a mobile passenger's offset is its Steps component (T6c) and the Mobile's, never an ItemOffset
            world.Ecs.Mirror.TryGetEntity(MOB, true, out EcsEntity m).Should().BeTrue();
            m.HasComponent<ItemOffset>().Should().BeFalse(because);
        }

        private static void Frame(World world, uint ticks)
        {
            Time.Ticks = ticks;
            world.BoatMovingManager.Update();
            Time.Ticks = NOW;
        }

        [Fact]
        public void The_Boat_Operations_Write_The_ItemOffset_Component()
        {
            World world = BuildOffsetBoat();
            BoatMovingManager boats = world.BoatMovingManager;
            Item boat = world.Items.Get(BOAT), bag = world.Items.Get(BAG);
            Mobile mob = world.Mobiles.Get(MOB);
            AssertItemOffsets(world, "start");
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
                ItemOffsets.Read(boat).Should().NotBe(Vector3.Zero, "mid-step");
                ItemOffsets.Read(bag).Should().Be(ItemOffsets.Read(boat));
                mob.Offset.Should().Be(ItemOffsets.Read(boat), "the mobile passenger follows the same offset");
                AssertItemOffsets(world, $"mid-step {t}");
            }

            // stop: the step completes, every offset back to zero (the components go)
            Frame(world, NOW + 700);
            boat.X.Should().Be(1001, "the step completed");
            (ItemOffsets.Read(boat), ItemOffsets.Read(bag)).Should().Be((Vector3.Zero, Vector3.Zero));
            AssertItemOffsets(world, "stopped");
            TryItemOffset(world, BOAT, out _).Should().BeFalse("a zero offset removes the component");
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(0, "no boat moves");

            // a second step, mid-way, then a turn: the multi reload's ClearSteps resets the boat and its passengers
            Time.Ticks = NOW + 800;
            boats.AddStep(BOAT, 3, Direction.Down, Direction.Down, 1002, 1001, 1);
            boats.PushItemToList(BOAT, MOB, 1, 0, 0);
            boats.PushItemToList(BOAT, BAG, -1, 1, 0);
            Frame(world, NOW + 1000);
            ItemOffsets.Read(boat).Should().NotBe(Vector3.Zero, "mid-step");
            AssertItemOffsets(world, "second step");
            boats.ClearSteps(BOAT);
            (ItemOffsets.Read(boat), ItemOffsets.Read(bag)).Should().Be((Vector3.Zero, Vector3.Zero));
            AssertItemOffsets(world, "turned");

            // a passenger leaves: the next 0xF6 lists only the mobile; the bag keeps the offset it had
            Time.Ticks = NOW + 1100;
            boats.AddStep(BOAT, 3, Direction.East, Direction.East, 1003, 1001, 1);
            boats.PushItemToList(BOAT, MOB, 1, 0, 0);
            boats.PushItemToList(BOAT, BAG, -1, 1, 0);
            Frame(world, NOW + 1300);
            Vector3 left = ItemOffsets.Read(bag);
            left.Should().NotBe(Vector3.Zero);
            boats.ClearEntities(BOAT);
            boats.PushItemToList(BOAT, MOB, 1, 0, 0);
            Frame(world, NOW + 1400);
            ItemOffsets.Read(bag).Should().Be(left, "no longer carried");
            ItemOffsets.Read(boat).Should().NotBe(left, "the boat moved on");
            AssertItemOffsets(world, "passenger left");

            // the boat removed (0x1D) mid-step: its record goes with its component (the count follows), the bag keeps its
            // offset
            world.Writer.RemoveWorldItem(boat, out _);
            Frame(world, NOW + 1500);
            world.Items.Get(BOAT).Should().BeNull();
            AssertItemOffsets(world, "boat removed");
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(1, "the bag's");

            world.Writer.Violations.Should().Be(0);
            world.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void The_Mobile_Passenger_Gets_No_ItemOffset_And_A_Zero_Offset_Adds_None()
        {
            World world = BuildOffsetBoat();
            Mobile m = world.Mobiles.Get(MOB);
            Item bag = world.Items.Get(BAG);

            world.Writer.FollowBoat(m, new Vector3(3, 4, 5));
            m.Offset.Should().Be(new Vector3(3, 4, 5));
            m.ScreenOffset.Should().Be(new Vector3(3, 4, 5));
            world.Writer.FollowBoat(bag, Vector3.Zero);
            world.Writer.ResetBoatOffset(bag);
            world.Writer.MoveBoatPassenger(bag, 1001, 1000, 0);
            TryItemOffset(world, BAG, out _).Should().BeFalse("a zero offset never adds the component");
            world.Ecs.Mirror.TryGetEntity(MOB, true, out EcsEntity e).Should().BeTrue();
            e.HasComponent<ItemOffset>().Should().BeFalse();
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(0);

            world.Writer.FollowBoat(bag, new Vector3(-2, 1, 0));
            TryItemOffset(world, BAG, out ItemOffset c).Should().BeTrue();
            (c.X, c.Y, c.Z).Should().Be((-2f, 1f, 0f));
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(1);
            world.Writer.FollowBoat(bag, new Vector3(-3, 1, 0));
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(1, "a second write keeps the one component");
            world.Writer.MoveBoatPassenger(bag, 1001, 1000, 0);
            TryItemOffset(world, BAG, out _).Should().BeFalse("back at zero: the component goes");
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(0);
        }

        [Fact]
        public void An_Item_Without_A_Record_Has_No_Offset_And_Clear_Resets_The_Count()
        {
            var world = new World();

            // a detached instance (the house placement preview): no record, no offset, and a push writes nothing
            Item preview = Item.Create(world, 0);
            ItemOffsets.Push(world, preview, new Vector3(1, 1, 1));
            ItemOffsets.Read(preview).Should().Be(Vector3.Zero);
            ItemOffsets.ReadRow(false, default).Should().Be(Vector3.Zero);
            world.Ecs.Mirror.ItemOffsetCount.Should().Be(0, "no record to write");

            World boat = BuildOffsetBoat();
            boat.Writer.FollowBoat(boat.Items.Get(BAG), new Vector3(1, 2, 3));
            boat.Ecs.Mirror.ItemOffsetCount.Should().Be(1);
            boat.Ecs.Mirror.Clear();
            boat.Ecs.Mirror.ItemOffsetCount.Should().Be(0, "no record is left");
        }
    }
}
