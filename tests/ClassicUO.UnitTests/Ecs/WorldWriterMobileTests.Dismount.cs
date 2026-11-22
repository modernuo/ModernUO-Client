using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // Upstream survey 2026-11-22, Task 1: absolute assertions for the pace after a dismount. The T6c mount tests pair the old
    // path against the new one, and both read the same WornCache and mirror, so a staleness they shared would pass there.
    // Here the player rides, dismounts by each of the server's sequences, and the Mobile, the step system's input, the ECS
    // slots and the request pace Walk stores must all say "walking" again.
    public partial class WorldWriterMobileTests
    {
        public enum DismountKind
        {
            // 0x1D: the mount's internal item is deleted (BaseMount.Internalize)
            Delete,
            // 0x25: the mount item goes into the pack
            IntoPack,
            // 0x1D then 0x25 for the same serial in one batch: an ethereal going back into the pack
            DeleteThenIntoPack
        }

        private static World BuildRider(out TestPlayer p)
        {
            World world = BuildPlayer(out p);
            SlotsSeam(world);
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        private static bool SlotsMounted(World world, Mobile m) => StepEntity(world, m).GetComponent<EquipmentSlots>().Mounted;

        [Theory]
        [InlineData(DismountKind.Delete)]
        [InlineData(DismountKind.IntoPack)]
        [InlineData(DismountKind.DeleteThenIntoPack)]
        public void Dismount_Restores_The_Walking_Pace(DismountKind kind)
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World world = BuildRider(out TestPlayer p);
                EquipMount(world, ETHEREAL);

                Time.Ticks += 16;
                world.StepSystem.TryRun(world, p, Time.Ticks, FRAME_DELAY, out _).Should().BeTrue();
                p.IsMounted.Should().BeTrue();
                StepSystem.Mounted(p).Should().BeTrue();
                SlotsMounted(world, p).Should().BeTrue("FreshenSlots rebuilt the slots after the equip");
                p.StepRequestDelay(false).Should().Be((ushort)MovementSpeed.TimeToCompleteMovement(false, true));

                switch (kind)
                {
                    case DismountKind.Delete:
                        world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);

                        break;

                    case DismountKind.IntoPack:
                        world.Writer.PlaceInContainer(MOUNT_ITEM, ETHEREAL, 1, 10, 10, 0, BAG, out _);

                        break;

                    case DismountKind.DeleteThenIntoPack:
                        world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                        world.Writer.PlaceInContainer(MOUNT_ITEM, ETHEREAL, 1, 10, 10, 0, BAG, out _);
                        world.Items.Get(MOUNT_ITEM).Should().NotBeNull("the same serial is back, in the pack");

                        break;
                }

                Time.Ticks += 16;
                world.StepSystem.TryRun(world, p, Time.Ticks, FRAME_DELAY, out _).Should().BeTrue();

                p.IsMounted.Should().BeFalse();
                p.FindItemByLayer(Layer.Mount).Should().BeNull();
                StepSystem.Mounted(p).Should().BeFalse();
                SlotsMounted(world, p).Should().BeFalse("FreshenSlots rebuilt the slots after the dismount");
                p.StepRequestDelay(false).Should().Be((ushort)MovementSpeed.TimeToCompleteMovement(false, false));
                p.StepRequestDelay(false).Should().Be(400);
                p.StepRequestDelay(true).Should().Be(200);
                world.Writer.Violations.Should().Be(0);
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }

        // 0xBF.26: a fast-unmount speed mode steps at the mounted pace; Normal restores the walking pace (the server owns the
        // mode: a dismount does not reset it, and ModernUO never sends it on a dismount)
        [Fact]
        public void Fast_Unmount_Then_Normal_Restores_The_Walking_Pace()
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World world = BuildRider(out TestPlayer p);
                EcsEntity e = StepEntity(world, p);

                world.Writer.SetSpeedMode(p, CharacterSpeedType.FastUnmount);
                Time.Ticks += 16;
                world.StepSystem.TryRun(world, p, Time.Ticks, FRAME_DELAY, out _).Should().BeTrue();
                p.IsMounted.Should().BeFalse();
                StepSystem.Mounted(p).Should().BeTrue();
                StepSystem.MountedFromEcs(in e.GetComponent<MobileInputs>(), in e.GetComponent<EquipmentSlots>()).Should().BeTrue();
                p.StepRequestDelay(false).Should().Be((ushort)MovementSpeed.TimeToCompleteMovement(false, true));

                world.Writer.SetSpeedMode(p, CharacterSpeedType.Normal);
                Time.Ticks += 16;
                world.StepSystem.TryRun(world, p, Time.Ticks, FRAME_DELAY, out _).Should().BeTrue();
                StepSystem.Mounted(p).Should().BeFalse();
                StepSystem.MountedFromEcs(in e.GetComponent<MobileInputs>(), in e.GetComponent<EquipmentSlots>()).Should().BeFalse();
                p.StepRequestDelay(false).Should().Be(400);
                world.Writer.Violations.Should().Be(0);
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }
    }
}
