using System;
using System.Collections.Generic;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 5 (ecs-transition-plan.md, T1): EnterWorld (0x1B). Same method as part 4: a copy of the old code on one
    // World, the funnel on another, identical starts, everything compared (AssertSameWorld).
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

        // ---- the detached World.Player (0x25 under its serial): 0x1B's operations write it as before T1

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_EnterWorld_Operations_Write_The_Detached_Player_As_Before_T1(bool throws)
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
            (b.Graphic, b.IsFemale, b.X, b.Direction).Should().Be(((ushort)0x25E, true, (ushort)1003, Direction.South));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);

            // another instance World does not hold is still rejected by each
            Mobile stranger = Mobile.Create(newWorld, MOB);
            var ops = new Action[]
            {
                () => newWorld.Writer.SetMobileGraphic(stranger, 0x191, true),
                () => newWorld.Writer.SetMobileDirection(stranger, Direction.West),
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
            (stranger.Graphic, stranger.Direction).Should().Be(((ushort)0, (Direction)0));
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
        public void The_EnterWorld_Operations_Make_No_Direct_Writes()
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
            FunnelCheck.Reset();

            // 0x1B
            world.Writer.SetMobileGraphic(m, 0x25E, checkBody: true);
            world.Writer.MoveMobile(m, 1000, 1000, 0);
            world.Writer.SetMobileDirection(m, Direction.North);
            FunnelCheck.Total.Should().Be(0, "0x1B");

            Time.Ticks = 100_000;
        }
    }
}
