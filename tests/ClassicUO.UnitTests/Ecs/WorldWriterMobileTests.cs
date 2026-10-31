using System;
using System.Collections.Generic;
using System.Linq;
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
    public class WorldWriterMobileTests
    {
        private const uint MOB = 0x0000_3000, OTHER = 0x0000_3001, NEWMOB = 0x0000_3002, DEAD = 0x8000_3000, BAG = 0x4000_7000, ITEM = 0x4000_7001;

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

        public enum Start { None, Standing, Walking, FullQueue, Unplaced, DestroyedHeld }

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

            // the mobile's equipment tag starts clear: a body change sets it
            if (world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity me) && me.Tags.Has<EquipmentDirty>())
            {
                me.RemoveTag<EquipmentDirty>();
            }

            return world;
        }


        // ---- 0x77 / 0x78 / 0x20: the player's body, the notoriety, the move (on a plain mobile: see the class note)

        // ---- 0x77's lookups: the notoriety on World.Mobiles.Get's mobile, then UpdateGameObject's World.Get (an item first
        // for a serial outside the mobile range, e.g. a death-rekeyed mobile's)

        // ---- placements, nesting, foreign and stale instances

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
}
