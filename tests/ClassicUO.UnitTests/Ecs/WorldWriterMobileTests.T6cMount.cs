using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T6c follow-up: the step system's mounted pace from the ECS (EquipmentSlots.Mounted, rebuilt when dirty;
    // MobileInputs.SpeedMode and its Flying bit) instead of the Mobile (T9 deleted ecs_step_mount_inputs' off path). Legacy
    // ProcessSteps on one World, the system on the other with CUO_STEP_MOUNT_CHECK's comparison on, through mounting and
    // dismounting by the packets' operations (0x2E Equip, 0x1D RemoveWorldItem, 0x25 PlaceInContainer) and 0xBF.26's speed mode.
    public partial class WorldWriterMobileTests
    {
        // an ethereal unicorn: Item.GetGraphicForAnimation maps it without the tiledata, so Mobile.IsMounted works here
        private const uint MOUNT_ITEM = 0x4000_7E01;
        private const ushort ETHEREAL = 0x3E9B, TILLER = 0x3E96;

        public enum MountScenario
        {
            Unmounted, Mounted, Tiller, MountMidWalk, DismountWhileRunning, DismountIntoBag, FastUnmountMidWalk, MountedRunningQueue
        }

        private static void SlotsSeam(World world) =>
            world.StepSystem.RebuildSlotsOverride = (e, a, m) => world.Ecs.Animation.Rebuild(e, in a, m, T5bTiles, T5bConv, false);

        // the old World's slots rebuilt before ProcessSteps, as the drawn path (or T5b's advance) would; the new World's are
        // rebuilt by the system itself (FreshenSlots), so both carry the same tags
        private static void OldRebuildSlots(World world, Mobile m)
        {
            if (world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e) && e.HasComponent<EquipmentSlots>() && MobileAnimationSystem.TakeDirty(e))
            {
                AnimationState s = default;
                s.Flags = e.GetComponent<MobileInputs>().Flags;
                world.Ecs.Animation.Rebuild(e, in s, m, T5bTiles, T5bConv, false);
            }
        }

        private static void MountFrame(World oldWorld, World newWorld, Mobile a, Mobile b, uint mob = MOB)
        {
            byte da = 0, db = 0;
            bool ran = false;
            Pair(() => { OldRebuildSlots(oldWorld, a); a.ProcessSteps(out da, true); }, () => ran = newWorld.StepSystem.TryRun(newWorld, b, Time.Ticks, FRAME_DELAY, out db));
            ran.Should().BeTrue();
            db.Should().Be(da, "the direction out");
            AssertSameWorld(oldWorld, newWorld, mob);
            StepSystem.Diff(b, StepEntity(newWorld, b)).Should().BeNull();
        }

        private static void EquipMount(World world, ushort graphic) =>
            world.Writer.Equip(MOUNT_ITEM, graphic, Layer.Mount, MOB, 0, out _, out _).Should().NotBeNull();

        private static World BuildMount(MountScenario sc, bool system)
        {
            StepScenario steps = sc == MountScenario.DismountWhileRunning || sc == MountScenario.MountedRunningQueue ? StepScenario.Running : StepScenario.Walking;
            World world = BuildSteps(steps, system);

            if (sc == MountScenario.Mounted || sc == MountScenario.DismountWhileRunning || sc == MountScenario.DismountIntoBag ||
                sc == MountScenario.MountedRunningQueue)
            {
                EquipMount(world, ETHEREAL);
            }
            else if (sc == MountScenario.Tiller)
            {
                EquipMount(world, TILLER);
            }

            if (system)
            {
                world.StepSystem.MountCheckOverride = true;
                SlotsSeam(world);
            }

            return world;
        }

        public static IEnumerable<object[]> MountCases()
        {
            foreach (MountScenario sc in System.Enum.GetValues<MountScenario>())
            {
                yield return new object[] { sc, 37 };
                yield return new object[] { sc, 16 };
            }
        }

        [Theory]
        [MemberData(nameof(MountCases))]
        public void The_Step_System_With_Mount_Inputs_Equals_ProcessSteps(MountScenario sc, int frameMs)
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World oldWorld = BuildMount(sc, false), newWorld = BuildMount(sc, true);
                oldWorld.SmallFieldsOverride = false; // ecs_small_fields: the old path writes the GameObject only
                newWorld.Writer.ThrowOnViolation = true;
                Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
                long diffs0 = StepSystem.MountDiffs, reads0 = StepSystem.MountEcsReads, checks0 = StepSystem.MountChecks;
                long rebuilds0 = StepSystem.MountSlotRebuilds;
                bool everMounted = false;

                for (int f = 0; f < 80 && (a.Steps.Count != 0 || f < 3); f++)
                {
                    Time.Ticks += (uint)frameMs;

                    if (f == 2)
                    {
                        switch (sc)
                        {
                            case MountScenario.MountMidWalk:
                                // 0x2E mid-walk: the slots are dirty until the system rebuilds them at the next step decision
                                Pair(() => EquipMount(oldWorld, ETHEREAL), () => EquipMount(newWorld, ETHEREAL));

                                break;

                            case MountScenario.DismountWhileRunning:
                                // 0x1D deletes the mount item while the rider runs
                                Pair(() => oldWorld.Writer.RemoveWorldItem(oldWorld.Items.Get(MOUNT_ITEM), out _),
                                    () => newWorld.Writer.RemoveWorldItem(newWorld.Items.Get(MOUNT_ITEM), out _));

                                break;

                            case MountScenario.DismountIntoBag:
                                // 0x25: the mount item leaves the rider for a container
                                Pair(() => oldWorld.Writer.PlaceInContainer(MOUNT_ITEM, ETHEREAL, 1, 10, 10, 0, BAG, out _),
                                    () => newWorld.Writer.PlaceInContainer(MOUNT_ITEM, ETHEREAL, 1, 10, 10, 0, BAG, out _));

                                break;

                            case MountScenario.FastUnmountMidWalk:
                                // 0xBF.26: a fast-unmount speed mode steps at the mounted pace
                                Pair(() => oldWorld.Writer.SetSpeedMode(a, CharacterSpeedType.FastUnmount),
                                    () => newWorld.Writer.SetSpeedMode(b, CharacterSpeedType.FastUnmount));

                                break;
                        }

                        AssertSameWorld(oldWorld, newWorld);
                    }

                    if (sc == MountScenario.MountedRunningQueue && f == 1)
                    {
                        // more steps while mounted: the auto-movement sync reads the mounted pace
                        Pair(() => OldEnqueueStep(a, 1001, 996, 0, Direction.North, true), () => OldEnqueueStep(b, 1001, 996, 0, Direction.North, true));
                    }

                    everMounted |= StepSystem.Mounted(a);
                    MountFrame(oldWorld, newWorld, a, b);
                }

                b.Steps.Count.Should().Be(0);
                StepSystem.MountDiffs.Should().Be(diffs0, "CUO_STEP_MOUNT_CHECK: the ECS's mounted pace equals the Mobile's at every decision");
                StepSystem.MountChecks.Should().BeGreaterThan(checks0);

                StepSystem.MountEcsReads.Should().BeGreaterThan(reads0);

                everMounted.Should().Be(sc != MountScenario.Unmounted && sc != MountScenario.Tiller);

                if (sc == MountScenario.MountMidWalk || sc == MountScenario.DismountWhileRunning || sc == MountScenario.DismountIntoBag)
                {
                    StepSystem.MountSlotRebuilds.Should().BeGreaterThan(rebuilds0, "the change left the slots dirty");
                }

                newWorld.Writer.Violations.Should().Be(0);
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }

        [Fact]
        public void The_Players_Speed_Mode_Paces_Its_Steps_From_The_Ecs()
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World oldWorld = BuildPlayerSteps(WalkerStart.AllAccepted, false, out TestPlayer a);
                World newWorld = BuildPlayerSteps(WalkerStart.AllAccepted, true, out TestPlayer b);
                newWorld.StepSystem.MountCheckOverride = true;
                SlotsSeam(newWorld);
                long diffs0 = StepSystem.MountDiffs;

                Pair(() => oldWorld.Writer.SetSpeedMode(a, CharacterSpeedType.FastUnmountAndCantRun), () => newWorld.Writer.SetSpeedMode(b, CharacterSpeedType.FastUnmountAndCantRun));
                StepEntity(newWorld, b).GetComponent<MobileInputs>().SpeedMode.Should().Be((byte)CharacterSpeedType.FastUnmountAndCantRun);

                for (int f = 0; f < 60 && a.Steps.Count != 0; f++)
                {
                    Time.Ticks += 29;

                    if (f == 3)
                    {
                        Pair(() => oldWorld.Writer.SetSpeedMode(a, CharacterSpeedType.Normal), () => newWorld.Writer.SetSpeedMode(b, CharacterSpeedType.Normal));
                    }

                    MountFrame(oldWorld, newWorld, a, b, STEP_PLAYER);
                    b.Walker.StepsCount.Should().Be(a.Walker.StepsCount);
                }

                b.Steps.Count.Should().Be(0);
                StepSystem.MountDiffs.Should().Be(diffs0);
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }

        // the source matters: a stale ECS mount (slots say mounted, the Mobile has none) changes the decision, and the checker
        // reports it. One step started 300 ms ago: due at the mounted pace (200 - 16 ms), not at the walking pace (400 - 16 ms).
        [Fact]
        public void The_Decision_Reads_The_Mounted_Pace_From_The_Ecs()
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World oldWorld = BuildSteps(StepScenario.OneStep, false), newWorld = BuildSteps(StepScenario.OneStep, true);
                oldWorld.SmallFieldsOverride = false; // ecs_small_fields: the old path writes the GameObject only
                newWorld.StepSystem.MountCheckOverride = true;
                SlotsSeam(newWorld);
                Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
                a.Steps.Count.Should().Be(1);

                EcsEntity e = StepEntity(newWorld, b);
                MobileAnimationSystem.TakeDirty(e);
                newWorld.Ecs.Animation.Rebuild(e, default, b, T5bTiles, T5bConv, false);
                e.GetComponent<EquipmentSlots>().Mounted.Should().BeFalse();
                e.GetComponent<EquipmentSlots>().Mounted = true;

                Time.Ticks = a.LastStepTime + 300;
                long diffs0 = StepSystem.MountDiffs;
                byte da = 0, db = 0;
                // not paired: the paths part (the completion draws the idle timer)
                a.ProcessSteps(out da, true);
                newWorld.StepSystem.TryRun(newWorld, b, Time.Ticks, FRAME_DELAY, out db).Should().BeTrue();

                StepSystem.MountDiffs.Should().Be(diffs0 + 1, "CUO_STEP_MOUNT_CHECK reports the stale slots");
                a.Steps.Count.Should().Be(1, "the walking pace: not due yet");
                b.Steps.Count.Should().Be(0, "the ECS says mounted: due");
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }

        [Fact]
        public void The_Ecs_Mounted_Pace_Is_The_Slots_The_Fast_Unmount_Modes_Or_Flying()
        {
            var eq = new EquipmentSlots();
            var inp = new MobileInputs();
            StepSystem.MountedFromEcs(in inp, in eq).Should().BeFalse();

            eq.Mounted = true;
            StepSystem.MountedFromEcs(in inp, in eq).Should().BeTrue("riding");
            eq.Mounted = false;

            foreach (CharacterSpeedType mode in System.Enum.GetValues<CharacterSpeedType>())
            {
                inp.SpeedMode = (byte)mode;
                StepSystem.MountedFromEcs(in inp, in eq).Should().Be(mode == CharacterSpeedType.FastUnmount || mode == CharacterSpeedType.FastUnmountAndCantRun, mode.ToString());
            }

            inp.SpeedMode = 0;
            inp.Flags = AnimStateFlags.Flying | AnimStateFlags.GargoyleBody;
            StepSystem.MountedFromEcs(in inp, in eq).Should().BeTrue("a flying gargoyle (IsFlying: CV_7000's poisoned bit)");
            inp.Flags = AnimStateFlags.GargoyleBody | AnimStateFlags.GargoyleProp | AnimStateFlags.Running;
            StepSystem.MountedFromEcs(in inp, in eq).Should().BeFalse("a walking gargoyle");
        }

        [Fact]
        public void SetSpeedMode_Commits_The_Speed_Mode_Input_Once()
        {
            World world = BuildPlayer(out TestPlayer player);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e);
            long pushes = e.GetComponent<MobileInputs>().Pushes;

            world.Writer.SetSpeedMode(player, CharacterSpeedType.FastUnmount);
            ((long)e.GetComponent<MobileInputs>().Pushes - pushes).Should().Be(1L);
            e.GetComponent<MobileInputs>().SpeedMode.Should().Be((byte)CharacterSpeedType.FastUnmount);

            world.Writer.SetSpeedMode(player, CharacterSpeedType.Normal);
            e.GetComponent<MobileInputs>().SpeedMode.Should().Be((byte)CharacterSpeedType.Normal);
        }
    }
}
