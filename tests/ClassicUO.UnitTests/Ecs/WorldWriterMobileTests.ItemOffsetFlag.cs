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
    // perf-17 (item offset flag): ItemOffsets.Push sets Item.ItemOffsetHeld (a non-zero offset written), and the readers test
    // it. They read the component's value at every step of a boat run with a second, unrelated item in the world.
    public partial class WorldWriterMobileTests
    {
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

        [Fact]
        public void Item_Offset_Flag_Reads_The_Component()
        {
            {
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

                world.Writer.Violations.Should().Be(0);
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
    }
}
