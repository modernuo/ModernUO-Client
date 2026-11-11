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
    // A boat's queued steps, step clock and passengers are its BoatSteps component, filled by WorldWriter's boat operations
    // and advanced by BoatStepSystem. A reference World runs a copy of the old BoatMovingManager's dictionaries
    // (LegacyBoatCalls; T9 deleted ecs_boat_steps' off path); the subject World runs the same calls through the real manager,
    // and after every call and frame both Worlds must be equal: positions, offsets, step clocks, tiles, records.
    public partial class WorldWriterMobileTests
    {
        private const uint BOAT2 = 0x4000_7402, DECKCOIN = 0x4000_7403;

        private static World BuildTwoBoats()
        {
            World world = BuildBoat(Start.Standing);
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
        private static IEnumerable<(uint ticks, string what, Action<World, IBoatCalls> act)> BoatScript()
        {
            Action<World, IBoatCalls> frame = null;

            // moving: boat 1 east with the mobile and the bag on deck, boat 2 north with the bystander; mid-step, then done
            yield return (100, "two boats queue", (w, b) =>
            {
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
            yield return (750, "turn queued", (w, b) => b.AddStep(BOAT, 3, Direction.South, Direction.South, 1001, 1000, 0));
            yield return (900, "step after the turn", (w, b) =>
            {
                b.AddStep(BOAT, 4, Direction.Down, Direction.Down, 1002, 1001, 1);
                b.PushItemToList(BOAT, MOB, 1, 0, 0);
                b.PushItemToList(BOAT, BAG, -1, 1, 0);
                b.PushItemToList(BOAT, MOB, 1, 1, 0); // listed twice: updated in place
            });
            yield return (950, "frame", frame);
            yield return (1000, "frame", frame);
            yield return (1100, "frame (boat 2 done)", frame);

            // a turn while under way: the multi reload's ClearSteps resets the boat and its passengers mid-step
            yield return (1150, "step", (w, b) =>
            {
                b.AddStep(BOAT, 3, Direction.East, Direction.East, 1003, 1001, 1);
                b.PushItemToList(BOAT, MOB, 1, 1, 0);
                b.PushItemToList(BOAT, BAG, -1, 1, 0);
            });
            yield return (1250, "frame", frame);
            yield return (1260, "ClearSteps mid-step", (w, b) => b.ClearSteps(BOAT));
            yield return (1300, "frame (nothing queued)", frame);
            yield return (1310, "ClearSteps with nothing queued", (w, b) => b.ClearSteps(BOAT));

            // a passenger leaves (the next list has only the mobile) and one joins (a coin on deck), speeds slow/fast/other
            yield return (1400, "steps with the bag on deck", (w, b) =>
            {
                b.AddStep(BOAT, 2, Direction.East, Direction.East, 1004, 1001, 1);
                b.PushItemToList(BOAT, MOB, 1, 1, 0);
                b.PushItemToList(BOAT, BAG, -1, 1, 0);
            });
            yield return (1500, "frame", frame);
            yield return (1520, "the bag leaves, the coin joins", (w, b) =>
            {
                Item coin = w.GetOrCreateItem(DECKCOIN);
                coin.Graphic = 0xEED;
                coin.SetInWorldTile(1003, 1000, 1);
                coin.PendingBlock = 7;
                w.Ecs.Sync(coin);
                b.ClearEntities(BOAT);
                b.PushItemToList(BOAT, MOB, 1, 1, 0);
                b.PushItemToList(BOAT, DECKCOIN, 1, 1, 0);
                b.AddStep(BOAT, 6, Direction.East, Direction.East, 1005, 1001, 1);
                b.PushItemToList(BOAT, MOB, 1, 1, 0);
                b.PushItemToList(BOAT, DECKCOIN, 1, 1, 0);
            });
            yield return (1600, "frame", frame);
            yield return (2000, "frame (first done)", frame);
            yield return (2030, "frame", frame);
            yield return (2100, "frame (second done)", frame);

            // a full queue: seven packets in a row are trimmed to six
            yield return (2200, "seven steps", (w, b) =>
            {
                for (int i = 0; i < 7; i++)
                {
                    Time.Ticks = NOW + 2200 + (uint)i * 10;
                    b.AddStep(BOAT, 4, Direction.East, Direction.East, (ushort)(1006 + i), 1001, 1);
                    b.PushItemToList(BOAT, MOB, 1, 1, 0);
                }
            });

            for (uint t = 2300; t <= 2700; t += 50)
            {
                yield return (t, "frame (the queue drains)", frame);
            }

            // the boat removed mid-step: boat 2 under way, then deleted (0x1D); the bystander keeps its offset
            yield return (2800, "boat 2 steps", (w, b) =>
            {
                b.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 998, 0);
                b.PushItemToList(BOAT2, OTHER, 20, 8, 0);
            });
            yield return (2900, "frame", frame);
            yield return (2910, "boat 2 removed", (w, b) => w.Writer.RemoveWorldItem(w.Items.Get(BOAT2), out _));
            yield return (3000, "frame", frame);
            yield return (3500, "frame", frame);
        }

        [Fact]
        public void The_Boat_Step_System_Equals_The_Old_Manager()
        {
            BoatStepSystem.ResetCounters();

            try
            {
                World reference = BuildTwoBoats(), world = BuildTwoBoats();
                var legacy = new LegacyBoatCalls(reference);
                var boats = new ManagerBoatCalls(world);
                int frames = 0;

                foreach ((uint ticks, string what, Action<World, IBoatCalls> act) in BoatScript())
                {
                    Time.Ticks = NOW + ticks;
                    Pair(() => (act ?? ((w, b) => b.Update()))(reference, legacy), () => (act ?? ((w, b) => b.Update()))(world, boats));
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
                world.Get(OTHER).ScreenOffset.Should().NotBe(Vector3.Zero, "boat 2 was removed mid-step");
                world.Writer.Violations.Should().Be(0);

                // the component is the state; the reference's dictionaries never add it
                HasBoatSteps(world, BOAT).Should().BeTrue();
                HasBoatSteps(reference, BOAT).Should().BeFalse();
                BoatStepSystem.Frames.Should().BeGreaterThan(0);
                BoatStepSystem.Completed.Should().BeGreaterThan(0);
                BoatStepSystem.Turns.Should().BeGreaterThan(0, "the turn in place");
                BoatStepSystem.PassengerMoves.Should().BeGreaterThan(0);
                BoatStepSystem.Follows.Should().BeGreaterThan(0);
                BoatStepSystem.Fallbacks.Should().Be(0);
            }
            finally
            {
                BoatStepSystem.ResetCounters();
            }
        }

        [Fact]
        public void QueueBoatStep_Fills_The_Component_As_The_Old_Queue()
        {
            World world = BuildTwoBoats();
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
        public void The_Boat_Operations_Reject_A_Boat_This_World_Does_Not_Hold()
        {
            World world = BuildTwoBoats(), other = BuildTwoBoats();
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

        // the step is done in the frame whose delay reaches its time exactly (delay >= TimeDiff)
        [Fact]
        public void A_Boat_Step_Completes_When_Its_Time_Is_Reached_Exactly()
        {
            World world = BuildTwoBoats();
            Item boat = world.Items.Get(BOAT);
            Time.Ticks = NOW + 100;
            world.BoatMovingManager.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);

            Time.Ticks = NOW + 599;
            world.BoatMovingManager.Update();
            boat.X.Should().Be(1000, "1 ms short of the step's 500 ms");
            boat.ScreenOffset.Should().NotBe(Vector3.Zero);

            Time.Ticks = NOW + 600;
            world.BoatMovingManager.Update();
            Time.Ticks = NOW;
            boat.X.Should().Be(1001, "the step's time is reached");
            boat.ScreenOffset.Should().Be(Vector3.Zero);
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

        private static IEnumerable<(uint ticks, string what, Action<World, IBoatCalls> act)> OrderScript()
        {
            Action<World, IBoatCalls> frame = null;

            // four boats queue (slots W, X1, B, X2); X1 and X2 are removed under way, so the frame frees X1's slot, then X2's.
            // W is removed with nothing queued: the old manager kept its slot for good
            yield return (100, "W, X1, B, X2 queue", (w, b) =>
            {
                b.AddStep(GONE_W, 3, Direction.East, Direction.East, 1071, 1000, 0);
                b.AddStep(GONE_X1, 3, Direction.East, Direction.East, 1041, 1000, 0);
                b.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1021, 1000, 0);
                b.AddStep(GONE_X2, 3, Direction.East, Direction.East, 1051, 1000, 0);
            });
            yield return (150, "X1 and X2 removed", (w, b) =>
            {
                w.Writer.RemoveWorldItem(w.Items.Get(GONE_X1), out _);
                w.Writer.RemoveWorldItem(w.Items.Get(GONE_X2), out _);
            });
            yield return (200, "frame (X1, X2 dropped)", frame);
            yield return (600, "frame (B done)", frame);
            yield return (650, "W removed, nothing queued", (w, b) => w.Writer.RemoveWorldItem(w.Items.Get(GONE_W), out _));

            // A takes the last freed slot (X2's, after B): B runs first, so A's completion leaves B's step queued
            yield return (700, "A queues, carrying B", (w, b) =>
            {
                b.AddStep(CARRIER_A, 3, Direction.East, Direction.East, 1031, 1000, 0);
                b.PushItemToList(CARRIER_A, CARRIED_B, 9, 0, 0);
            });
            yield return (900, "B queues", (w, b) => b.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1022, 1000, 0));
            yield return (1200, "frame (A done, B mid-step)", frame);
            yield return (1300, "frame (B's turn)", frame);

            // Z takes X1's slot, before B: Z's completion puts B on its destination before B's own decision
            yield return (1400, "Z queues, carrying B", (w, b) =>
            {
                b.AddStep(CARRIER_Z, 3, Direction.East, Direction.East, 1061, 1000, 0);
                b.PushItemToList(CARRIER_Z, CARRIED_B, 38, 0, 0);
            });
            yield return (1600, "B queues", (w, b) => b.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1023, 1000, 0));
            yield return (1900, "frame (Z done, B mid-step)", frame);
            yield return (2000, "frame", frame);

            // X1 comes back under its serial and queues: no slot is free (W's is still held), so it goes last
            yield return (2100, "X1 back, carrying B", (w, b) =>
            {
                AddBoat(w, GONE_X1, 1040);
                b.AddStep(GONE_X1, 3, Direction.East, Direction.East, 1041, 1000, 0);
                b.PushItemToList(GONE_X1, CARRIED_B, 17, 0, 0);
            });
            yield return (2300, "B queues", (w, b) => b.AddStep(CARRIED_B, 3, Direction.East, Direction.East, 1024, 1000, 0));
            yield return (2600, "frame (X1 done, B mid-step)", frame);
            yield return (2900, "frame", frame);
        }

        [Fact]
        public void The_Boats_Step_In_The_Old_Managers_Order()
        {
            {
                World reference = BuildTwoBoats(), world = BuildTwoBoats();
                var legacy = new LegacyBoatCalls(reference);
                var boats = new ManagerBoatCalls(world);

                foreach (World w in new[] { reference, world })
                {
                    AddBoat(w, GONE_W, 1070);
                    AddBoat(w, GONE_X1, 1040);
                    AddBoat(w, CARRIED_B, 1020);
                    AddBoat(w, GONE_X2, 1050);
                    AddBoat(w, CARRIER_A, 1030);
                    AddBoat(w, CARRIER_Z, 1060);
                }

                foreach ((uint ticks, string what, Action<World, IBoatCalls> act) in OrderScript())
                {
                    Time.Ticks = NOW + ticks;
                    Pair(() => (act ?? ((w, b) => b.Update()))(reference, legacy), () => (act ?? ((w, b) => b.Update()))(world, boats));
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
            }
        }

        // ---- the Codex audit of ecs_boat_steps (P2 2): a boat removed and recreated under its serial before the next frame
        // keeps its pending steps and passengers (the old manager's dictionaries were keyed by serial and dropped a removed
        // boat only at the next Update): the recreated multi's reload (LoadMulti -> ClearSteps) resets the passengers'
        // offsets; a recreated plain item steps the old queue; a packet appends to it. A boat that is not back by the next
        // frame loses them there.

        [Theory]
        [InlineData("multi")]
        [InlineData("item, then a frame")]
        [InlineData("item, then a packet")]
        [InlineData("item, list cleared while gone")]
        [InlineData("back after a frame")]
        public void A_Boat_Recreated_Before_The_Next_Frame_Keeps_Its_Pending_Steps(string how)
        {
            {
                World reference = BuildTwoBoats(), world = BuildTwoBoats();
                var legacy = new LegacyBoatCalls(reference);
                var boats = new ManagerBoatCalls(world);
                byte type = how == "multi" ? (byte)2 : (byte)0;

                IEnumerable<(uint ticks, string what, Action<World, IBoatCalls> act)> Script()
                {
                    Action<World, IBoatCalls> frame = null;

                    yield return (100, "boat 2 steps", (w, b) =>
                    {
                        b.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 999, 0);
                        b.PushItemToList(BOAT2, OTHER, 20, 9, 0);
                    });
                    yield return (140, "a second step queued", (w, b) =>
                    {
                        b.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 998, 0);
                        b.PushItemToList(BOAT2, OTHER, 20, 9, 0);
                    });
                    yield return (300, "frame (mid-step)", frame);
                    yield return (310, "boat 2 removed", (w, b) => w.Writer.RemoveWorldItem(w.Items.Get(BOAT2), out _));

                    if (how == "item, list cleared while gone")
                    {
                        yield return (315, "ClearEntities without the Item", (w, b) => b.ClearEntities(BOAT2));
                    }

                    if (how == "back after a frame")
                    {
                        yield return (320, "frame (boat 2 gone)", frame);
                    }

                    yield return (330, "boat 2 recreated", (w, b) =>
                    {
                        Item boat = w.Writer.UpsertWorldItem(BOAT2, 0x3E4E, 0, 1, 1010, 1000, 0, Direction.North, 0, 0, type, out bool created, out WorldWriter.Placement placement);
                        created.Should().BeTrue();
                        w.Writer.PlaceWorldItem(placement);

                        if (type == 2 && b is LegacyBoatCalls)
                        {
                            // the multi's reload (LoadMulti) called the World's real manager; the reference's queue is here
                            b.ClearSteps(BOAT2);
                        }
                    });

                    if (how == "item, then a packet")
                    {
                        yield return (400, "a packet for the recreated boat", (w, b) =>
                        {
                            b.AddStep(BOAT2, 3, Direction.North, Direction.North, 1010, 997, 0);
                            b.PushItemToList(BOAT2, OTHER, 20, 10, 0);
                        });
                    }

                    yield return (450, "frame", frame);
                    yield return (700, "frame", frame);
                    yield return (1000, "frame", frame);
                    yield return (1500, "frame", frame);
                }

                foreach ((uint ticks, string what, Action<World, IBoatCalls> act) in Script())
                {
                    Time.Ticks = NOW + ticks;
                    Pair(() => (act ?? ((w, b) => b.Update()))(reference, legacy), () => (act ?? ((w, b) => b.Update()))(world, boats));
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
                    world.Get(OTHER).ScreenOffset.Should().Be(Vector3.Zero, "the recreated multi's reload reset the passenger");
                }

                world.BoatMovingManager.Steps.ParkedCount.Should().Be(0, "nothing waits once a frame ran");

                world.Writer.Violations.Should().Be(0);
            }
        }

        [Fact]
        public void The_Ring_Holds_What_AddStep_Can_Queue()
        {
            BoatSteps.CAPACITY.Should().Be(6, "AddStep trims to five before it adds one");
        }
    }
}
