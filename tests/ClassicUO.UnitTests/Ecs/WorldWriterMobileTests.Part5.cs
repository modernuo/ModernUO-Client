using System;
using System.Collections.Generic;
using System.Reflection;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 5 (ecs-transition-plan.md, T1): EnterWorld (0x1B), the walker (0x21 DenyWalk, 0x22 ConfirmWalk, 0x20's
    // deny) and Mobile.ProcessSteps' step completion. Same method as part 4: a copy of the old code on one World, the funnel
    // on another, identical starts, everything compared (AssertSameWorld). ProcessSteps itself needs Client.Game (its frame
    // delay), so its completion block is compared through the operation it now calls; the client gate covers the call site.
    public partial class WorldWriterMobileTests
    {
        // ---- 0x1B: the body graphic and CheckGraphicChange, the tile (position callbacks), the direction (door check)

        private static void OldEnterWorld(Mobile m, ushort graphic, ushort x, ushort y, sbyte z, Direction dir)
        {
            m.Graphic = graphic;
            OldCheckGraphicChange(m);
            m.SetInWorldTile(x, y, z);
            m.Direction = dir;
        }

        private static void NewEnterWorld(World world, Mobile m, ushort graphic, ushort x, ushort y, sbyte z, Direction dir)
        {
            world.Writer.SetMobileGraphic(m, graphic, checkBody: true);
            world.Writer.MoveMobile(m, x, y, z);
            world.Writer.SetMobileDirection(m, dir);
        }

        private static readonly (ushort Graphic, ushort X, ushort Y, sbyte Z, Direction Dir)[] Entries =
        {
            (0x25E, 1005, 1006, 3, Direction.West), // elf female
            (0x190, 1000, 1000, 0, Direction.North), // where it stands, as it faces
            (0x3DB, 2000, 2001, -5, Direction.South), // a body that changes neither sex nor race
            (0x29B, 1001, 1000, 0, Direction.East), // gargoyle female, one tile on
        };

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.Unplaced)]
        public void EnterWorld_Equals_The_Old_Path(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);

            foreach (var e in Entries)
            {
                Pair(() => OldEnterWorld(a, e.Graphic, e.X, e.Y, e.Z, e.Dir), () => NewEnterWorld(newWorld, b, e.Graphic, e.X, e.Y, e.Z, e.Dir));
                AssertSameWorld(oldWorld, newWorld);
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void EnterWorld_On_The_Player_Publishes_Its_Callbacks_As_Before_T1()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;

            // the position callback walks (RequestMove), the door check's packet turns (a plugin send hook)
            foreach (TestPlayer p in new[] { a, b })
            {
                p.OnMoved = StandInWalk(Direction.North); // facing North already: queues a step, no turn
                p.OnTurned = StandInWalk(Direction.South);
            }

            Pair(() => OldEnterWorld(a, 0x191, 1005, 1006, 3, Direction.West), () => NewEnterWorld(newWorld, b, 0x191, 1005, 1006, 3, Direction.West));

            b.OnMoved.Should().BeNull("the position callback ran");
            b.OnTurned.Should().BeNull("the direction callback ran");
            b.Direction.Should().Be(Direction.South);
            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void SetMobileGraphic_And_SetMobileDirection_Compute_Their_Values_Before_The_Writes()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            var seen = new List<string>();
            world.Ecs.MobileCommitObserver = (mob, v) => seen.Add($"{mob.Graphic:X4} {mob.Direction} {mob.IsFemale} -> {v.Graphic:X4} {v.Direction} {v.Hue:X4} {v.Flags}");

            world.Writer.SetMobileGraphic(m, 0x25E, checkBody: true);
            world.Writer.SetMobileDirection(m, Direction.West);

            seen.Should().Equal("0190 North False -> 025E North 83EA None", "025E North True -> 025E West 83EA None");
            (m.Graphic, m.IsFemale, m.Race, m.Direction).Should().Be(((ushort)0x25E, true, RaceType.ELF, Direction.West));

            // checkBody false: the graphic only
            world.Writer.SetMobileGraphic(m, 0x190, checkBody: false);
            (m.Graphic, m.IsFemale, m.Race).Should().Be(((ushort)0x190, true, RaceType.ELF));
        }

        // ---- the walker: 0x21 (WalkerManager.DenyWalk, then the packet's direction), 0x20's deny (no move), 0x22's notoriety

        private static void OldDenyWalk(Mobile m, int x, int y, sbyte z, Direction dir)
        {
            m.ClearSteps();

            if (x != -1)
            {
                m.SetInWorldTile((ushort)x, (ushort)y, z);
            }

            m.Direction = dir;
        }

        private static void NewDenyWalk(World world, Mobile m, int x, int y, sbyte z, Direction dir)
        {
            world.Writer.StopMobile(m);

            if (x != -1)
            {
                world.Writer.MoveMobile(m, (ushort)x, (ushort)y, z);
            }

            world.Writer.SetMobileDirection(m, dir);
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.OneStep)]
        [InlineData(Start.Walking)]
        [InlineData(Start.FullQueue)]
        public void DenyWalk_And_ConfirmWalk_Equal_The_Old_Path(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);

            // an offset mid-step, which the clear zeroes
            a.OffsetX = b.OffsetX = 7;
            a.OffsetZ = b.OffsetZ = -2;

            var denies = new (int X, int Y, sbyte Z, Direction Dir)[] { (998, 1001, 2, Direction.Left), (-1, -1, -1, Direction.North), (1000, 1000, 0, Direction.East) };

            foreach (var d in denies)
            {
                Pair(() => OldDenyWalk(a, d.X, d.Y, d.Z, d.Dir), () => NewDenyWalk(newWorld, b, d.X, d.Y, d.Z, d.Dir));
                AssertSameWorld(oldWorld, newWorld);
                b.Steps.Count.Should().Be(0);

                // 0x22's notoriety
                NotorietyFlag n = d.X == -1 ? NotorietyFlag.Criminal : NotorietyFlag.Ally;
                Pair(() => a.NotorietyFlag = n, () => newWorld.Writer.SetNotoriety(b, n));
                AssertSameWorld(oldWorld, newWorld);

                // the next step queued again, so the following deny clears a queue
                Pair(() => OldEnqueueStep(a, a.X + 1, a.Y, a.Z, Direction.East, false), () => OldEnqueueStep(b, b.X + 1, b.Y, b.Z, Direction.East, false));
                a.OffsetY = b.OffsetY = 3;
            }

            newWorld.Writer.Violations.Should().Be(0);
        }

        private static void GiveWalker(TestPlayer p) =>
            typeof(PlayerMobile).GetField("<Walker>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(p, new WalkerManager(p));

        [Fact]
        public void WalkerManager_DenyWalk_Goes_Through_The_Funnel_As_The_Old_Path()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            GiveWalker(b);
            Pair(() => OldEnqueueStep(a, 1001, 1000, 0, Direction.East, true), () => OldEnqueueStep(b, 1001, 1000, 0, Direction.East, true));
            a.OffsetX = b.OffsetX = 5;

            // the position callback walks again (RequestMove): the move is not folded
            a.OnMoved = StandInWalk(Direction.West);
            b.OnMoved = StandInWalk(Direction.West);

            Pair(() => { a.ClearSteps(); a.SetInWorldTile(990, 991, 1); }, () => b.Walker.DenyWalk(3, 990, 991, 1));
            b.OnMoved.Should().BeNull();
            AssertSameWorld(oldWorld, newWorld);
            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));

            // 0x20's deny: no position
            Pair(() => a.ClearSteps(), () => b.Walker.DenyWalk(0xFF, -1, -1, -1));
            b.Steps.Count.Should().Be(0);
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);
        }

        // ---- Mobile.ProcessSteps' step completion

        // the removeStep block of ProcessSteps before T1 (PushInputs through WorldEcs; CloseRangedGumps needs a map: World.InGame
        // is false in these Worlds)
        internal static void OldCompleteStep(Mobile m, bool directionChange)
        {
            Mobile.Step step = m.Steps.Front();
            m.X = (ushort)step.X;
            m.Y = (ushort)step.Y;
            m.Z = step.Z;
            m.Direction = (Direction)step.Direction;
            m.IsRunning = step.Run;
            m.OffsetX = 0;
            m.OffsetY = 0;
            m.OffsetZ = 0;
            m.RemoveFirstStep();
            m.CalculateRandomIdleTime();
            m.World.Ecs.PushInputs(m);

            if (directionChange)
            {
                return;
            }

            m.UpdateScreenPosition();

            if (m.World.InGame && m.Serial == m.World.Player)
            {
                m.World.Player.CloseRangedGumps();
            }

            if (m.IsPlaced)
            {
                m.AddToTile();
            }

            m.LastStepTime = Time.Ticks;
            m.World.Ecs.PushInputs(m);
        }

        // ProcessSteps' test: a step to the tile it stands on is a turn in place
        private static bool DirectionChange(Mobile m)
        {
            Mobile.Step s = m.Steps.Front();

            return m.X == s.X && m.Y == s.Y;
        }

        private static void NewCompleteStep(World world, Mobile m, bool directionChange)
        {
            Mobile.Step s = m.Steps.Front();
            world.Writer.CompleteStep(m, in s, directionChange);
        }

        // these Worlds have no map, so a mobile is "placed" only when parked (PendingBlock >= 0, Map.Park's mark); AddToTile
        // then finds no map and no chunk and unparks it, so a placed mobile ends unplaced exactly when AddToTile ran
        [Theory]
        [InlineData(Start.OneStep, true)]
        [InlineData(Start.Walking, true)]
        [InlineData(Start.FullQueue, true)]
        [InlineData(Start.Walking, false)]
        [InlineData(Start.FullQueue, false)]
        public void CompleteStep_Equals_The_Old_Step_Completion(Start start, bool placed)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);

            int turns = 0, moves = 0;

            while (a.Steps.Count != 0)
            {
                Time.Ticks += 150;
                // mid-step offsets, as the per-frame interpolation leaves them
                a.OffsetX = b.OffsetX = 11;
                a.OffsetY = b.OffsetY = -6;
                a.OffsetZ = b.OffsetZ = 1;
                a.UpdateRealScreenPositionOnly(0, 0);
                b.UpdateRealScreenPositionOnly(0, 0);
                a.PendingBlock = b.PendingBlock = placed ? 0 : -1;

                bool dc = DirectionChange(a);
                DirectionChange(b).Should().Be(dc);
                _ = dc ? turns++ : moves++;

                Pair(() => OldCompleteStep(a, dc), () => NewCompleteStep(newWorld, b, dc));
                AssertSameWorld(oldWorld, newWorld);
                b.PendingBlock.Should().Be(a.PendingBlock);
                b.IsPlaced.Should().Be(placed && dc, "AddToTile runs for a placed mobile after a move, not after a turn");
            }

            Time.Ticks = NOW;
            moves.Should().BeGreaterThan(0);

            if (start == Start.Walking)
            {
                turns.Should().BeGreaterThan(0, "the walk starts with a turn step");
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void CompleteStep_Of_The_Player_Publishes_Its_Callbacks_As_Before_T1()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;

            // a turn step, then the move
            Pair(() => OldEnqueueStep(a, 1001, 1000, 0, Direction.East, false), () => OldEnqueueStep(b, 1001, 1000, 0, Direction.East, false));

            // what the ECS holds when the position callback runs (a plugin sees the GameObject, but the callback's own walk
            // reads and pushes from there): the inputs as the old code had pushed them before UpdateScreenPosition
            var seenAtCallback = new Dictionary<World, MobileInputs>();

            foreach ((World w, TestPlayer p) in new[] { (oldWorld, a), (newWorld, b) })
            {
                // the turn's door check turns again; the move's position callback walks on (RequestMove)
                p.OnTurned = StandInWalk(Direction.South);
                p.OnMoved = q =>
                {
                    seenAtCallback[w] = Inputs(w, MOB);
                    StandInWalk(Direction.West)(q);
                };
            }

            int completions = 0;

            while (a.Steps.Count != 0 && completions < 6)
            {
                Time.Ticks += 150;
                bool dc = DirectionChange(a);
                Pair(() => OldCompleteStep(a, dc), () => NewCompleteStep(newWorld, b, dc));
                AssertSameWorld(oldWorld, newWorld);
                PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
                completions++;
            }

            Time.Ticks = NOW;
            b.OnMoved.Should().BeNull("the position callback ran");
            b.OnTurned.Should().BeNull("the direction callback ran");
            seenAtCallback[newWorld].Should().Be(seenAtCallback[oldWorld], "the inputs were pushed before the callbacks, as before T1");
            newWorld.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void SetMobileGraphic_Alone_Leaves_The_Ecs_As_A_Sync_Of_The_Old_Writes()
        {
            // 0x1B follows it with MoveMobile, which commits again; on its own it must already publish its values
            foreach (Start start in new[] { Start.Standing, Start.Walking })
            {
                World oldWorld = Build(start), newWorld = Build(start);
                Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
                a.Direction = b.Direction = Direction.Left;
                a.Flags = b.Flags = Flags.WarMode;
                oldWorld.Ecs.Sync(a);
                newWorld.Ecs.Sync(b);

                foreach (ushort g in new ushort[] { 0x25E, 0x3DB, 0x190 })
                {
                    Pair(() => { a.Graphic = g; OldCheckGraphicChange(a); oldWorld.Ecs.Sync(a); }, () => newWorld.Writer.SetMobileGraphic(b, g, checkBody: true));
                    AssertSameWorld(oldWorld, newWorld);
                }
            }
        }

        [Fact]
        public void CompleteStep_Computes_Its_Values_Before_The_Writes()
        {
            World world = Build(Start.Walking);
            Mobile m = world.Mobiles.Get(MOB);
            Mobile.Step turn = m.Steps.Front();
            int n = m.Steps.Count;
            var seen = new List<string>();
            world.Ecs.MobileCommitObserver = (mob, v) => seen.Add($"{mob.X} {mob.Direction} {mob.Steps.Count} -> {v.X} {v.Y} {v.Direction}");

            world.Writer.CompleteStep(m, in turn, directionChange: true);
            Mobile.Step move = m.Steps.Front();
            world.Writer.CompleteStep(m, in move, directionChange: false);

            seen.Should().Equal($"1000 North {n} -> 1000 1000 East", $"1000 East {n - 1} -> 1001 1000 East");
            m.LastStepTime.Should().Be(NOW);
        }

        [Fact]
        public void CompleteStep_Writes_A_Stale_Instance_As_Before_T1_And_Rejects_Another_Worlds()
        {
            // a stale instance still updating: written unfolded, nothing reaches the ECS (as before T1); no violation
            World oldWorld = Build(Start.Standing), newWorld = Build(Start.Standing);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = Mobile.Create(oldWorld, MOB), b = Mobile.Create(newWorld, MOB);

            foreach (Mobile s in new[] { a, b })
            {
                s.Graphic = 0x190;
                s.Direction = Direction.East;
                s.X = 1000;
                s.Y = 1000;
            }

            Pair(() => OldEnqueueStep(a, 1001, 1000, 0, Direction.East, true), () => OldEnqueueStep(b, 1001, 1000, 0, Direction.East, true));

            Time.Ticks = NOW + 300;
            Pair(() => OldCompleteStep(a, false), () => NewCompleteStep(newWorld, b, false));
            Time.Ticks = NOW;
            Snapshot(b).Should().Be(Snapshot(a));
            (b.X, b.IsRunning, b.Steps.Count).Should().Be(((ushort)1001, true, 0));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);

            // another World's mobile: rejected before any change (the step stays queued)
            foreach (bool throws in new[] { true, false })
            {
                World world = Build(Start.Standing), reference = Build(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                World other = Build(Start.OneStep);
                Mobile foreign = other.Mobiles.Get(MOB);
                Mobile.Step step = foreign.Steps.Front();
                string before = Snapshot(foreign);
                Action act = () => world.Writer.CompleteStep(foreign, in step, false);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                world.Writer.Violations.Should().Be(1);
                Snapshot(foreign).Should().Be(before);
                foreign.Steps.Count.Should().Be(1);
                AssertSameWorld(reference, world);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_Step_Completion_Inside_An_Operation_Is_Rejected_Before_Any_Change(bool throws)
        {
            World world = Build(Start.OneStep), reference = Build(Start.OneStep);
            world.Writer.ThrowOnViolation = throws;
            Mobile m = world.Mobiles.Get(MOB);
            Mobile.Step step = m.Steps.Front();
            bool threw = false;
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));

            try
            {
                world.Writer.CompleteStep(m, in step, false);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            finally
            {
                world.Ecs.EndFold(world.Mobiles.Get(OTHER));
            }

            threw.Should().Be(throws);
            world.Writer.Violations.Should().Be(1);
            AssertSameWorld(reference, world);
        }

        // ---- the detached World.Player (0x25 under its serial): 0x1B/0x21/0x22's operations write it as before T1

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_Walker_And_EnterWorld_Operations_Write_The_Detached_Player_As_Before_T1(bool throws)
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = throws;

            foreach (World w in new[] { oldWorld, newWorld })
            {
                w.Writer.PlaceInContainer(MOB, 0xE76, 1, 10, 10, 0, BAG, out _).Should().NotBeNull();
            }

            newWorld.Mobiles.Get(MOB).Should().BeNull();
            Pair(() => OldEnqueueStep(a, 1001, 1000, 0, Direction.East, false), () => OldEnqueueStep(b, 1001, 1000, 0, Direction.East, false));

            Pair(() => OldEnterWorld(a, 0x25E, 1003, 1004, 2, Direction.South), () => NewEnterWorld(newWorld, b, 0x25E, 1003, 1004, 2, Direction.South));
            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
            Pair(() => OldDenyWalk(a, 1000, 1000, 0, Direction.West), () => NewDenyWalk(newWorld, b, 1000, 1000, 0, Direction.West));
            Pair(() => a.NotorietyFlag = NotorietyFlag.Murderer, () => newWorld.Writer.SetNotoriety(b, NotorietyFlag.Murderer));
            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
            (b.Graphic, b.IsFemale, b.X, b.Direction, b.NotorietyFlag, b.Steps.Count).Should().Be(((ushort)0x25E, true, (ushort)1000, Direction.West, NotorietyFlag.Murderer, 0));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);

            // another instance World does not hold is still rejected by each
            Mobile stranger = Mobile.Create(newWorld, MOB);
            var ops = new Action[]
            {
                () => newWorld.Writer.SetMobileGraphic(stranger, 0x191, true),
                () => newWorld.Writer.SetMobileDirection(stranger, Direction.West),
                () => newWorld.Writer.StopMobile(stranger),
                () => newWorld.Writer.SetNotoriety(stranger, NotorietyFlag.Enemy),
            };

            foreach (Action op in ops)
            {
                if (throws)
                {
                    op.Should().Throw<ArgumentException>();
                }
                else
                {
                    op();
                }
            }

            newWorld.Writer.Violations.Should().Be(ops.Length);
            (stranger.Graphic, stranger.Direction, stranger.NotorietyFlag).Should().Be(((ushort)0, (Direction)0, (NotorietyFlag)0));
        }
    }
}

namespace ClassicUO.UnitTests.Ecs
{
    [Collection(nameof(FunnelCheckCollection))]
    public class WalkFunnelCheckTests : IDisposable
    {
        public WalkFunnelCheckTests()
        {
            FunnelCheck.Reset();
            FunnelCheck.Enabled = true;
        }

        public void Dispose()
        {
            FunnelCheck.Enabled = false;
            FunnelCheck.Reset();
        }

        [Fact]
        public void The_EnterWorld_And_Walk_Operations_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            Time.Ticks = 100_000;
            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0000_3200);
            m.Graphic = 0x190;
            m.SetInWorldTile(1000, 1000, 0);
            world.Writer.UpsertMobile(m, m.Serial, 0x190, 0, 1001, 1001, 0, Direction.Down, 0, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceMobile(p);
            m.Steps.Count.Should().BeGreaterThan(1, "a turn and a move");
            FunnelCheck.Reset();

            // 0x1B
            world.Writer.SetMobileGraphic(m, 0x25E, checkBody: true);
            world.Writer.MoveMobile(m, 1000, 1000, 0);
            world.Writer.SetMobileDirection(m, Direction.North);
            FunnelCheck.Total.Should().Be(0, "0x1B");

            // the step completions: a turn, then a move (with its tile re-add and LastStepTime)
            while (m.Steps.Count != 0)
            {
                Time.Ticks += 200;
                Mobile.Step s = m.Steps.Front();
                world.Writer.CompleteStep(m, in s, m.X == s.X && m.Y == s.Y);
            }

            FunnelCheck.Total.Should().Be(0, "ProcessSteps' step completion");

            // 0x21 / 0x22
            m.OffsetX = 4;
            FunnelCheck.Reset();
            world.Writer.StopMobile(m);
            world.Writer.SetMobileDirection(m, Direction.West);
            world.Writer.SetNotoriety(m, NotorietyFlag.Ally);
            FunnelCheck.Total.Should().Be(0, "0x21, 0x22");
            Time.Ticks = 100_000;
        }
    }
}
