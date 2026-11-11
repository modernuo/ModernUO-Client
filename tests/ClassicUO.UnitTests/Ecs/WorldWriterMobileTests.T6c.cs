using System;
using System.Collections.Generic;
using System.Reflection;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using StepsComponent = ClassicUO.Ecs.Steps;

namespace ClassicUO.UnitTests.Ecs
{
    // T6c (ecs-transition-plan.md): Mobile.ProcessSteps(evaluate: true) as StepSystem over the Steps component. Two Worlds from
    // identical starts: the legacy ProcessSteps on one, StepSystem.TryRun on the other (the Steps component kept),
    // frame by frame with the same tick, frame delay and random state; the Worlds, the direction out, the player's walker and
    // the component (against the Mobile, never re-pushed by the test) are compared after every frame.
    public partial class WorldWriterMobileTests
    {
        private const uint STEP_PLAYER = 0x0000_3010;
        private const int FRAME_DELAY = 16;

        public enum StepScenario
        {
            Walking, OneStep, FullQueue, Running, AcrossChunks, Parked, BadStep, TurnsOnly, TeleportMidStep, DeathMidStep,
            BoatMidStep, ServerAnimation, BadStepCarried, SevenQueued
        }

        // the scenario's World: Build's mobile and a player (ProcessSteps compares the serial with World.Player)
        private static World BuildSteps(StepScenario sc, bool system)
        {
            Start start = sc switch
            {
                StepScenario.Walking => Start.Walking,
                StepScenario.OneStep => Start.OneStep,
                StepScenario.FullQueue => Start.FullQueue,
                _ => Start.Standing
            };

            World world = Build(start);
            TestPlayer p = TestPlayer.Create(world, STEP_PLAYER);
            typeof(World).GetProperty(nameof(World.Player))!.SetValue(world, p);
            world.Mobiles.Add(p);
            world.Ecs.Sync(p);
            p.Graphic = 0x190;
            p.SetInWorldTile(900, 900, 0);

            Mobile m = world.Mobiles.Get(MOB);

            switch (sc)
            {
                case StepScenario.Running:
                    Time.Ticks = NOW - 500;
                    OldEnqueueStep(m, 1000, 999, 0, Direction.North, true);
                    OldEnqueueStep(m, 1000, 998, 0, Direction.North, true);
                    OldEnqueueStep(m, 1001, 997, 5, Direction.Right, true);
                    Time.Ticks = NOW;

                    break;

                case StepScenario.AcrossChunks:
                case StepScenario.Parked:
                    m.SetInWorldTile(1006, 1000, 0);
                    m.TestSetDirection(Direction.East);
                    world.Ecs.Sync(m);
                    Time.Ticks = NOW - 100;

                    for (int x = 1007; x <= 1010; x++)
                    {
                        OldEnqueueStep(m, x, 1000, (sbyte)(x - 1006), Direction.East, false);
                    }

                    Time.Ticks = NOW;

                    break;

                case StepScenario.BadStepCarried:
                case StepScenario.BadStep:
                    // a step three tiles away (a server move the client could not walk): removed at once, no offset
                    m.AddStep(new Mobile.Step { X = 1003, Y = 1000, Z = 0, Direction = (byte)Direction.East, Run = false });
                    m.AddStep(new Mobile.Step { X = 1004, Y = 1000, Z = 0, Direction = (byte)Direction.East, Run = false });
                    if (sc == StepScenario.BadStepCarried)
                    {
                        // the step started just now, so it is not overdue: only the bad-step test can end it at once
                        m.LastStepTime = NOW;
                    }

                    world.Ecs.PushInputs(m);

                    break;

                case StepScenario.TurnsOnly:
                    // two turns in place and a step: ProcessSteps' recursion
                    m.AddStep(new Mobile.Step { X = 1000, Y = 1000, Z = 0, Direction = (byte)Direction.East, Run = false });
                    m.AddStep(new Mobile.Step { X = 1000, Y = 1000, Z = 0, Direction = (byte)Direction.South, Run = false });
                    // a running step: its direction carries the running bit, which the direction out masks off
                    m.AddStep(new Mobile.Step { X = 1000, Y = 1001, Z = 0, Direction = (byte)Direction.South | 0x80, Run = true });
                    world.Ecs.PushInputs(m);

                    break;

                case StepScenario.SevenQueued:
                    // the longest legal queue: four northbound steps (Count 4 < MAX_STEP_COUNT), then one packet that plans a turn,
                    // the move and the final facing (4 + 3 = 7); the 4 + turn + move = 6 shape is seen on the way down
                    Time.Ticks = NOW - 100;
                    for (int y = 999; y >= 996; y--)
                    {
                        OldEnqueueStep(m, 1000, y, 0, Direction.North, false);
                    }

                    m.Steps.Count.Should().Be(Constants.MAX_STEP_COUNT - 1);
                    OldEnqueueStep(m, 1001, 996, 0, Direction.South, false);
                    m.Steps.Count.Should().Be(StepsComponent.CAPACITY);
                    Time.Ticks = NOW;

                    break;

                case StepScenario.TeleportMidStep:
                case StepScenario.DeathMidStep:
                case StepScenario.BoatMidStep:
                case StepScenario.ServerAnimation:
                    Time.Ticks = NOW - 100;
                    OldEnqueueStep(m, 1001, 1000, 0, Direction.East, false);
                    OldEnqueueStep(m, 1002, 1000, 0, Direction.East, false);
                    OldEnqueueStep(m, 1003, 1000, 0, Direction.East, false);
                    Time.Ticks = NOW;

                    break;
            }

            if (sc == StepScenario.ServerAnimation)
            {
                world.Writer.AnimateMobile(m, 12, interval: 5, frameCount: 4, repeatCount: 1, forward: true, fromServer: true);
            }

            if (system)
            {
                world.Ecs.StepsEnabled = true;
                world.Ecs.ResyncClocks();
            }

            return world;
        }

        internal static EcsEntity StepEntity(World world, Mobile m)
        {
            world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e).Should().BeTrue();

            return e;
        }

        // one frame of the legacy path and of the system on the two Worlds (paired random state)
        private static void StepFrame(World oldWorld, World newWorld, Mobile a, Mobile b, bool expectSystem = true)
        {
            byte da = 0, db = 0;
            bool ran = false;

            Pair(() => a.ProcessSteps(out da, true), () => ran = newWorld.StepSystem.TryRun(newWorld, b, Time.Ticks, FRAME_DELAY, out db));

            ran.Should().Be(expectSystem);
            db.Should().Be(da, "the direction out");
            AssertSameWorld(oldWorld, newWorld);
            b.Offset.Should().Be(a.Offset);

            if (expectSystem)
            {
                StepSystem.Diff(b, StepEntity(newWorld, b)).Should().BeNull("the component follows the Mobile without a re-push");
            }
        }

        public static IEnumerable<object[]> StepCases()
        {
            // 37 ms frames land between the step deadlines; 16 ms frames hit them exactly (delay == maxDelay completes)
            foreach (StepScenario sc in Enum.GetValues<StepScenario>())
            {
                yield return new object[] { sc, 37 };
                yield return new object[] { sc, 16 };
            }
        }

        [Theory]
        [MemberData(nameof(StepCases))]
        public void The_Step_System_Equals_ProcessSteps(StepScenario sc, int frameMs)
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World oldWorld = BuildSteps(sc, false), newWorld = BuildSteps(sc, true);
                newWorld.Writer.ThrowOnViolation = true;
                Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
                b.Steps.Count.Should().Be(a.Steps.Count);
                sc.Should().Match(s => a.Steps.Count > 0 || s == StepScenario.Walking, "every scenario starts with steps");

                int interpolated = 0, completed = 0, frames = 0;
                long interp0 = StepSystem.Interpolated, done0 = StepSystem.Completed;

                for (int f = 0; f < 60 && (a.Steps.Count != 0 || f < 3); f++, frames++)
                {
                    Time.Ticks += (uint)frameMs;

                    if (sc == StepScenario.Parked)
                    {
                        a.PendingBlock = b.PendingBlock = 0;
                    }

                    if (f == 3)
                    {
                        switch (sc)
                        {
                            case StepScenario.TeleportMidStep:
                                // 0x20 mid-step: the queue is dropped and the mobile moves; then the server walks it on
                                Pair(() => NewDenyWalk(oldWorld, a, 1200, 1300, 10, Direction.West), () => NewDenyWalk(newWorld, b, 1200, 1300, 10, Direction.West));
                                Pair(() => OldEnqueueStep(a, 1199, 1300, 10, Direction.West, false), () => OldEnqueueStep(b, 1199, 1300, 10, Direction.West, false));

                                break;

                            case StepScenario.DeathMidStep:
                                // the death drops the queue (0xAF's ClearSteps, the StopMobile operation)
                                Pair(() => oldWorld.Writer.StopMobile(a), () => newWorld.Writer.StopMobile(b));

                                break;
                        }
                    }

                    if (sc == StepScenario.BoatMidStep && f >= 2)
                    {
                        // a boat carries the mobile: its offset follows the boat's, every other frame it is moved a tile
                        var off = new Vector3(f % 3 - 1, 2, 0);
                        oldWorld.Writer.FollowBoat(a, off);
                        newWorld.Writer.FollowBoat(b, off);

                        if (f % 4 == 0)
                        {
                            oldWorld.Writer.MoveBoatPassenger(a, (ushort)(a.X + 1), a.Y, a.Z);
                            newWorld.Writer.MoveBoatPassenger(b, (ushort)(b.X + 1), b.Y, b.Z);
                        }
                    }

                    if (sc == StepScenario.BadStepCarried && f == 0)
                    {
                        // carried with a zero X offset: ProcessSteps tests a bad step only when both offsets are 0
                        oldWorld.Writer.FollowBoat(a, new Vector3(0, 2, 0));
                        newWorld.Writer.FollowBoat(b, new Vector3(0, 2, 0));
                    }

                    // what CUO_STEP_CHECK's before-run check sees: the component follows every write outside the system
                    StepSystem.Diff(b, StepEntity(newWorld, b)).Should().BeNull("before frame {0}", f);

                    StepFrame(oldWorld, newWorld, a, b);
                }

                interpolated = (int)(StepSystem.Interpolated - interp0);
                completed = (int)(StepSystem.Completed - done0);
                b.Steps.Count.Should().Be(0, "every step completed within {0} frames", frames);
                completed.Should().BeGreaterThan(0);

                if (sc != StepScenario.BadStep && sc != StepScenario.TurnsOnly && sc != StepScenario.DeathMidStep && sc != StepScenario.BadStepCarried)
                {
                    interpolated.Should().BeGreaterThan(0);
                }

                newWorld.Writer.Violations.Should().Be(0);
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }

        public enum WalkerStart { AllAccepted, NoneAccepted, Mixed }

        private static World BuildPlayerSteps(WalkerStart ws, bool system, out TestPlayer p)
        {
            World world = BuildPlayer(out p, STEP_PLAYER);
            GiveWalker(p);
            Time.Ticks = NOW - 100;
            OldEnqueueStep(p, 1001, 1000, 0, Direction.East, false);
            OldEnqueueStep(p, 1002, 1000, 0, Direction.East, true);
            OldEnqueueStep(p, 1002, 1001, 0, Direction.South, false);
            Time.Ticks = NOW;

            WalkerManager w = p.Walker;
            w.StepsCount = p.Steps.Count;

            for (int i = 0; i < w.StepsCount; i++)
            {
                w.StepInfos[i].Sequence = (byte)(i + 1);
                w.StepInfos[i].Accepted = ws == WalkerStart.AllAccepted || (ws == WalkerStart.Mixed && i % 2 == 0);
            }

            if (system)
            {
                world.Ecs.StepsEnabled = true;
                world.Ecs.ResyncClocks();
            }

            return world;
        }

        [Theory]
        [InlineData(WalkerStart.AllAccepted)]
        [InlineData(WalkerStart.NoneAccepted)]
        [InlineData(WalkerStart.Mixed)]
        public void The_Step_System_Equals_ProcessSteps_For_The_Player_And_Its_Walker(WalkerStart ws)
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                World oldWorld = BuildPlayerSteps(ws, false, out TestPlayer a), newWorld = BuildPlayerSteps(ws, true, out TestPlayer b);
                SlotsSeam(newWorld); // the system rebuilds the player's slots from the test tiles

                for (int f = 0; f < 60 && a.Steps.Count != 0; f++)
                {
                    Time.Ticks += 29;

                    if (f == 4 && ws == WalkerStart.Mixed)
                    {
                        // ConfirmWalk's re-insert: the confirmed step's AddToTile on the player mid-walk (both Worlds)
                        a.AddToTile();
                        b.AddToTile();
                    }

                    byte da = 0, db = 0;
                    bool ran = false;
                    Pair(() => { OldRebuildSlots(oldWorld, a); a.ProcessSteps(out da, true); }, () => ran = newWorld.StepSystem.TryRun(newWorld, b, Time.Ticks, FRAME_DELAY, out db));
                    ran.Should().BeTrue();
                    db.Should().Be(da);
                    AssertSameWorld(oldWorld, newWorld, STEP_PLAYER);
                    b.Walker.StepsCount.Should().Be(a.Walker.StepsCount);
                    b.Walker.CurrentWalkSequence.Should().Be(a.Walker.CurrentWalkSequence);

                    for (int i = 0; i < Constants.MAX_STEP_COUNT; i++)
                    {
                        b.Walker.StepInfos[i].Accepted.Should().Be(a.Walker.StepInfos[i].Accepted);
                        b.Walker.StepInfos[i].Sequence.Should().Be(a.Walker.StepInfos[i].Sequence);
                    }

                    StepSystem.Diff(b, StepEntity(newWorld, b)).Should().BeNull();
                    b.PositionCallbacks.Should().Be(a.PositionCallbacks);
                }

                b.Steps.Count.Should().Be(0);
                b.PositionCallbacks.Should().BeGreaterThan(0);
            }
            finally
            {
                Mobile.FrameDelayOverride = null;
                Time.Ticks = NOW;
            }
        }

        [Fact]
        public void The_Step_System_Declines_When_Off_Or_Not_Held()
        {
            World world = BuildSteps(StepScenario.Walking, false);
            Mobile m = world.Mobiles.Get(MOB);
            long declined = StepSystem.Declined;

            world.StepSystem.TryRun(world, m, Time.Ticks, FRAME_DELAY, out _).Should().BeFalse("the component is not kept with the switch off");
            world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e);
            e.HasComponent<StepsComponent>().Should().BeFalse();

            world.Ecs.StepsEnabled = true;
            world.Ecs.ResyncClocks();
            e.HasComponent<StepsComponent>().Should().BeTrue();

            // another World's instance under the same serial
            World other = BuildSteps(StepScenario.Walking, true);
            other.StepSystem.TryRun(other, m, Time.Ticks, FRAME_DELAY, out _).Should().BeFalse();
            StepSystem.Declined.Should().BeGreaterOrEqualTo(declined + 2);
        }

        // ecs-hygiene: the eligibility comes from one record row; a record without Facing is declined and nothing moves
        [Fact]
        public void The_Step_System_Declines_A_Record_Without_Facing()
        {
            World world = BuildSteps(StepScenario.Walking, true);
            Mobile m = world.Mobiles.Get(MOB);
            world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e).Should().BeTrue();
            e.HasComponent<StepsComponent>().Should().BeTrue();
            e.RemoveComponent<Facing>();
            int steps = m.Steps.Count;

            world.StepSystem.TryRun(world, m, Time.Ticks, FRAME_DELAY, out _).Should().BeFalse();
            m.Steps.Count.Should().Be(steps);
        }

        [Fact]
        public void The_Component_Copies_The_Queue_In_Order_And_The_Diff_Sees_Every_Field()
        {
            World world = BuildSteps(StepScenario.FullQueue, true);
            Mobile m = world.Mobiles.Get(MOB);
            EcsEntity e = StepEntity(world, m);
            ref StepsComponent s = ref e.GetComponent<StepsComponent>();
            s.Count.Should().Be((byte)Constants.MAX_STEP_COUNT);
            StepsComponent.CAPACITY.Should().Be(Constants.MAX_STEP_COUNT - 1 + 3, "PlanStep checks the room once, then adds up to three steps");

            for (int k = 0; k < m.Steps.Count; k++)
            {
                s.Ring[k].X.Should().Be(m.Steps[k].X);
            }

            StepSystem.Diff(m, e).Should().BeNull();

            StepsComponent saved = s;
            s.Ring[2].Direction ^= 1;
            StepSystem.Diff(m, e).Should().Contain("step 2");
            s = saved;
            s.Ring[4].Run = !s.Ring[4].Run;
            StepSystem.Diff(m, e).Should().Contain("step 4");
            s = saved;
            s.LastStepTime++;
            StepSystem.Diff(m, e).Should().Contain("last step time");
            s = saved;
            s.OffsetZ = 3;
            StepSystem.Diff(m, e).Should().Contain("offset");
            s = saved;
            s.Count--;
            StepSystem.Diff(m, e).Should().Contain("count");
            s = saved;
            e.GetComponent<Position>().Z++;
            StepSystem.Diff(m, e).Should().Contain("position");
        }

        [Fact]
        public void The_Skipped_Update_Hash_Sees_The_Queued_Steps()
        {
            World world = BuildSteps(StepScenario.FullQueue, false);
            Mobile m = world.Mobiles.Get(MOB);
            int before = m.UpdateStateHash();
            Mobile.Step s = m.Steps[3];
            s.Direction ^= 1;
            m.ReplaceStep(3, in s);
            m.UpdateStateHash().Should().NotBe(before, "a changed step in the middle of the queue changes the hash");
        }
    }
}
