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
    // subject World runs the same calls with the switch off or on (and CUO_BOAT_CHECK's shadow on or off), and after every call and frame both Worlds must be equal: positions, offsets, step clocks, tiles, records.
    public partial class WorldWriterMobileTests
    {
        [Fact]
        public void The_BoatSteps_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().EcsBoatSteps.Should().BeTrue();
        }

        private const uint BOAT2 = 0x4000_7402, DECKCOIN = 0x4000_7403;

        private static World BuildTwoBoats(bool boatSteps)
        {
            World world = BuildBoat(Start.Standing);
            world.BoatStepsOverride = boatSteps;
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
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public void The_Boat_Step_System_Equals_The_Old_Manager(bool boatSteps, bool check)
        {
            BoatStepSystem.CheckOverride = check;
            BoatStepSystem.ResetCounters();

            try
            {
                World reference = BuildTwoBoats(false), world = BuildTwoBoats(boatSteps);
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
            World world = BuildTwoBoats(true);
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
            World world = BuildTwoBoats(false);
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
            World world = BuildTwoBoats(true), other = BuildTwoBoats(true);
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
                World world = BuildTwoBoats(true);
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
                World world = BuildTwoBoats(true);
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
            World world = BuildTwoBoats(boatSteps);
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

        // ---- the Codex audit of ecs_boat_steps (P2 1): the boats step in the old manager's order, its dictionary's insertion
        // order (a removed boat's slot is reused by the next new boat, the last freed first), not the archetype query's. The
        // order shows when one boat carries another: a boat whose completion moves a passenger boat onto that boat's queued
        // destination makes the passenger's step a turn in place in the same frame only if it runs first.

        private const uint CARRIER_A = 0x4000_7410, CARRIED_B = 0x4000_7411, GONE_X1 = 0x4000_7412, GONE_X2 = 0x4000_7413, CARRIER_Z = 0x4000_7414,
            GONE_W = 0x4000_7415;

        private static readonly uint[] OrderSerials = { CARRIER_A, CARRIED_B, GONE_X1, GONE_X2, CARRIER_Z, GONE_W };

        private static void AddBoat(World w, uint serial, ushort x)
        {
            Item b = w.GetOrCreateItem(serial);
            b.Graphic = 0x3E4E;
            b.SetInWorldTile(x, 1000, 0);
            b.PendingBlock = 7;
            w.Ecs.Sync(b);
            b.UpdateRealScreenPositionOnly(0, 0);
        }

        private static IEnumerable<(uint ticks, string what, Action<World> act)> OrderScript()
        {
            Action<World> frame = null;

            // four boats queue (slots W, X1, B, X2); X1 and X2 are removed under way, so the frame frees X1's slot, then X2's.
            // W is removed with nothing queued: the old manager kept its slot for good
            yield return (100, "W, X1, B, X2 queue", w =>
            {
                w.BoatMovingManager.AddStep(GONE_W, 3, Direction.East, Direction.East, 1071, 1000, 0);
                w.BoatMovingManager.AddStep(GONE_X1, 3, Direction.East, Direction.East, 1041, 1000, 0);
                w.BoatMovingManager.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1021, 1000, 0);
                w.BoatMovingManager.AddStep(GONE_X2, 3, Direction.East, Direction.East, 1051, 1000, 0);
            });
            yield return (150, "X1 and X2 removed", w =>
            {
                w.Writer.RemoveWorldItem(w.Items.Get(GONE_X1), out _);
                w.Writer.RemoveWorldItem(w.Items.Get(GONE_X2), out _);
            });
            yield return (200, "frame (X1, X2 dropped)", frame);
            yield return (600, "frame (B done)", frame);
            yield return (650, "W removed, nothing queued", w => w.Writer.RemoveWorldItem(w.Items.Get(GONE_W), out _));

            // A takes the last freed slot (X2's, after B): B runs first, so A's completion leaves B's step queued
            yield return (700, "A queues, carrying B", w =>
            {
                w.BoatMovingManager.AddStep(CARRIER_A, 3, Direction.East, Direction.East, 1031, 1000, 0);
                w.BoatMovingManager.PushItemToList(CARRIER_A, CARRIED_B, 9, 0, 0);
            });
            yield return (900, "B queues", w => w.BoatMovingManager.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1022, 1000, 0));
            yield return (1200, "frame (A done, B mid-step)", frame);
            yield return (1300, "frame (B's turn)", frame);

            // Z takes X1's slot, before B: Z's completion puts B on its destination before B's own decision
            yield return (1400, "Z queues, carrying B", w =>
            {
                w.BoatMovingManager.AddStep(CARRIER_Z, 3, Direction.East, Direction.East, 1061, 1000, 0);
                w.BoatMovingManager.PushItemToList(CARRIER_Z, CARRIED_B, 38, 0, 0);
            });
            yield return (1600, "B queues", w => w.BoatMovingManager.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1023, 1000, 0));
            yield return (1900, "frame (Z done, B mid-step)", frame);
            yield return (2000, "frame", frame);

            // X1 comes back under its serial and queues: no slot is free (W's is still held), so it goes last
            yield return (2100, "X1 back, carrying B", w =>
            {
                AddBoat(w, GONE_X1, 1040);
                w.BoatMovingManager.AddStep(GONE_X1, 3, Direction.East, Direction.East, 1041, 1000, 0);
                w.BoatMovingManager.PushItemToList(GONE_X1, CARRIED_B, 17, 0, 0);
            });
            yield return (2300, "B queues", w => w.BoatMovingManager.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1024, 1000, 0));
            yield return (2600, "frame (X1 done, B mid-step)", frame);
            yield return (2900, "frame", frame);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Boats_Step_In_The_Old_Managers_Order(bool check)
        {
            BoatStepSystem.CheckOverride = check;
            BoatStepSystem.ResetCounters();

            try
            {
                World reference = BuildTwoBoats(false), world = BuildTwoBoats(true);

                foreach (World w in new[] { reference, world })
                {
                    AddBoat(w, GONE_W, 1070);
                    AddBoat(w, GONE_X1, 1040);
                    AddBoat(w, CARRIED_B, 1020);
                    AddBoat(w, GONE_X2, 1050);
                    AddBoat(w, CARRIER_A, 1030);
                    AddBoat(w, CARRIER_Z, 1060);
                }

                foreach ((uint ticks, string what, Action<World> act) in OrderScript())
                {
                    Time.Ticks = NOW + ticks;
                    Pair(() => (act ?? (w => w.BoatMovingManager.Update()))(reference), () => (act ?? (w => w.BoatMovingManager.Update()))(world));
                    Time.Ticks = NOW;

                    AssertSameWorld(reference, world);

                    foreach (uint serial in OrderSerials)
                    {
                        BoatState(world, serial).Should().Be(BoatState(reference, serial), $"0x{serial:X8} after '{what}' at {ticks}");
                    }

                    world.Writer.InScope.Should().BeFalse();
                }

                world.Items.Get(CARRIED_B).X.Should().Be(1024, "every step of B was taken");
                world.Writer.Violations.Should().Be(0);
                (BoatStepSystem.DecisionDiffs, BoatStepSystem.StateDiffs).Should().Be((0L, 0L));
            }
            finally
            {
                BoatStepSystem.CheckOverride = null;
                BoatStepSystem.ResetCounters();
            }
        }

        // ---- the Codex audit of ecs_boat_steps (P2 2): a boat removed and recreated under its serial before the next frame
        // keeps its pending steps and passengers (the old manager's dictionaries were keyed by serial and dropped a removed
        // boat only at the next Update): the recreated multi's reload (LoadMulti -> ClearSteps) resets the passengers'
        // offsets; a recreated plain item steps the old queue; a packet appends to it. A boat that is not back by the next
        // frame loses them there.

        [Theory]
        [InlineData("multi", false)]
        [InlineData("multi", true)]
        [InlineData("item, then a frame", false)]
        [InlineData("item, then a frame", true)]
        [InlineData("item, then a packet", false)]
        [InlineData("item, then a packet", true)]
        [InlineData("item, list cleared while gone", false)]
        [InlineData("item, list cleared while gone", true)]
        [InlineData("back after a frame", false)]
        [InlineData("back after a frame", true)]
        public void A_Boat_Recreated_Before_The_Next_Frame_Keeps_Its_Pending_Steps(string how, bool check)
        {
            BoatStepSystem.CheckOverride = check;
            BoatStepSystem.ResetCounters();

            try
            {
                World reference = BuildTwoBoats(false), world = BuildTwoBoats(true);
                byte type = how == "multi" ? (byte)2 : (byte)0;

                IEnumerable<(uint ticks, string what, Action<World> act)> Script()
                {
                    Action<World> frame = null;

                    yield return (100, "boat 2 steps", w =>
                    {
                        w.BoatMovingManager.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 999, 0);
                        w.BoatMovingManager.PushItemToList(BOAT2, OTHER, 20, 9, 0);
                    });
                    yield return (140, "a second step queued", w =>
                    {
                        w.BoatMovingManager.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 998, 0);
                        w.BoatMovingManager.PushItemToList(BOAT2, OTHER, 20, 9, 0);
                    });
                    yield return (300, "frame (mid-step)", frame);
                    yield return (310, "boat 2 removed", w => w.Writer.RemoveWorldItem(w.Items.Get(BOAT2), out _));

                    if (how == "item, list cleared while gone")
                    {
                        yield return (315, "ClearEntities without the Item", w => w.BoatMovingManager.ClearEntities(BOAT2));
                    }

                    if (how == "back after a frame")
                    {
                        yield return (320, "frame (boat 2 gone)", frame);
                    }

                    yield return (330, "boat 2 recreated", w =>
                    {
                        Item boat = w.Writer.UpsertWorldItem(BOAT2, 0x3E4E, 0, 1, 1010, 1000, 0, Direction.North, 0, 0, type, out bool created, out WorldWriter.Placement placement);
                        created.Should().BeTrue();
                        w.Writer.PlaceWorldItem(placement);
                    });

                    if (how == "item, then a packet")
                    {
                        yield return (400, "a packet for the recreated boat", w =>
                        {
                            w.BoatMovingManager.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 997, 0);
                            w.BoatMovingManager.PushItemToList(BOAT2, OTHER, 20, 10, 0);
                        });
                    }

                    yield return (450, "frame", frame);
                    yield return (700, "frame", frame);
                    yield return (1000, "frame", frame);
                    yield return (1500, "frame", frame);
                }

                foreach ((uint ticks, string what, Action<World> act) in Script())
                {
                    Time.Ticks = NOW + ticks;
                    Pair(() => (act ?? (w => w.BoatMovingManager.Update()))(reference), () => (act ?? (w => w.BoatMovingManager.Update()))(world));
                    Time.Ticks = NOW;

                    AssertSameWorld(reference, world);

                    foreach (uint serial in BoatSerials)
                    {
                        BoatState(world, serial).Should().Be(BoatState(reference, serial), $"0x{serial:X8} after '{what}' at {ticks} ({how})");
                    }

                    if (what == "boat 2 removed")
                    {
                        world.BoatMovingManager.Steps.ParkedCount.Should().Be(1, "the queue waits for the next frame under the serial");
                    }

                    world.Writer.InScope.Should().BeFalse();
                }

                if (how == "multi")
                {
                    world.Get(OTHER).Offset.Should().Be(Vector3.Zero, "the recreated multi's reload reset the passenger");
                }

                world.BoatMovingManager.Steps.ParkedCount.Should().Be(0, "nothing waits once a frame ran");

                world.Writer.Violations.Should().Be(0);
                (BoatStepSystem.DecisionDiffs, BoatStepSystem.StateDiffs).Should().Be((0L, 0L));
            }
            finally
            {
                BoatStepSystem.CheckOverride = null;
                BoatStepSystem.ResetCounters();
            }
        }

        [Fact]
        public void The_Ring_Holds_What_AddStep_Can_Queue()
        {
            BoatSteps.CAPACITY.Should().Be(6, "AddStep trims to five before it adds one");
        }
    }
}
