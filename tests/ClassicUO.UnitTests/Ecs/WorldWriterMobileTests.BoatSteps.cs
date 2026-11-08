using System;
using System.Collections.Generic;
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
    // ecs_boat_steps: a boat's queued steps, step clock and passengers are its BoatSteps component, filled by WorldWriter's
    // boat operations and advanced by BoatStepSystem. A reference World runs the old BoatMovingManager (switch off); the
    // subject World runs the same calls with the switch off or on (and CUO_BOAT_CHECK's shadow on or off, ecs_item_offset on
    // or off), and after every call and frame both Worlds must be equal: positions, offsets, step clocks, tiles, records.
    public partial class WorldWriterMobileTests
    {
        private const uint BOAT2 = 0x4000_7402, DECKCOIN = 0x4000_7403;

        private static World BuildTwoBoats(bool boatSteps, bool itemOffset)
        {
            World world = BuildBoat(Start.Standing);
            world.BoatStepsOverride = boatSteps;
            world.ItemOffsetOverride = itemOffset;
            world.Writer.ThrowOnViolation = true;

            Item boat2 = world.GetOrCreateItem(BOAT2);
            boat2.Graphic = 0x3E4E;
            boat2.SetInWorldTile(1010, 1000, 0);
            boat2.PendingBlock = 7;
            world.Ecs.Sync(boat2);
            boat2.UpdateRealScreenPositionOnly(0, 0);

            Entity other = world.Get(OTHER);
            other.PendingBlock = 7;
            other.UpdateRealScreenPositionOnly(0, 0);

            // boat 1 is a multi: its components follow its offset mid-step and are regenerated at each step's end
            AddBoatHouse(world);

            return world;
        }

        private static string HouseState(World w)
        {
            if (!w.HouseManager.TryGetHouse(BOAT, out House house))
            {
                return "none";
            }

            var sb = new System.Text.StringBuilder();

            foreach (Multi c in house.Components)
            {
                sb.Append($"{c.X},{c.Y},{c.Z} {c.Offset}; ");
            }

            return sb.ToString();
        }

        private static readonly uint[] BoatSerials = { BOAT, BOAT2, MOB, BAG, OTHER, DECKCOIN };

        private static string BoatState(World w, uint serial)
        {
            Entity e = w.Get(serial);

            return e == null ? "none" : $"{BoatSnapshot(w, serial)} {e.IsDestroyed}";
        }

        private static BoatSteps BoatStepsOf(World w, uint serial) =>
            w.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e) && e.TryGetComponent(out BoatSteps s) ? s : default;

        private static bool HasBoatSteps(World w, uint serial) =>
            w.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e) && e.HasComponent<BoatSteps>();

        // the scenarios: each call runs on both Worlds at its time; a null action is a frame (BoatMovingManager.Update)
        private static IEnumerable<(uint ticks, string what, Action<World> act)> BoatScript()
        {
            Action<World> frame = null;

            // moving: boat 1 east with the mobile and the bag on deck, boat 2 north with the bystander; mid-step, then done
            yield return (100, "two boats queue", w =>
            {
                BoatMovingManager b = w.BoatMovingManager;
                b.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);
                b.PushItemToList(BOAT, MOB, 1, 0, -2);
                b.PushItemToList(BOAT, BAG, -1, 1, 0);
                b.AddStep(BOAT2, 2, Direction.North, Direction.North, 1010, 999, 0);
                b.PushItemToList(BOAT2, OTHER, 20, 9, 0);
            });
            yield return (130, "frame", frame);
            yield return (300, "frame", frame);
            yield return (450, "frame", frame);
            yield return (600, "frame (boat 1 done)", frame);
            yield return (700, "frame", frame);

            // stopping and turning: a turn in place (the boat's own tile) and a step after it, both done in one frame
            yield return (750, "turn queued", w => w.BoatMovingManager.AddStep(BOAT, 3, Direction.South, Direction.South, 1001, 1000, 0));
            yield return (900, "step after the turn", w =>
            {
                w.BoatMovingManager.AddStep(BOAT, 4, Direction.Down, Direction.Down, 1002, 1001, 1);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 0, 0);
                w.BoatMovingManager.PushItemToList(BOAT, BAG, -1, 1, 0);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 1, 0); // listed twice: updated in place
            });
            yield return (950, "frame", frame);
            yield return (1000, "frame", frame);
            yield return (1100, "frame (boat 2 done)", frame);

            // a turn while under way: the multi reload's ClearSteps resets the boat and its passengers mid-step
            yield return (1150, "step", w =>
            {
                w.BoatMovingManager.AddStep(BOAT, 3, Direction.East, Direction.East, 1003, 1001, 1);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 1, 0);
                w.BoatMovingManager.PushItemToList(BOAT, BAG, -1, 1, 0);
            });
            yield return (1250, "frame", frame);
            yield return (1260, "ClearSteps mid-step", w => w.BoatMovingManager.ClearSteps(BOAT));
            yield return (1300, "frame (nothing queued)", frame);
            yield return (1310, "ClearSteps with nothing queued", w => w.BoatMovingManager.ClearSteps(BOAT));

            // a passenger leaves (the next list has only the mobile) and one joins (a coin on deck), speeds slow/fast/other
            yield return (1400, "steps with the bag on deck", w =>
            {
                w.BoatMovingManager.AddStep(BOAT, 2, Direction.East, Direction.East, 1004, 1001, 1);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 1, 0);
                w.BoatMovingManager.PushItemToList(BOAT, BAG, -1, 1, 0);
            });
            yield return (1500, "frame", frame);
            yield return (1520, "the bag leaves, the coin joins", w =>
            {
                Item coin = w.GetOrCreateItem(DECKCOIN);
                coin.Graphic = 0xEED;
                coin.SetInWorldTile(1003, 1000, 1);
                coin.PendingBlock = 7;
                w.Ecs.Sync(coin);
                w.BoatMovingManager.ClearEntities(BOAT);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 1, 0);
                w.BoatMovingManager.PushItemToList(BOAT, DECKCOIN, 1, 1, 0);
                w.BoatMovingManager.AddStep(BOAT, 6, Direction.East, Direction.East, 1005, 1001, 1);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 1, 0);
                w.BoatMovingManager.PushItemToList(BOAT, DECKCOIN, 1, 1, 0);
            });
            yield return (1600, "frame", frame);
            yield return (2000, "frame (first done)", frame);
            yield return (2030, "frame", frame);
            yield return (2100, "frame (second done)", frame);

            // a full queue: seven packets in a row are trimmed to six
            yield return (2200, "seven steps", w =>
            {
                for (int i = 0; i < 7; i++)
                {
                    Time.Ticks = NOW + 2200 + (uint)i * 10;
                    w.BoatMovingManager.AddStep(BOAT, 4, Direction.East, Direction.East, (ushort)(1006 + i), 1001, 1);
                    w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 1, 0);
                }
            });

            for (uint t = 2300; t <= 2700; t += 50)
            {
                yield return (t, "frame (the queue drains)", frame);
            }

            // the boat removed mid-step: boat 2 under way, then deleted (0x1D); the bystander keeps its offset
            yield return (2800, "boat 2 steps", w =>
            {
                w.BoatMovingManager.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 998, 0);
                w.BoatMovingManager.PushItemToList(BOAT2, OTHER, 20, 8, 0);
            });
            yield return (2900, "frame", frame);
            yield return (2910, "boat 2 removed", w => w.Writer.RemoveWorldItem(w.Items.Get(BOAT2), out _));
            yield return (3000, "frame", frame);
            yield return (3500, "frame", frame);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        [InlineData(false, true, true)]
        public void The_Boat_Step_System_Equals_The_Old_Manager(bool boatSteps, bool check, bool itemOffset)
        {
            BoatStepSystem.CheckOverride = check;
            BoatStepSystem.ResetCounters();

            try
            {
                World reference = BuildTwoBoats(false, itemOffset), world = BuildTwoBoats(boatSteps, itemOffset);
                int frames = 0;

                foreach ((uint ticks, string what, Action<World> act) in BoatScript())
                {
                    Time.Ticks = NOW + ticks;
                    Pair(() => (act ?? (w => w.BoatMovingManager.Update()))(reference), () => (act ?? (w => w.BoatMovingManager.Update()))(world));
                    Time.Ticks = NOW;
                    frames += act == null ? 1 : 0;

                    AssertSameWorld(reference, world);

                    foreach (uint serial in BoatSerials)
                    {
                        BoatState(world, serial).Should().Be(BoatState(reference, serial), $"0x{serial:X8} after '{what}' at {ticks}");
                    }

                    HouseState(world).Should().Be(HouseState(reference), $"boat 1's components after '{what}' at {ticks}");
                    world.Writer.InScope.Should().BeFalse();
                }

                frames.Should().BeGreaterThan(15);
                world.Items.Get(BOAT).X.Should().Be(1012, "every queued step was taken");
                world.Get(OTHER).Offset.Should().NotBe(Vector3.Zero, "boat 2 was removed mid-step");
                world.Writer.Violations.Should().Be(0);

                // the component is the state with the switch on, never added with it off
                HasBoatSteps(world, BOAT).Should().Be(boatSteps);
                HasBoatSteps(reference, BOAT).Should().BeFalse();
                (BoatStepSystem.Frames > 0).Should().Be(boatSteps, "the system runs only with the switch on");
                (BoatStepSystem.Completed > 0).Should().Be(boatSteps);
                (BoatStepSystem.Turns > 0).Should().Be(boatSteps, "the turn in place");
                (BoatStepSystem.PassengerMoves > 0).Should().Be(boatSteps);
                (BoatStepSystem.Follows > 0).Should().Be(boatSteps);
                BoatStepSystem.Fallbacks.Should().Be(0);
                (BoatStepSystem.Checks > 0).Should().Be(boatSteps && check, "the shadow compares only with the checker on");
                (BoatStepSystem.StateChecks > 0).Should().Be(boatSteps && check);
                (BoatStepSystem.DecisionDiffs, BoatStepSystem.StateDiffs).Should().Be((0L, 0L));
            }
            finally
            {
                BoatStepSystem.CheckOverride = null;
                BoatStepSystem.ResetCounters();
            }
        }

        [Fact]
        public void QueueBoatStep_Fills_The_Component_As_The_Old_Queue()
        {
            World world = BuildTwoBoats(true, false);
            Item boat = world.Items.Get(BOAT);
            BoatMovingManager b = world.BoatMovingManager;

            Time.Ticks = NOW + 100;
            b.AddStep(BOAT, 2, Direction.East, Direction.East, 1001, 1000, 0);
            Time.Ticks = NOW + 140;
            b.AddStep(BOAT, 4, Direction.Down, Direction.Down, 1002, 1001, -3);
            b.PushItemToList(BOAT, MOB, 1, 0, -2);
            b.PushItemToList(BOAT, BAG, -1, 1, 0);
            b.PushItemToList(BOAT, MOB, 2, 3, 4);
            Time.Ticks = NOW;

            BoatSteps s = BoatStepsOf(world, BOAT);
            s.Count.Should().Be(2);
            BoatStepEntry first = BoatStepRules.At(ref s, 0), second = BoatStepRules.At(ref s, 1);
            (first.X, first.Y, first.Z, first.Direction, first.Speed, first.TimeDiff).Should().Be(((ushort)1001, (ushort)1000, (sbyte)0, (byte)Direction.East, (byte)2, 1000), "a first step takes the speed's time");
            (second.X, second.Y, second.Z, second.Direction, second.Speed, second.TimeDiff).Should().Be(((ushort)1002, (ushort)1001, (sbyte)-3, (byte)Direction.Down, (byte)4, 40), "a later one the time since the last packet");
            s.LastStepTime.Should().Be(NOW + 100, "the first step started the clock");
            boat.LastStepTime.Should().Be(NOW + 100);
            s.PassengerCount.Should().Be(2);
            (s.Passengers[0].Serial, s.Passengers[0].X, s.Passengers[0].Y, s.Passengers[0].Z).Should().Be((MOB, 2, 3, 4), "updated in place");
            (s.Passengers[1].Serial, s.Passengers[1].X, s.Passengers[1].Y, s.Passengers[1].Z).Should().Be((BAG, -1, 1, 0));

            // a new packet drops the list; ClearEntities too; ClearSteps the steps
            Time.Ticks = NOW + 150;
            b.AddStep(BOAT, 3, Direction.East, Direction.East, 1003, 1001, 0);
            Time.Ticks = NOW;
            BoatStepsOf(world, BOAT).PassengerCount.Should().Be(0);
            BoatStepsOf(world, BOAT).Count.Should().Be(3);
            BoatStepsOf(world, BOAT).LastStepTime.Should().Be(NOW + 100, "not the first step: the clock runs on");
            b.PushItemToList(BOAT, BAG, -1, 1, 0);
            b.ClearEntities(BOAT);
            BoatStepsOf(world, BOAT).PassengerCount.Should().Be(0);
            b.ClearSteps(BOAT);
            BoatStepsOf(world, BOAT).Count.Should().Be(0);
            HasBoatSteps(world, BOAT).Should().BeTrue("the component is kept");

            // more than nine passengers grow the array; a trimmed queue keeps the last six
            for (uint i = 0; i < 9; i++)
            {
                b.PushItemToList(BOAT, 0x4000_9000 + i, (int)i, 0, 0);
            }

            BoatStepsOf(world, BOAT).PassengerCount.Should().Be(9);

            for (int i = 0; i < 8; i++)
            {
                Time.Ticks = NOW + 200 + (uint)i;
                b.AddStep(BOAT, 3, Direction.East, Direction.East, (ushort)(1100 + i), 1000, 0);
            }

            Time.Ticks = NOW;
            s = BoatStepsOf(world, BOAT);
            s.Count.Should().Be(BoatSteps.CAPACITY);
            BoatStepRules.At(ref s, 0).X.Should().Be(1102);
            BoatStepRules.At(ref s, BoatSteps.CAPACITY - 1).X.Should().Be(1107);
            world.Writer.Violations.Should().Be(0);
            world.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void The_Boat_Operations_Write_No_Component_With_The_Switch_Off()
        {
            World world = BuildTwoBoats(false, false);
            Item boat = world.Items.Get(BOAT);
            world.Writer.QueueBoatStep(boat, 3, Direction.East, 1001, 1000, 0, 0);
            world.Writer.SetBoatPassenger(boat, MOB, 1, 0, 0);
            world.Writer.DropBoatSteps(boat, true);
            world.Writer.SetBoatStepTime(boat, 77);
            HasBoatSteps(world, BOAT).Should().BeFalse();
            boat.LastStepTime.Should().Be(77);
            world.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void The_Boat_Operations_Reject_A_Boat_This_World_Does_Not_Hold()
        {
            World world = BuildTwoBoats(true, false), other = BuildTwoBoats(true, false);
            Item foreign = other.Items.Get(BOAT);

            foreach (Action act in new Action[]
            {
                () => world.Writer.QueueBoatStep(foreign, 3, Direction.East, 1001, 1000, 0, 0),
                () => world.Writer.SetBoatPassenger(foreign, MOB, 1, 0, 0),
                () => world.Writer.DropBoatSteps(foreign, true)
            })
            {
                act.Should().Throw<ArgumentException>();
            }

            HasBoatSteps(other, BOAT).Should().BeFalse();
            world.Writer.Violations.Should().Be(3);
        }

        [Theory]
        [InlineData("front step")]
        [InlineData("clock")]
        [InlineData("passenger")]
        [InlineData("passenger dropped")]
        [InlineData("queue")]
        public void The_Boat_Checker_Reports_A_Component_That_Left_The_Shadow(string corrupt)
        {
            BoatStepSystem.CheckOverride = true;
            BoatStepSystem.ResetCounters();

            try
            {
                World world = BuildTwoBoats(true, false);
                BoatMovingManager b = world.BoatMovingManager;
                Time.Ticks = NOW + 100;
                b.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);
                b.PushItemToList(BOAT, MOB, 1, 0, -2);
                Time.Ticks = NOW + 200;
                b.Update();
                (BoatStepSystem.DecisionDiffs, BoatStepSystem.StateDiffs).Should().Be((0L, 0L), "clean before the corruption");

                world.Ecs.Mirror.TryGetEntity(BOAT, false, out EcsEntity e).Should().BeTrue();
                ref BoatSteps s = ref e.GetComponent<BoatSteps>();

                switch (corrupt)
                {
                    case "front step": BoatStepRules.Front(ref s).X = 1005; break;
                    case "clock": s.LastStepTime -= 50; break;
                    case "passenger": s.Passengers[0].Y = 7; break;
                    case "passenger dropped": s.PassengerCount = 0; break;
                    case "queue": BoatStepRules.PushBack(ref s, new BoatStepEntry { X = 1002, Y = 1000, TimeDiff = 500 }); break;
                }

                Time.Ticks = NOW + 250;
                b.Update();
                Time.Ticks = NOW;

                if (corrupt is "front step" or "clock")
                {
                    BoatStepSystem.DecisionDiffs.Should().BeGreaterThan(0, corrupt);
                }

                BoatStepSystem.StateDiffs.Should().BeGreaterThan(0, corrupt);
                BoatStepSystem.Summary(world).Should().Contain("ecs_boat_steps on");
            }
            finally
            {
                BoatStepSystem.CheckOverride = null;
                BoatStepSystem.ResetCounters();
                Time.Ticks = NOW;
            }
        }

        [Fact]
        public void The_Boat_Checker_Reports_A_Boat_Only_The_Shadow_Moves()
        {
            BoatStepSystem.CheckOverride = true;
            BoatStepSystem.ResetCounters();

            try
            {
                World world = BuildTwoBoats(true, false);
                Time.Ticks = NOW + 100;
                world.BoatMovingManager.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);

                // the component lost (a record recreated mid-step): only the frame-end sweep sees the shadow's queue
                world.Ecs.Mirror.TryGetEntity(BOAT, false, out EcsEntity e).Should().BeTrue();
                e.RemoveComponent<BoatSteps>();
                Time.Ticks = NOW + 200;
                world.BoatMovingManager.Update();
                Time.Ticks = NOW;

                BoatStepSystem.StateDiffs.Should().Be(1);
                BoatStepSystem.DecisionDiffs.Should().Be(0);
            }
            finally
            {
                BoatStepSystem.CheckOverride = null;
                BoatStepSystem.ResetCounters();
                Time.Ticks = NOW;
            }
        }

        // the step is done in the frame whose delay reaches its time exactly (delay >= TimeDiff), on both paths
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Boat_Step_Completes_When_Its_Time_Is_Reached_Exactly(bool boatSteps)
        {
            World world = BuildTwoBoats(boatSteps, false);
            Item boat = world.Items.Get(BOAT);
            Time.Ticks = NOW + 100;
            world.BoatMovingManager.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);

            Time.Ticks = NOW + 599;
            world.BoatMovingManager.Update();
            boat.X.Should().Be(1000, "1 ms short of the step's 500 ms");
            boat.Offset.Should().NotBe(Vector3.Zero);

            Time.Ticks = NOW + 600;
            world.BoatMovingManager.Update();
            Time.Ticks = NOW;
            boat.X.Should().Be(1001, "the step's time is reached");
            boat.Offset.Should().Be(Vector3.Zero);
        }

        [Fact]
        public void The_Ring_Holds_What_AddStep_Can_Queue()
        {
            BoatSteps.CAPACITY.Should().Be(6, "AddStep trims to five before it adds one");
        }
    }
}
