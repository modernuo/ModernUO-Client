using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 4 (ecs-transition-plan.md, T1): the mobile operations of WorldWriter must leave the GameObject and the
    // ECS exactly as the handler code they replace did. Each case runs the same packets through a copy of the old code on one
    // World and through the funnel on another, from identical starting states, and compares every mobile property the
    // handlers and the step queue touch (the walk queue element by element, the animation state through UpdateStateHash), the
    // mirror record, tags and entity id, the pushed MobileInputs and MobileClock, and the child lists.
    //
    // Not covered here: the player's own branch of UpdateGameObject (serial == World.Player: no step) and the PlayerMobile
    // callbacks (door and corpse checks), because a PlayerMobile needs the UO files; the client gate (0x20, 0x77, 0x78 on a
    // live server) covers them. SetMobileBody and MoveMobile, the player's operations, are compared here on a plain mobile.
    [Collection(nameof(FunnelCheckCollection))] // Time.Ticks and the shared RNG are process-wide: never in parallel with other tests
    public partial class WorldWriterMobileTests
    {
        [Fact]
        public void The_WorldAnimSystem_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().WorldAnimSystem.Should().BeTrue();
        }

        [Fact]
        public void The_WorldStepSystem_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().WorldStepSystem.Should().BeTrue();
        }

        internal const uint DEAD = 0x8000_3000;
        private const uint MOB = 0x0000_3000, OTHER = 0x0000_3001, NEWMOB = 0x0000_3002, BAG = 0x4000_7000, ITEM = 0x4000_7001;

        internal struct MobPacket
        {
            public uint Serial;
            public ushort Graphic;
            public byte GraphicInc;
            public ushort X, Y;
            public sbyte Z;
            public Direction Direction;
            public ushort Hue;
            public Flags Flags;
        }

        // Mobile.EnqueueStep before T1 (PushInputs is protected: the same push through WorldEcs)
        internal static bool OldEnqueueStep(Mobile m, int x, int y, sbyte z, Direction direction, bool run)
        {
            if (m.Steps.Count >= Constants.MAX_STEP_COUNT)
            {
                return false;
            }

            m.GetEndPosition(out int endX, out int endY, out sbyte endZ, out Direction endDir);

            if (endX == x && endY == y && endZ == z && endDir == direction)
            {
                return true;
            }

            if (m.Steps.Count == 0)
            {
                if (!(m.LastStepTime > Time.Ticks - Constants.WALKING_DELAY))
                {
                    m.SetAnimation(0xFF);
                }

                m.LastStepTime = Time.Ticks;
            }

            Direction moveDir = DirectionHelper.CalculateDirection(endX, endY, x, y);
            Mobile.Step step = new Mobile.Step();

            if (moveDir != Direction.NONE)
            {
                if (moveDir != endDir)
                {
                    step.X = endX;
                    step.Y = endY;
                    step.Z = endZ;
                    step.Direction = (byte)moveDir;
                    step.Run = run;
                    m.AddStep(step);
                }

                step.X = x;
                step.Y = y;
                step.Z = z;
                step.Direction = (byte)moveDir;
                step.Run = run;
                m.AddStep(step);
            }

            if (moveDir != direction)
            {
                step.X = x;
                step.Y = y;
                step.Z = z;
                step.Direction = (byte)direction;
                step.Run = run;
                m.AddStep(step);
            }

            m.World.Ecs.PushInputs(m);

            return true;
        }

        // Mobile.CheckGraphicChange before T1
        internal static void OldCheckGraphicChange(Mobile m)
        {
            switch (m.Graphic)
            {
                case 0x0190: case 0x0192: m.IsFemale = false; m.Race = RaceType.HUMAN; break;
                case 0x0191: case 0x0193: m.IsFemale = true; m.Race = RaceType.HUMAN; break;
                case 0x025D: m.IsFemale = false; m.Race = RaceType.ELF; break;
                case 0x025E: m.IsFemale = true; m.Race = RaceType.ELF; break;
                case 0x029A: m.IsFemale = false; m.Race = RaceType.GARGOYLE; break;
                case 0x029B: m.IsFemale = true; m.Race = RaceType.GARGOYLE; break;
            }
        }

        // the mobile branch of UpdateGameObject before T1 (the UI left out; the callback point is the new-mobile SingleClick,
        // between the writes and the tile). The player test is written null-safe (the test Worlds have no player).
        internal static Mobile OldUpsertMobile(World world, in MobPacket p, Action<Mobile> beforePlacement = null)
        {
            Entity obj = world.Get(p.Serial);
            Mobile mobile;
            ushort graphic = p.Graphic;

            if (obj == null || obj.IsDestroyed)
            {
                mobile = world.GetOrCreateMobile(p.Serial);

                if (mobile == null)
                {
                    return null;
                }

                mobile.Graphic = (ushort)(graphic + p.GraphicInc);
                OldCheckGraphicChange(mobile);
                mobile.Direction = p.Direction & Direction.Up;
                mobile.FixHue(p.Hue);
                mobile.X = p.X;
                mobile.Y = p.Y;
                mobile.Z = p.Z;
                mobile.Flags = p.Flags;
            }
            else
            {
                mobile = (Mobile)obj;
            }

            graphic += p.GraphicInc;

            if (world.Player == null || p.Serial != world.Player.Serial)
            {
                Direction cleaned_dir = p.Direction & Direction.Up;
                bool isrun = (p.Direction & Direction.Running) != 0;

                if (world.Get(mobile) == null || mobile.X == 0xFFFF && mobile.Y == 0xFFFF)
                {
                    mobile.X = p.X;
                    mobile.Y = p.Y;
                    mobile.Z = p.Z;
                    mobile.Direction = cleaned_dir;
                    mobile.IsRunning = isrun;
                    mobile.ClearSteps();
                }

                if (!OldEnqueueStep(mobile, p.X, p.Y, p.Z, cleaned_dir, isrun))
                {
                    mobile.X = p.X;
                    mobile.Y = p.Y;
                    mobile.Z = p.Z;
                    mobile.Direction = cleaned_dir;
                    mobile.IsRunning = isrun;
                    mobile.ClearSteps();
                }
            }

            mobile.Graphic = (ushort)(graphic & 0x3FFF);
            mobile.FixHue(p.Hue);
            mobile.Flags = p.Flags;

            beforePlacement?.Invoke(mobile);

            mobile.SetInWorldTile(mobile.X, mobile.Y, mobile.Z);

            return mobile;
        }

        internal static Mobile NewUpsertMobile(World world, in MobPacket p, Action<Mobile> beforePlacement = null)
        {
            Entity obj = world.Get(p.Serial);
            Mobile mobile = world.Writer.UpsertMobile(obj, p.Serial, p.Graphic, p.GraphicInc, p.X, p.Y, p.Z, p.Direction, p.Hue, p.Flags, out _, out WorldWriter.Placement placement);

            if (mobile == null)
            {
                return null;
            }

            beforePlacement?.Invoke(mobile);
            world.Writer.PlaceMobile(placement);

            return mobile;
        }

        public enum Start { None, Standing, OneStep, Walking, FullQueue, Unplaced, DestroyedHeld }

        private const uint NOW = 100_000;

        // the idle timer is random (SetAnimation's CalculateRandomIdleTime): every World starts from the same generator state,
        // and each pair of paths starts from the same state and must draw the same number of values
        private static readonly RandomHelper.State Seed = RandomHelper.Save();

        private static void Pair(Action oldPath, Action newPath)
        {
            RandomHelper.State s = RandomHelper.Save();
            oldPath();
            RandomHelper.State afterOld = RandomHelper.Save();
            RandomHelper.Restore(s);
            newPath();
            RandomHelper.Save().Should().Be(afterOld, "both paths draw the same random values");
        }

        private static World Build(Start start)
        {
            RandomHelper.Restore(Seed);
            Time.Ticks = NOW;
            var world = new World();
            world.Ecs.ClockEnabled = true;

            // a bystander, so the entity ids of a created mobile are not the first
            Mobile other = world.GetOrCreateMobile(OTHER);
            other.Graphic = 0x191;
            other.SetInWorldTile(990, 990, 0);

            Item bag = world.GetOrCreateItem(BAG);
            bag.Graphic = 0xE75;
            bag.SetInWorldTile(1001, 1000, 0);

            if (start == Start.None)
            {
                return world;
            }

            Mobile m = world.GetOrCreateMobile(MOB);
            m.Graphic = 0x190;
            m.Hue = 0x83EA;
            m.Flags = Flags.None;
            m.Direction = Direction.North;
            m.NotorietyFlag = NotorietyFlag.Innocent;
            m.SetInWorldTile(1000, 1000, 0);

            // a worn item, so a body change's equipment-dirty tag shows
            Item worn = world.GetOrCreateItem(ITEM);
            worn.Graphic = 0x1515;
            worn.Container = MOB;
            worn.Layer = Layer.Shirt;
            m.PushToBack(worn);
            world.Ecs.Sync(worn);

            switch (start)
            {
                case Start.OneStep:
                    // one queued step from an earlier frame (a walk start sets LastStepTime only on an empty queue);
                    // a step in the facing direction, so no turn step is queued before it
                    Time.Ticks = NOW - 1000;
                    OldEnqueueStep(m, 1000, 999, 0, Direction.North, false);
                    Time.Ticks = NOW;

                    if (m.Steps.Count != 1)
                    {
                        throw new InvalidOperationException($"Start.OneStep queued {m.Steps.Count} steps");
                    }

                    break;

                case Start.Walking:
                    Time.Ticks = NOW - 1000;
                    OldEnqueueStep(m, 1001, 1000, 0, Direction.East, false);
                    Time.Ticks = NOW;
                    OldEnqueueStep(m, 1002, 1001, 0, Direction.Down, true);

                    break;

                case Start.FullQueue:
                    for (int i = 1; m.Steps.Count < Constants.MAX_STEP_COUNT; i++)
                    {
                        OldEnqueueStep(m, 1000 + i, 1000, 0, Direction.East, false);
                    }

                    break;

                case Start.Unplaced:
                    m.X = 0xFFFF;
                    m.Y = 0xFFFF;
                    world.Ecs.Sync(m);

                    break;

                case Start.DestroyedHeld:
                    m.Destroy(); // World still holds the destroyed instance: the handler creates a new one

                    break;
            }

            // as after a frame: a placement shows as IsPositionChanged
            m.UpdateRealScreenPositionOnly(0, 0);
            other.UpdateRealScreenPositionOnly(0, 0);

            WorldWriterTests.AttachLists(world);

            // the mobile's equipment tag starts clear: a body change sets it
            if (world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity me) && me.Tags.Has<EquipmentDirty>())
            {
                me.RemoveTag<EquipmentDirty>();
            }

            return world;
        }

        private static readonly MobPacket[] Packets =
        {
            // where it stands (the step is a no-op when standing)
            new MobPacket { Serial = MOB, Graphic = 0x190, X = 1000, Y = 1000, Z = 0, Direction = Direction.North, Hue = 0x83EA },
            // one tile east, running, flags
            new MobPacket { Serial = MOB, Graphic = 0x190, X = 1001, Y = 1000, Z = 0, Direction = Direction.Running | Direction.East, Hue = 0x83EA, Flags = Flags.WarMode },
            // far, a turn at the end, a new hue to fix (>= 0xBB8)
            new MobPacket { Serial = MOB, Graphic = 0x190, X = 1010, Y = 1012, Z = 5, Direction = Direction.West, Hue = 0x8BB9, Flags = Flags.Hidden },
            // a turn in place
            new MobPacket { Serial = MOB, Graphic = 0x190, X = 1000, Y = 1000, Z = 0, Direction = Direction.Left, Hue = 0 },
            // a body change through the increment (the female human), another position
            new MobPacket { Serial = MOB, Graphic = 0x190, GraphicInc = 1, X = 1002, Y = 1001, Z = -3, Direction = Direction.South, Hue = 0x0021, Flags = Flags.Poisoned },
            // a graphic above 0x3FFF: the creation's CheckGraphicChange read it unmasked, the final write is masked
            new MobPacket { Serial = MOB, Graphic = 0x425E, X = 1003, Y = 1003, Z = 0, Direction = Direction.Running | Direction.Down, Hue = 0x4001 },
            // the unplaced marker as the packet's position
            new MobPacket { Serial = MOB, Graphic = 0x25D, X = 0xFFFF, Y = 0xFFFF, Z = 0, Direction = Direction.Running | Direction.Up, Hue = 0 },
            // a new mobile next to MOB
            new MobPacket { Serial = NEWMOB, Graphic = 0x29B, X = 1004, Y = 1004, Z = 2, Direction = Direction.Running | Direction.Right, Hue = 0x0455 },
        };

        public static IEnumerable<object[]> Cases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                for (int i = 0; i < Packets.Length; i++)
                {
                    yield return new object[] { s, i };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void UpsertMobile_Equals_The_Old_Path(Start start, int packet)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            MobPacket p = Packets[packet];

            Mobile a = null, b = null;
            Pair(() => a = OldUpsertMobile(oldWorld, p), () => b = NewUpsertMobile(newWorld, p));
            (b == null).Should().Be(a == null);
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            newWorld.Writer.OutstandingPlacements.Should().Be(0);

            // the same packet again (the step a no-op or a re-queue), then the next one, then a later frame's
            Pair(() => OldUpsertMobile(oldWorld, p), () => NewUpsertMobile(newWorld, p));
            AssertSameWorld(oldWorld, newWorld);

            MobPacket next = Packets[(packet + 1) % Packets.Length];
            Pair(() => OldUpsertMobile(oldWorld, next), () => NewUpsertMobile(newWorld, next));
            AssertSameWorld(oldWorld, newWorld);

            Time.Ticks = NOW + 5000;
            MobPacket third = Packets[(packet + 2) % Packets.Length];
            Pair(() => OldUpsertMobile(oldWorld, third), () => NewUpsertMobile(newWorld, third));
            Time.Ticks = NOW;
            AssertSameWorld(oldWorld, newWorld);
        }

        [Theory]
        [InlineData(Start.None)]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        public void UpsertMobile_The_Callback_Sees_The_Same_Mobile_As_Before_T1(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            string seenOld = null, seenNew = null;
            MobPacket p = Packets[2];

            Pair(() => OldUpsertMobile(oldWorld, p, m => seenOld = Snapshot(m)), () => NewUpsertMobile(newWorld, p, m => seenNew = Snapshot(m)));

            seenNew.Should().Be(seenOld);
            seenNew.Should().Contain("placed False", "the callback runs before the tile placement");
        }

        private static string Snapshot(Mobile m) =>
            $"{m.Graphic:X4} {m.Hue:X4} {m.X},{m.Y},{m.Z} {m.Direction} {m.Flags} {m.IsFemale} {m.Race} {m.IsRunning} {m.Steps.Count} {m.Offset} {m.LastStepTime} {m.UpdateStateHash()} placed {m.IsPositionChanged}";

        [Theory]
        [InlineData(Start.None, 7)]
        [InlineData(Start.Standing, 2)]
        [InlineData(Start.FullQueue, 1)]
        [InlineData(Start.Unplaced, 4)]
        public void UpsertMobile_Computes_Its_Values_Before_The_Writes(Start start, int packet)
        {
            World oldWorld = Build(start), world = Build(start);
            MobPacket p = Packets[packet];
            OldUpsertMobile(oldWorld, p);
            Mobile expected = oldWorld.Mobiles.Get(p.Serial);

            var seen = new List<string>();
            world.Ecs.MobileCommitObserver = (m, v) =>
            {
                // at the commit, the cache still holds the state before the packet's writes (T4: the getters read the ECS,
                // so a value read back after the commit would be the committed one, not computed)
                seen.Add($"{m.Graphic:X4} {m.Hue:X4} {m.X},{m.Y} {m.Flags}");
                (v.Graphic, v.Hue, v.X, v.Y, v.Z, v.Direction, v.Flags).Should().Be((expected.Graphic, expected.Hue, expected.X, expected.Y, expected.Z, expected.Direction, expected.Flags));
            };

            Mobile before = world.Mobiles.Get(p.Serial);
            string start0 = before == null || before.IsDestroyed ? "0000 0000 0,0 None" : $"{before.Graphic:X4} {before.Hue:X4} {before.X},{before.Y} {before.Flags}";

            NewUpsertMobile(world, p);

            seen.Should().Equal(start0);
        }

        // ---- 0x77 / 0x78 / 0x20: the player's body, the notoriety, the move (on a plain mobile: see the class note)

        public enum BodyPacket { UpdateCharacter, UpdateObject, UpdatePlayer }

        private static void OldBody(World world, Mobile m, BodyPacket kind, ushort graphic, ushort hue, Flags flags, Direction dir, ushort x, ushort y, sbyte z)
        {
            switch (kind)
            {
                case BodyPacket.UpdateCharacter:
                    m.Flags = flags;
                    m.Graphic = graphic;
                    OldCheckGraphicChange(m);
                    m.FixHue(hue);
                    world.Ecs.Sync(m);

                    break;

                case BodyPacket.UpdateObject:
                    m.Graphic = graphic;
                    OldCheckGraphicChange(m);
                    m.FixHue(hue);
                    m.Flags = flags;
                    world.Ecs.Sync(m);

                    break;

                default:
                    m.Graphic = graphic;
                    m.Direction = dir & Direction.Mask;
                    m.FixHue(hue);
                    m.Flags = flags;
                    m.SetInWorldTile(x, y, z);

                    break;
            }
        }

        private static void NewBody(World world, Mobile m, BodyPacket kind, ushort graphic, ushort hue, Flags flags, Direction dir, ushort x, ushort y, sbyte z)
        {
            if (kind == BodyPacket.UpdatePlayer)
            {
                world.Writer.SetMobileBody(m, graphic, hue, flags, checkBody: false, setDirection: true, dir & Direction.Mask);
                world.Writer.MoveMobile(m, x, y, z);
            }
            else
            {
                // the packet's direction is passed and must not be written (0x77/0x78 keep the player's)
                world.Writer.SetMobileBody(m, graphic, hue, flags, checkBody: true, setDirection: false, dir);
            }
        }

        [Theory]
        [InlineData(BodyPacket.UpdateCharacter, Start.Standing)]
        [InlineData(BodyPacket.UpdateCharacter, Start.Walking)]
        [InlineData(BodyPacket.UpdateObject, Start.Standing)]
        [InlineData(BodyPacket.UpdateObject, Start.FullQueue)]
        [InlineData(BodyPacket.UpdatePlayer, Start.Standing)]
        [InlineData(BodyPacket.UpdatePlayer, Start.Walking)]
        [InlineData(BodyPacket.UpdatePlayer, Start.Unplaced)]
        public void The_Body_Notoriety_And_Move_Operations_Equal_The_Old_Path(BodyPacket kind, Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);

            var bodies = new (ushort Graphic, ushort Hue, Flags Flags, Direction Dir, ushort X, ushort Y, sbyte Z)[]
            {
                (0x191, 0x8BB9, Flags.Hidden, Direction.Running | Direction.West, 1005, 1006, 3),
                (0x25E, 0x0021, Flags.WarMode, Direction.South, 1005, 1006, 3),
                (0x0190, 0, Flags.None, Direction.North, 1000, 1000, 0),
                (0x3DB, 0x4001, Flags.Poisoned, Direction.East, 2000, 2001, -5), // a body that changes neither sex nor race
            };

            foreach (var x in bodies)
            {
                NotorietyFlag n = (NotorietyFlag)((byte)x.Flags % 7 + 1);
                Pair(() => a.NotorietyFlag = n, () => newWorld.Writer.SetNotoriety(b, n));
                AssertSameWorld(oldWorld, newWorld); // the notoriety's push on its own
                Pair(() => OldBody(oldWorld, a, kind, x.Graphic, x.Hue, x.Flags, x.Dir, x.X, x.Y, x.Z), () => NewBody(newWorld, b, kind, x.Graphic, x.Hue, x.Flags, x.Dir, x.X, x.Y, x.Z));
                AssertSameWorld(oldWorld, newWorld);
            }

            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void SetMobileBody_And_MoveMobile_Compute_Their_Values_Before_The_Writes()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            var seen = new List<string>();
            world.Ecs.MobileCommitObserver = (mob, v) => seen.Add($"{mob.Graphic:X4} {mob.Direction} {mob.X} -> {v.Graphic:X4} {v.Direction} {v.X} {v.Hue:X4}");

            world.Writer.SetMobileBody(m, 0x191, 0x8BB9, Flags.Hidden, checkBody: false, setDirection: true, Direction.West);
            world.Writer.MoveMobile(m, 1200, 1300, 4);

            seen.Should().Equal("0190 North 1000 -> 0191 West 1000 8001", "0191 West 1000 -> 0191 West 1200 8001");
            (m.X, m.Y, m.Z).Should().Be(((ushort)1200, (ushort)1300, (sbyte)4));
        }

        // ---- 0x77's lookups: the notoriety on World.Mobiles.Get's mobile, then UpdateGameObject's World.Get (an item first
        // for a serial outside the mobile range, e.g. a death-rekeyed mobile's)

        internal static World BuildDead()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            world.Mobiles.Remove(MOB).Should().BeTrue();

            for (LinkedObject i = m.Items; i != null; i = i.Next)
            {
                ((Item)i).Container = DEAD;
            }

            world.Mobiles[DEAD] = m;
            m.Serial = DEAD;
            world.Ecs.OnSerialChanged(MOB, m);

            // an item (a corpse, as after a death) under the same serial
            Item corpse = world.GetOrCreateItem(DEAD);
            corpse.Graphic = 0x2006;
            corpse.Amount = 0x190;
            corpse.SetInWorldTile(1000, 1001, 0);
            WorldWriterTests.AttachLists(world);

            return world;
        }

        private static void OldUpdateCharacter(World world, in MobPacket p, NotorietyFlag notoriety)
        {
            Mobile mobile = world.Mobiles.Get(p.Serial);

            if (mobile == null)
            {
                return;
            }

            mobile.NotorietyFlag = notoriety;
            Dispatch(world, p, old: true);
        }

        private static void NewUpdateCharacter(World world, in MobPacket p, NotorietyFlag notoriety)
        {
            Mobile mobile = world.Mobiles.Get(p.Serial);

            if (mobile == null)
            {
                return;
            }

            world.Writer.SetNotoriety(mobile, notoriety);
            Dispatch(world, p, old: false);
        }

        // UpdateGameObject's dispatch for type 1 (0x77): an item World.Get finds takes the item branch
        private static void Dispatch(World world, in MobPacket p, bool old)
        {
            Entity obj = world.Get(p.Serial);

            if (obj == null || obj.IsDestroyed ? !SerialHelper.IsMobile(p.Serial) : obj is Item)
            {
                var ip = new WorldWriterTests.Packet { Serial = p.Serial, Graphic = p.Graphic, X = p.X, Y = p.Y, Z = p.Z, Direction = p.Direction, Hue = p.Hue, Flags = p.Flags, Type = 1 };

                if (old)
                {
                    WorldWriterTests.OldUpsert(world, ip);
                }
                else
                {
                    Item item = world.Writer.UpsertWorldItem(ip.Serial, ip.Graphic, 0, 0, ip.X, ip.Y, ip.Z, ip.Direction, ip.Hue, ip.Flags, 1, out _, out WorldWriter.Placement placement);

                    if (item != null)
                    {
                        world.Writer.PlaceWorldItem(placement);
                    }
                }

                return;
            }

            if (old)
            {
                OldUpsertMobile(world, p);
            }
            else
            {
                NewUpsertMobile(world, p);
            }
        }

        [Fact]
        public void UpdateCharacter_Keeps_The_Handlers_Lookups_For_A_Rekeyed_Mobile()
        {
            World oldWorld = BuildDead(), newWorld = BuildDead();
            var p = new MobPacket { Serial = DEAD, Graphic = 0x192, X = 1001, Y = 1001, Z = 0, Direction = Direction.East, Hue = 0x21 };

            Pair(() => OldUpdateCharacter(oldWorld, p, NotorietyFlag.Murderer), () => NewUpdateCharacter(newWorld, p, NotorietyFlag.Murderer));

            AssertSameWorld(oldWorld, newWorld, DEAD);
            newWorld.Mobiles.Get(DEAD).NotorietyFlag.Should().Be(NotorietyFlag.Murderer, "the notoriety goes to the mobile");
            newWorld.Items.Get(DEAD).Graphic.Should().Be(0x192, "UpdateGameObject's World.Get found the item first");
            newWorld.Mobiles.Get(DEAD).Graphic.Should().Be(0x190, "and left the mobile's body alone");
        }

        // ---- placements, nesting, foreign and stale instances

        [Fact]
        public void PlaceMobile_Needs_An_Outstanding_Mobile_Placement()
        {
            World world = Build(Start.Standing);
            world.Writer.ThrowOnViolation = true;
            MobPacket p = Packets[1];
            Mobile m = world.Writer.UpsertMobile(world.Get(MOB), MOB, p.Graphic, 0, p.X, p.Y, p.Z, p.Direction, p.Hue, p.Flags, out _, out WorldWriter.Placement first);
            world.Writer.UpsertMobile(m, MOB, p.Graphic, 0, p.X, p.Y, p.Z, p.Direction, p.Hue, p.Flags, out _, out WorldWriter.Placement second);

            // superseded by the second upsert
            Action stale = () => world.Writer.PlaceMobile(first);
            stale.Should().Throw<ArgumentException>();

            // an item's placement is not a mobile's, and the reverse
            Item it = world.Writer.UpsertWorldItem(0x4000_7500, 0xEED, 0, 1, 1100, 1100, 0, 0, 0, 0, 0, out _, out WorldWriter.Placement itemPlacement);
            it.Should().NotBeNull();
            Action wrongKind = () => world.Writer.PlaceMobile(itemPlacement);
            wrongKind.Should().Throw<ArgumentException>();
            Action wrongKind2 = () => world.Writer.PlaceWorldItem(second);
            wrongKind2.Should().Throw<ArgumentException>();
            world.Writer.OutstandingPlacements.Should().Be(2, "a rejected placement consumes nothing");

            world.Writer.PlaceMobile(second);
            world.Writer.PlaceWorldItem(itemPlacement);
            world.Writer.OutstandingPlacements.Should().Be(0);

            Action twice = () => world.Writer.PlaceMobile(second);
            twice.Should().Throw<ArgumentException>();
            Action none = () => world.Writer.PlaceMobile(default);
            none.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Release_A_Bad_Mobile_Placement_Places_As_Before_T1()
        {
            World world = Build(Start.Standing);
            world.Writer.ThrowOnViolation = false;
            Mobile m = world.Mobiles.Get(MOB);
            Mobile n = world.Writer.UpsertMobile(m, MOB, 0x190, 0, 1000, 1000, 0, Direction.North, 0, 0, out _, out WorldWriter.Placement placement);
            world.Writer.PlaceMobile(placement);
            m.UpdateRealScreenPositionOnly(0, 0);
            int violations = world.Writer.Violations;

            world.Writer.PlaceMobile(placement); // used twice

            world.Writer.Violations.Should().Be(violations + 1);
            m.IsPositionChanged.Should().BeTrue("the pre-T1 placement ran");
            n.Should().BeSameAs(m);
        }

        public enum MobileOp { Upsert, Place, Notoriety, Body, Move, Graphic, Turn, Stop, Hits, Status, MobileStatus, Mana, Stamina, Race, Healthbar, Animate }

        private static void Run(World world, MobileOp op, Mobile m, WorldWriter.Placement placement = default)
        {
            switch (op)
            {
                case MobileOp.Upsert: world.Writer.UpsertMobile(m, m.Serial, 0x191, 0, 1001, 1000, 0, Direction.East, 0x21, Flags.Hidden, out _, out _); break;
                case MobileOp.Place: world.Writer.PlaceMobile(placement); break;
                case MobileOp.Notoriety: world.Writer.SetNotoriety(m, NotorietyFlag.Enemy); break;
                case MobileOp.Body: world.Writer.SetMobileBody(m, 0x191, 0x21, Flags.Hidden, true, true, Direction.East); break;
                case MobileOp.Graphic: world.Writer.SetMobileGraphic(m, 0x25E, checkBody: true); break;
                case MobileOp.Turn: world.Writer.SetMobileDirection(m, Direction.West); break;
                case MobileOp.Stop: world.Writer.StopMobile(m); break;
                case MobileOp.Hits: world.Writer.SetHits(m, 90, 45); break;
                case MobileOp.Status: world.Writer.SetStatus(m, "renamed", 45, 90); break;
                case MobileOp.MobileStatus: world.Writer.SetMobileStatus(m, true, true, true); break;
                case MobileOp.Mana: world.Writer.SetMana(m, 80, 40); break;
                case MobileOp.Stamina: world.Writer.SetStamina(m, 70, 35); break;
                case MobileOp.Race: world.Writer.SetRace(m, RaceType.GARGOYLE); break;
                case MobileOp.Healthbar: world.Writer.SetHealthbar(m, Flags.YellowBar | Flags.Poisoned, true, true); break;
                case MobileOp.Animate: world.Writer.AnimateMobile(m, 12, 2, 5, 1, frame: 3, hold: true); break;
                default: world.Writer.MoveMobile(m, 1200, 1200, 0); break;
            }
        }

        [Theory]
        [InlineData(MobileOp.Upsert, true)]
        [InlineData(MobileOp.Upsert, false)]
        [InlineData(MobileOp.Place, true)]
        [InlineData(MobileOp.Place, false)]
        [InlineData(MobileOp.Notoriety, true)]
        [InlineData(MobileOp.Notoriety, false)]
        [InlineData(MobileOp.Body, true)]
        [InlineData(MobileOp.Body, false)]
        [InlineData(MobileOp.Move, true)]
        [InlineData(MobileOp.Move, false)]
        [InlineData(MobileOp.Graphic, true)]
        [InlineData(MobileOp.Graphic, false)]
        [InlineData(MobileOp.Turn, true)]
        [InlineData(MobileOp.Turn, false)]
        [InlineData(MobileOp.Stop, true)]
        [InlineData(MobileOp.Stop, false)]
        [InlineData(MobileOp.Hits, true)]
        [InlineData(MobileOp.Hits, false)]
        [InlineData(MobileOp.Status, true)]
        [InlineData(MobileOp.Status, false)]
        [InlineData(MobileOp.MobileStatus, true)]
        [InlineData(MobileOp.MobileStatus, false)]
        [InlineData(MobileOp.Mana, true)]
        [InlineData(MobileOp.Mana, false)]
        [InlineData(MobileOp.Stamina, true)]
        [InlineData(MobileOp.Stamina, false)]
        [InlineData(MobileOp.Race, true)]
        [InlineData(MobileOp.Race, false)]
        [InlineData(MobileOp.Healthbar, true)]
        [InlineData(MobileOp.Healthbar, false)]
        [InlineData(MobileOp.Animate, true)]
        [InlineData(MobileOp.Animate, false)]
        public void A_Mobile_Operation_Inside_An_Operation_Is_Rejected_Before_Any_Change(MobileOp op, bool throws)
        {
            World world = Build(Start.Standing), reference = Build(Start.Standing);
            world.Writer.ThrowOnViolation = throws;
            Mobile m = world.Mobiles.Get(MOB);
            Mobile other = world.Mobiles.Get(OTHER);
            world.Writer.UpsertMobile(m, MOB, 0x190, 0, 1000, 1000, 0, Direction.North, 0x83EA, 0, out _, out WorldWriter.Placement placement);
            reference.Writer.UpsertMobile(reference.Mobiles.Get(MOB), MOB, 0x190, 0, 1000, 1000, 0, Direction.North, 0x83EA, 0, out _, out _);
            bool threw = false;

            // inside another operation's commit (the bystander's notoriety)
            world.Ecs.BeginFold(other);

            try
            {
                Run(world, op, m, placement);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            finally
            {
                world.Ecs.EndFold(other);
            }

            threw.Should().Be(throws);
            world.Writer.Violations.Should().Be(1);
            world.Writer.OutstandingPlacements.Should().Be(1, "a rejected operation touches no placement");
            AssertSameMobile(reference, world, MOB);
        }

        [Theory]
        [InlineData(MobileOp.Upsert, true)]
        [InlineData(MobileOp.Upsert, false)]
        [InlineData(MobileOp.Notoriety, true)]
        [InlineData(MobileOp.Notoriety, false)]
        [InlineData(MobileOp.Body, true)]
        [InlineData(MobileOp.Body, false)]
        [InlineData(MobileOp.Move, true)]
        [InlineData(MobileOp.Move, false)]
        [InlineData(MobileOp.Graphic, true)]
        [InlineData(MobileOp.Graphic, false)]
        [InlineData(MobileOp.Turn, true)]
        [InlineData(MobileOp.Turn, false)]
        [InlineData(MobileOp.Stop, true)]
        [InlineData(MobileOp.Stop, false)]
        [InlineData(MobileOp.Hits, true)]
        [InlineData(MobileOp.Hits, false)]
        [InlineData(MobileOp.Status, true)]
        [InlineData(MobileOp.Status, false)]
        [InlineData(MobileOp.MobileStatus, true)]
        [InlineData(MobileOp.MobileStatus, false)]
        [InlineData(MobileOp.Mana, true)]
        [InlineData(MobileOp.Mana, false)]
        [InlineData(MobileOp.Stamina, true)]
        [InlineData(MobileOp.Stamina, false)]
        [InlineData(MobileOp.Race, true)]
        [InlineData(MobileOp.Race, false)]
        [InlineData(MobileOp.Healthbar, true)]
        [InlineData(MobileOp.Healthbar, false)]
        [InlineData(MobileOp.Animate, true)]
        [InlineData(MobileOp.Animate, false)]
        public void A_Mobile_This_World_Does_Not_Hold_Is_Rejected_Before_Any_Change(MobileOp op, bool throws)
        {
            foreach (bool foreign in new[] { true, false })
            {
                World world = Build(Start.Standing), reference = Build(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Mobile stranger;

                if (foreign)
                {
                    stranger = Build(Start.Standing).Mobiles.Get(MOB); // another World's
                }
                else
                {
                    // a stale instance: World replaced it under its serial
                    stranger = Mobile.Create(world, MOB);
                }

                Action act = () => Run(world, op, stranger);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                world.Writer.Violations.Should().Be(1);
                world.Writer.InScope.Should().BeFalse();
                world.Writer.OutstandingPlacements.Should().Be(0);
                AssertSameWorld(reference, world);
                (stranger.X, stranger.NotorietyFlag).Should().Be(foreign ? ((ushort)1000, NotorietyFlag.Innocent) : ((ushort)0, (NotorietyFlag)0), "the stranger is untouched");
                StatusOf(stranger).Should().Be(foreign ? StatusOf(Build(Start.Standing).Mobiles.Get(MOB)) : StatusOf(Mobile.Create(world, MOB)), "the stranger's status is untouched");
            }
        }

        [Fact]
        public void The_Mobile_Fold_Skips_The_Hooks_Of_Its_Instance_Only()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB), other = world.Mobiles.Get(OTHER);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e);
            world.Ecs.Mirror.TryGetEntity(OTHER, out EcsEntity eo);
            long writes = world.Ecs.MobileMirrorWrites;

            world.Ecs.BeginFold(m);
            m.Direction = Direction.West; // Sync: folded
            m.NotorietyFlag = NotorietyFlag.Murderer; // PushInputs: folded
            m.IsRunning = true; // PushInputs: folded
            other.Direction = Direction.West; // another instance: syncs
            world.Ecs.EndFold(m);

            world.Ecs.MobileMirrorWrites.Should().Be(writes + 1, "only the bystander's Sync wrote");
            e.GetComponent<Facing>().Value.Should().Be((byte)Direction.North);
            Inputs(world, MOB).Should().Be(Inputs(Build(Start.Standing), MOB), "the folded pushes did not run");
            eo.GetComponent<Facing>().Value.Should().Be((byte)Direction.West);

            m.IsRunning = true; // after the fold: pushes again
            e.GetComponent<MobileInputs>().Flags.Should().NotBe(Inputs(Build(Start.Standing), MOB).Flags, "the running bit is pushed");
        }

        private static MobileInputs Inputs(World w, uint serial)
        {
            w.Ecs.Mirror.TryGetEntity(serial, out EcsEntity e);

            MobileInputs i = e.GetComponent<MobileInputs>();
            i.Pushes = 0;

            return i;
        }

        // ---- 0x1D for a mobile

        private const uint PACK = 0x4000_7010, LOOT = 0x4000_7011, GEM = 0x4000_7012;

        // MOB wears ITEM (Build) and a backpack holding LOOT, which holds GEM; LOOT's 0x1A placement is still outstanding
        private static World BuildRemove(Start start, out WorldWriter.Placement lootPlacement)
        {
            World world = Build(start);
            Mobile m = world.Mobiles.Get(MOB);
            lootPlacement = default;

            if (m == null || m.IsDestroyed)
            {
                return world;
            }

            Item pack = world.GetOrCreateItem(PACK);
            pack.Graphic = 0xE75;
            pack.Layer = Layer.Backpack;
            pack.Container = MOB;
            m.PushToBack(pack);
            world.Ecs.Sync(pack);

            Item loot = world.Writer.UpsertWorldItem(LOOT, 0xE76, 0, 1, 1000, 1000, 0, 0, 0, 0, 0, out _, out lootPlacement);
            loot.Container = PACK;
            pack.PushToBack(loot);
            world.Ecs.Sync(loot);
            Item gem = world.GetOrCreateItem(GEM);
            gem.Graphic = 0xF10;
            gem.Container = LOOT;
            loot.PushToBack(gem);
            world.Ecs.Sync(gem);
            WorldWriterTests.AttachLists(world);

            return world;
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.FullQueue)]
        [InlineData(Start.Unplaced)]
        public void RemoveMobile_Equals_The_Old_Path(Start start)
        {
            World oldWorld = BuildRemove(start, out _), newWorld = BuildRemove(start, out _);

            Pair(() => oldWorld.RemoveMobile(MOB, true), () => newWorld.Writer.RemoveMobile((Mobile)newWorld.Get(MOB)));

            newWorld.Mobiles.Get(MOB).Should().BeNull();
            newWorld.Items.Get(GEM).Should().BeNull("the contents of the worn backpack go too");
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void RemoveMobile_Cancels_The_Placements_Of_The_Mobile_And_Its_Subtree()
        {
            World world = BuildRemove(Start.Standing, out WorldWriter.Placement loot);
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.ThrowOnViolation = true;
            world.Writer.UpsertMobile(m, MOB, 0x190, 0, 1001, 1000, 0, Direction.East, 0, 0, out _, out WorldWriter.Placement mobile);
            world.Writer.OutstandingPlacements.Should().Be(2);

            world.Writer.RemoveMobile(m);

            world.Writer.OutstandingPlacements.Should().Be(0);
            Action placeLoot = () => world.Writer.PlaceWorldItem(loot);
            placeLoot.Should().Throw<ArgumentException>("the removed item's placement was cancelled, not left to be consumed");
            Action placeMobile = () => world.Writer.PlaceMobile(mobile);
            placeMobile.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RemoveMobile_Rejects_Nesting_And_A_Mobile_This_World_Does_Not_Hold(bool throws)
        {
            World world = BuildRemove(Start.Standing, out _), reference = BuildRemove(Start.Standing, out _);
            world.Writer.ThrowOnViolation = throws;
            Mobile m = world.Mobiles.Get(MOB);

            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));
            Action nested = () => world.Writer.RemoveMobile(m);

            if (throws)
            {
                nested.Should().Throw<InvalidOperationException>();
            }
            else
            {
                nested();
            }

            world.Ecs.EndFold(world.Mobiles.Get(OTHER));

            foreach (Mobile stranger in new[] { Build(Start.Standing).Mobiles.Get(MOB), Mobile.Create(world, MOB) })
            {
                Action act = () => world.Writer.RemoveMobile(stranger);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }
            }

            world.Writer.Violations.Should().Be(3);
            world.Writer.OutstandingPlacements.Should().Be(1, "a rejected removal cancels nothing");
            AssertSameWorld(reference, world);
        }

        // ---- Codex audit of part 4: the player's position callbacks, a mobile placed as an item, the detached player

        // A PlayerMobile without its constructor (which reads the UO files): GetUninitializedObject, then Mobile's constructor
        // on it, so the PlayerMobile-only fields stay unset (Skills, Walker, Pathfinder, buffs; nothing here touches them).
        // World.Player is set to it. Its position and direction callbacks are the test's: the real ones call the plugins and
        // read the profile. OnMoved stands in for PlayerMobile.OnPositionChanged, where a plugin's RequestMove runs
        // Player.Walk synchronously.
        internal sealed class TestPlayer : PlayerMobile
        {
            public Action<TestPlayer> OnMoved;
            public Action<TestPlayer> OnTurned; // funnel part 5: a stand-in for the door check's packet (plugin send hooks)
            public int PositionCallbacks;
            // Codex audit of part 8, P2 #1: true on the new World's player, whose stand-in walk (StandInWalk) calls what the
            // converted PlayerMobile.Walk calls (TurnPlayer, QueuePlayerStep); false: the base revision's direct writes
            public bool Funnel;

            public TestPlayer(World world, uint serial) : base(world, serial) { } // never runs

            internal static TestPlayer Create(World world, uint serial)
            {
                var p = (TestPlayer)RuntimeHelpers.GetUninitializedObject(typeof(TestPlayer));
                typeof(Mobile).GetConstructor(new[] { typeof(World), typeof(uint) })!.Invoke(p, new object[] { world, serial });

                return p;
            }

            protected override void OnPositionChanged()
            {
                PositionCallbacks++;
                Action<TestPlayer> walk = OnMoved;
                OnMoved = null; // once
                walk?.Invoke(this);
            }

            protected override void OnDirectionChanged()
            {
                Action<TestPlayer> turn = OnTurned;
                OnTurned = null; // once
                turn?.Invoke(this);
            }
        }

        internal static World BuildPlayer(out TestPlayer player, uint serial = MOB)
        {
            World world = Build(Start.None);
            player = TestPlayer.Create(world, serial);
            typeof(World).GetProperty(nameof(World.Player))!.SetValue(world, player);
            world.Mobiles.Add(player);
            world.Ecs.Sync(player);
            player.Graphic = 0x190;
            player.Hue = 0x83EA;
            player.Direction = Direction.North;
            player.NotorietyFlag = NotorietyFlag.Innocent;
            player.SetInWorldTile(1000, 1000, 0);
            player.UpdateRealScreenPositionOnly(0, 0);

            return world;
        }

        // Player.Walk's writes, as a plugin's RequestMove makes them from the position (or the door check's) callback: two
        // requests, a coalesced turn, then a step. On the new World (TestPlayer.Funnel) they go through the operations the
        // converted Walk calls (TurnPlayer, QueuePlayerStep: admission, CloseBank, PlanWalkStep, ApplyStep), which must accept
        // them while the operation that runs the callback is open (Codex audit of part 8, P2 #1); on the old World they are
        // the base revision's writes (Direction; OldWalkStep, Walk from CloseBank to PushInputs). Walk's own checks before
        // them (the walker, the pathfinder, the profile) and the walker's record after them are not world state
        internal static Action<TestPlayer> StandInWalk(Direction to) => p =>
        {
            if (p.Funnel)
            {
                p.World.Writer.TurnPlayer(p, to).Should().BeTrue("a coalesced turn from a callback is accepted");
            }
            else
            {
                p.Direction = to;
            }

            int x = p.X, y = p.Y;
            Pathfinder.GetNewXY((byte)to, ref x, ref y);

            if (p.Funnel)
            {
                p.World.Writer.QueuePlayerStep(p, x, y, p.Z, to, true).Should().BeTrue("a step request from a callback is accepted");
            }
            else
            {
                OldWalkStep(p, x, y, p.Z, to, true);
            }
        };

        public enum PlayerPlacement { UpdateObject, UpdatePlayer }

        [Theory]
        [InlineData(PlayerPlacement.UpdateObject)]
        [InlineData(PlayerPlacement.UpdatePlayer)]
        public void A_Walk_From_The_Players_Position_Callback_Is_Published_As_Before_T1(PlayerPlacement kind)
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            b.Funnel = true; // the new World's callbacks walk through the converted Walk path
            newWorld.Writer.ThrowOnViolation = true;
            Time.Ticks = NOW + 2000;
            a.OnMoved = StandInWalk(Direction.West);
            b.OnMoved = StandInWalk(Direction.West);

            if (kind == PlayerPlacement.UpdateObject)
            {
                // UpdateGameObject's player branch (no step), then the placement with the callback
                MobPacket p = Packets[1];
                Pair(() => OldUpsertMobile(oldWorld, p), () => NewUpsertMobile(newWorld, p));
            }
            else
            {
                Pair
                (
                    () => OldBody(oldWorld, a, BodyPacket.UpdatePlayer, 0x191, 0x21, Flags.WarMode, Direction.South, 1005, 1006, 3),
                    () => NewBody(newWorld, b, BodyPacket.UpdatePlayer, 0x191, 0x21, Flags.WarMode, Direction.South, 1005, 1006, 3)
                );
            }

            Time.Ticks = NOW;
            b.PositionCallbacks.Should().Be(a.PositionCallbacks).And.BeGreaterThan(1);
            b.Steps.Count.Should().BeGreaterThan(0, "the callback walked");
            b.Direction.Should().Be(Direction.West);
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            newWorld.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void PlaceInContainer_Of_A_Mobile_Serial_Cancels_The_Placements_Of_The_Mobile_And_Its_Subtree()
        {
            World world = BuildRemove(Start.Standing, out WorldWriter.Placement loot);
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.ThrowOnViolation = true;
            world.Writer.UpsertMobile(m, MOB, 0x190, 0, 1001, 1000, 0, Direction.East, 0, 0, out _, out WorldWriter.Placement mobile);
            world.Writer.OutstandingPlacements.Should().Be(2);

            world.Writer.PlaceInContainer(MOB, 0xE76, 1, 10, 10, 0, BAG, out Entity container).Should().NotBeNull();

            container.Serial.Should().Be(BAG);
            world.Mobiles.Get(MOB).Should().BeNull();
            world.Items.Get(GEM).Should().BeNull("the mobile's subtree went with it");
            m.IsDestroyed.Should().BeTrue();
            world.Writer.OutstandingPlacements.Should().Be(0);
            Action placeLoot = () => world.Writer.PlaceWorldItem(loot);
            placeLoot.Should().Throw<ArgumentException>("the removed descendant's placement was cancelled");
            Action placeMobile = () => world.Writer.PlaceMobile(mobile);
            placeMobile.Should().Throw<ArgumentException>("the removed mobile's placement was cancelled");
            world.Writer.InScope.Should().BeFalse();
        }

        private static string PlayerSnapshot(TestPlayer m) => Snapshot(m) + $" destroyed {m.IsDestroyed} callbacks {m.PositionCallbacks}";

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_Detached_World_Player_Is_Written_As_Before_T1(bool throws)
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            b.Funnel = true; // the new World's callbacks walk through the converted Walk path
            newWorld.Writer.ThrowOnViolation = throws;

            // 0x25 under the player's serial: the mobile goes, World.Player stays
            foreach (World w in new[] { oldWorld, newWorld })
            {
                w.Writer.PlaceInContainer(MOB, 0xE76, 1, 10, 10, 0, BAG, out _).Should().NotBeNull();
            }

            newWorld.Player.Should().BeSameAs(b);
            newWorld.Mobiles.Get(MOB).Should().BeNull();
            b.IsDestroyed.Should().BeTrue();
            AssertSameWorld(oldWorld, newWorld);

            // 0x78 (the player's body), then 0x20 (body, direction, move with the position callback)
            Pair
            (
                () => OldBody(oldWorld, a, BodyPacket.UpdateObject, 0x25E, 0x8BB9, Flags.Hidden, Direction.East, 0, 0, 0),
                () => NewBody(newWorld, b, BodyPacket.UpdateObject, 0x25E, 0x8BB9, Flags.Hidden, Direction.East, 0, 0, 0)
            );
            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
            Pair
            (
                () => OldBody(oldWorld, a, BodyPacket.UpdatePlayer, 0x191, 0x21, Flags.WarMode, Direction.South, 1005, 1006, 3),
                () => NewBody(newWorld, b, BodyPacket.UpdatePlayer, 0x191, 0x21, Flags.WarMode, Direction.South, 1005, 1006, 3)
            );

            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
            (b.Graphic, b.X, b.Y, b.Z, b.Direction).Should().Be(((ushort)0x191, (ushort)1005, (ushort)1006, (sbyte)3, Direction.South));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();

            // any other instance World does not hold is still rejected
            Mobile stranger = Mobile.Create(newWorld, MOB);
            Action move = () => newWorld.Writer.MoveMobile(stranger, 1200, 1200, 0);
            Action body = () => newWorld.Writer.SetMobileBody(stranger, 0x190, 0, 0, true, false, 0);

            if (throws)
            {
                move.Should().Throw<ArgumentException>();
                body.Should().Throw<ArgumentException>();
            }
            else
            {
                move();
                body();
            }

            newWorld.Writer.Violations.Should().Be(2);
            stranger.X.Should().NotBe(1200);
        }

        // ---- groundwork: the pure helpers equal the code they were split from

        [Fact]
        public void BodyState_Equals_The_Old_CheckGraphicChange()
        {
            var world = new World();

            for (int g = 0; g <= 0x500; g++)
            {
                Mobile a = world.GetOrCreateMobile(0x100), b = world.GetOrCreateMobile(0x101);
                a.IsFemale = b.IsFemale = true;
                a.Race = b.Race = RaceType.ELF;
                a.Graphic = b.Graphic = (ushort)g;
                OldCheckGraphicChange(a);
                b.CheckGraphicChange();
                (b.IsFemale, b.Race).Should().Be((a.IsFemale, a.Race), $"graphic 0x{g:X}");
            }
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.OneStep)]
        [InlineData(Start.Walking)]
        [InlineData(Start.FullQueue)]
        public void EnqueueStep_Equals_The_Old_EnqueueStep(Start start)
        {
            var targets = new (int X, int Y, sbyte Z, Direction Dir, bool Run)[]
            {
                (1000, 1000, 0, Direction.North, false), (1001, 1000, 0, Direction.East, false), (1003, 1004, 2, Direction.West, true),
                (1003, 1004, 2, Direction.West, true), (1003, 1004, 2, Direction.Up, false), (999, 999, 0, Direction.Up, true),
            };

            foreach (bool walkingNow in new[] { false, true })
            {
                World oldWorld = Build(start), newWorld = Build(start);
                Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);

                if (walkingNow)
                {
                    a.LastStepTime = b.LastStepTime = NOW - 10;
                }

                foreach (var t in targets)
                {
                    bool ra = false, rb = false;
                    Pair(() => ra = OldEnqueueStep(a, t.X, t.Y, t.Z, t.Dir, t.Run), () => rb = b.EnqueueStep(t.X, t.Y, t.Z, t.Dir, t.Run));
                    rb.Should().Be(ra);
                    AssertSameWorld(oldWorld, newWorld);
                }
            }
        }

        // ---- the comparison

        private static void AssertSameWorld(World a, World b, uint mob = MOB)
        {
            b.Items.Count.Should().Be(a.Items.Count);
            b.Mobiles.Count.Should().Be(a.Mobiles.Count);
            b.ActiveItems.Select(i => i.Serial).Should().Equal(a.ActiveItems.Select(i => i.Serial));

            foreach (uint serial in a.Mobiles.Values.Select(m => m.Serial).Union(b.Mobiles.Values.Select(m => m.Serial)).Union(new[] { mob, NEWMOB, OTHER }).OrderBy(s => s))
            {
                AssertSameMobile(a, b, serial);
            }

            foreach (uint serial in a.Items.Keys.Union(b.Items.Keys))
            {
                Item x = a.Items.Get(serial), y = b.Items.Get(serial);
                (y == null).Should().Be(x == null);

                if (x != null)
                {
                    (y.Graphic, y.Hue, y.X, y.Y, y.Z, y.Container, y.Layer, y.Amount, y.Direction).Should().Be((x.Graphic, x.Hue, x.X, x.Y, x.Z, x.Container, x.Layer, x.Amount, x.Direction));
                    StatusOf(y).Should().Be(StatusOf(x), $"0x{serial:X8} status");
                    MiscOf(y).Should().Be(MiscOf(x), $"0x{serial:X8} price (funnel part 7)");
                }

                AssertSameRecord(a, b, serial);
            }

            WorldMirror ma = a.Ecs.Mirror, mb = b.Ecs.Mirror;
            mb.Count.Should().Be(ma.Count);
            mb.MobileCount.Should().Be(ma.MobileCount);
            mb.ItemCount.Should().Be(ma.ItemCount);
            mb.Epoch.Should().Be(ma.Epoch);
        }

        private static void AssertSameMobile(World a, World b, uint serial)
        {
            Mobile x = a.Mobiles.Get(serial), y = b.Mobiles.Get(serial);
            (y == null).Should().Be(x == null, $"0x{serial:X8} exists in both or neither");

            if (x != null)
            {
                y.Graphic.Should().Be(x.Graphic);
                y.Hue.Should().Be(x.Hue);
                (y.X, y.Y, y.Z).Should().Be((x.X, x.Y, x.Z));
                y.Direction.Should().Be(x.Direction);
                y.Flags.Should().Be(x.Flags);
                y.IsFemale.Should().Be(x.IsFemale);
                y.Race.Should().Be(x.Race);
                y.IsRunning.Should().Be(x.IsRunning);
                y.NotorietyFlag.Should().Be(x.NotorietyFlag);
                y.Offset.Should().Be(x.Offset);
                y.LastStepTime.Should().Be(x.LastStepTime);
                y.IsPositionChanged.Should().Be(x.IsPositionChanged);
                y.IsDestroyed.Should().Be(x.IsDestroyed);
                y.UpdateStateHash().Should().Be(x.UpdateStateHash(), "the animation state, the queue and the timers");
                StatusOf(y).Should().Be(StatusOf(x), $"0x{serial:X8} status (funnel part 6)");
                MiscOf(y).Should().Be(MiscOf(x), $"0x{serial:X8} speed, dead and war mode (funnel part 7)");
                y.ExecuteAnimation.Should().Be(x.ExecuteAnimation);
                Enumerable.Range(0, y.Steps.Count).Select(i => y.Steps[i]).Should().Equal(Enumerable.Range(0, x.Steps.Count).Select(i => x.Steps[i]), "the walk queue");
                ChildSerials(y).Should().Equal(ChildSerials(x));
            }

            AssertSameRecord(a, b, serial);
        }

        private static void AssertSameRecord(World a, World b, uint serial)
        {
            WorldMirror ma = a.Ecs.Mirror, mb = b.Ecs.Mirror;
            bool ha = ma.TryGet(serial, out MirrorRecord ra), hb = mb.TryGet(serial, out MirrorRecord rb);
            hb.Should().Be(ha, $"0x{serial:X8} mirrored in both or neither");
            rb.Should().BeEquivalentTo(ra, $"the mirror record of 0x{serial:X8}");

            if (!ha)
            {
                return;
            }

            ma.TryGetEntity(serial, out EcsEntity ea);
            mb.TryGetEntity(serial, out EcsEntity eb);
            eb.Id.Should().Be(ea.Id, "the same entities are created in the same order");
            eb.Tags.Has<EquipmentDirty>().Should().Be(ea.Tags.Has<EquipmentDirty>(), $"0x{serial:X8} equipment dirty");
            eb.Tags.Has<PlayerTag>().Should().Be(ea.Tags.Has<PlayerTag>());
            eb.Tags.Has<OnGround>().Should().Be(ea.Tags.Has<OnGround>());
            eb.HasComponent<MobileInputs>().Should().Be(ea.HasComponent<MobileInputs>());

            if (ea.HasComponent<MobileInputs>())
            {
                // Pushes counts the pushes (a diagnostic: the funnel folds the hook pushes into one)
                MobileInputs ia = ea.GetComponent<MobileInputs>(), ib = eb.GetComponent<MobileInputs>();
                ia.Pushes = ib.Pushes = 0;
                ib.Should().Be(ia, $"0x{serial:X8} inputs");
            }

            eb.HasComponent<MobileClock>().Should().Be(ea.HasComponent<MobileClock>());

            if (ea.HasComponent<MobileClock>())
            {
                eb.GetComponent<MobileClock>().Should().BeEquivalentTo(ea.GetComponent<MobileClock>(), $"0x{serial:X8} clock");
            }
        }

        // funnel part 6: the cache-only status (the hits and their derived percentage, the hits request, the name; for a mobile
        // the mana, the stamina, IsRenamable, the race and the SA poison)
        // the caches the operations write (T4: with ecs_owns_scalars on the getters read the record, which the old paths never
        // wrote; the records are compared by -ecscheck v2 and EcsScalarsTests)
        internal static string StatusOf(Entity e) =>
            $"{e.CachedName} {e.CachedHits}/{e.CachedHitsMax} {e.HitsPercentage}% {e.HitsRequest}" +
            (e is Mobile m ? $" mana {m.CachedMana}/{m.CachedManaMax} stamina {m.CachedStamina}/{m.CachedStaminaMax} renamable {m.IsRenamable} race {m.Race} sa {m.SAPoisoned}" : "");

        private static List<uint> ChildSerials(Entity e)
        {
            var list = new List<uint>();

            for (LinkedObject i = e?.Items; i != null; i = i.Next)
            {
                list.Add(((Entity)i).Serial);
            }

            return list;
        }
    }

    [Collection(nameof(FunnelCheckCollection))]
    public class MobileFunnelCheckTests : IDisposable
    {
        public MobileFunnelCheckTests()
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
        public void The_Mobile_Operations_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            Time.Ticks = 100_000;
            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0000_3100);
            m.Graphic = 0x190;
            m.SetInWorldTile(1000, 1000, 0);
            FunnelCheck.Reset();

            // a new mobile (creation, sex and race), a step (SetAnimation, LastStepTime, the queue), a full queue's reset,
            // the unplaced reset, then the placements
            Mobile n = world.Writer.UpsertMobile(null, 0x0000_3101, 0x191, 0, 1002, 1002, 0, Direction.Running | Direction.East, 0x8BB9, Flags.Hidden, out bool created, out WorldWriter.Placement p1);
            created.Should().BeTrue();
            world.Writer.PlaceMobile(p1);

            for (int i = 1; i <= Constants.MAX_STEP_COUNT + 2; i++)
            {
                world.Writer.UpsertMobile(m, m.Serial, 0x190, 0, (ushort)(1000 + i), 1000, 0, Direction.East, 0, 0, out _, out WorldWriter.Placement p);
                world.Writer.PlaceMobile(p);
            }

            world.Writer.UpsertMobile(n, n.Serial, 0x191, 0, 0xFFFF, 0xFFFF, 0, Direction.North, 0, 0, out _, out WorldWriter.Placement p2);
            world.Writer.PlaceMobile(p2);
            m.Steps.Count.Should().BeGreaterThan(0);
            FunnelCheck.Total.Should().Be(0, "0x77/0x78 for another mobile");

            // 0x77 / 0x78 / 0x20 for the player (here a plain mobile)
            world.Writer.SetNotoriety(m, NotorietyFlag.Criminal);
            world.Writer.SetMobileBody(m, 0x25E, 0x21, Flags.WarMode, checkBody: true, setDirection: false, 0);
            world.Writer.SetMobileBody(m, 0x190, 0, 0, checkBody: false, setDirection: true, Direction.South);
            world.Writer.MoveMobile(m, 1500, 1500, 5);
            FunnelCheck.Total.Should().Be(0, "the player's operations");

            // 0x1D: the mobile with a queued step, its equipment and a backpack's contents (the removal owns the subtree)
            Item pack = world.GetOrCreateItem(0x4000_7110);
            pack.Graphic = 0xE75;
            pack.Layer = Layer.Backpack;
            pack.Container = n.Serial;
            n.PushToBack(pack);
            Item loot = world.GetOrCreateItem(0x4000_7111);
            loot.Container = pack.Serial;
            pack.PushToBack(loot);
            WorldWriterTests.AttachLists(world);
            world.Writer.UpsertMobile(n, n.Serial, 0x191, 0, 1003, 1003, 0, Direction.East, 0, 0, out _, out WorldWriter.Placement p3);
            world.Writer.PlaceMobile(p3);
            n.Steps.Count.Should().BeGreaterThan(0);
            FunnelCheck.Reset();
            world.Writer.RemoveMobile(n);
            world.Items.Get(loot.Serial).Should().BeNull();
            FunnelCheck.Total.Should().Be(0, "0x1D");
        }
    }
}
