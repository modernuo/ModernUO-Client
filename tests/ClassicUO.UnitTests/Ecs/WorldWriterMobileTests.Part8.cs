using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using Flags = ClassicUO.Game.Data.Flags;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 8 (ecs-transition-plan.md, T1): the remaining per-frame and walk writers. Same method as parts 4-7: a copy
    // of the old code on one World (the base revision 9ade17d63, reading and writing private fields by reflection where the
    // operation uses a helper), the funnel on another, identical starts, everything compared (AssertSameWorld; the clock
    // pushes counted).
    public partial class WorldWriterMobileTests
    {
        // ---- group 1: the idle animation (Mobile.SetIdleAnimation) and the footstep sound (ProcessFootstepsSound)

        private static readonly System.Reflection.FieldInfo FGroup = TimerField("_animationGroup"), FIdle = TimerField("_lastAnimationIdleDelay");

        /// <summary>The animation data the idle planner reads, as the tests set it.</summary>
        internal struct FakeIdleData : Mobile.IIdleAnimationData
        {
            public int Max;
            public AnimationGroupsType Type;
            public AnimationFlags Flags;
            public byte[] Exists;
            public byte Replaced;

            public int MaxAnimationCount => Max;
            public AnimationGroupsType GetAnimType(ushort graphic) => Type;
            public AnimationFlags GetAnimFlags(ushort graphic) => Flags;
            public bool AnimationExists(ushort graphic, byte group) => Exists != null && Array.IndexOf(Exists, group) >= 0;
            public byte GetReplacedObjectAnimation(ushort graphic, ushort index) => Replaced;
        }

        private static readonly byte[,] IdleTable =
        {
            { (byte)LowAnimationGroup.Fidget1, (byte)LowAnimationGroup.Fidget2, (byte)LowAnimationGroup.Fidget1 },
            { (byte)HighAnimationGroup.Fidget1, (byte)HighAnimationGroup.Fidget2, (byte)HighAnimationGroup.Fidget1 },
            { (byte)PeopleAnimationGroup.Fidget1, (byte)PeopleAnimationGroup.Fidget2, (byte)PeopleAnimationGroup.Fidget3 }
        };

        // the base revision's SetIdleAnimation, with Client.Game.UO.Animations replaced by the test's data
        internal static void OldSetIdle(Mobile m, FakeIdleData animations)
        {
            // CalculateRandomIdleTime
            FIdle.SetValue(m, Time.Ticks + (30000 + RandomHelper.GetValue(0, 30000)));
            m.World.Ecs.PushClock(m);

            if (!m.IsMounted && !m.InWarMode && m.ExecuteAnimation)
            {
                m.AnimIndex = 0;
                m.AnimationFrameCount = 0;
                FInterval.SetValue(m, (byte)1);
                FRepeatMode.SetValue(m, (ushort)1);
                FRepeatModeCount.SetValue(m, (ushort)1);
                FForward.SetValue(m, true);
                FRepeat.SetValue(m, false);
                m.AnimationFromServer = true;

                ushort graphic = m.GetGraphicForAnimation();

                if (graphic >= animations.MaxAnimationCount)
                {
                    return;
                }

                AnimationGroupsType type = animations.GetAnimType(graphic);
                AnimationFlags flags = animations.GetAnimFlags(graphic);
                AnimationGroups animGroup = AnimationGroups.None;
                bool isLowExtended = false;

                if ((flags & AnimationFlags.CalculateOffsetLowGroupExtended) != 0)
                {
                    isLowExtended = true;
                    type = AnimationGroupsType.Monster;
                }
                else if ((flags & AnimationFlags.CalculateOffsetByLowGroup) != 0)
                {
                    type = AnimationGroupsType.Animal;
                }

                switch (type)
                {
                    case AnimationGroupsType.SeaMonster:
                    case AnimationGroupsType.Monster: animGroup = AnimationGroups.High; break;
                    case AnimationGroupsType.Animal: animGroup = AnimationGroups.Low; break;
                    case AnimationGroupsType.Human:
                    case AnimationGroupsType.Equipment: animGroup = AnimationGroups.People; break;
                }

                if (animGroup == 0)
                {
                    return;
                }

                if ((flags & AnimationFlags.UseUopAnimation) != 0)
                {
                    if (animGroup != AnimationGroups.People)
                    {
                        FGroup.SetValue(m, (byte)(m.InWarMode ? 28 : 26));

                        return;
                    }

                    // (IsGargoyle && IsFlying needs Client.Game: no test start reaches it)
                }

                int first_value = RandomHelper.GetValue(0, 2);
                byte original_value = (byte)FGroup.GetValue(m);
                FGroup.SetValue(m, IdleTable[(byte)animGroup - 1, first_value]);

                if (isLowExtended && (byte)FGroup.GetValue(m) == 18)
                {
                    if (!animations.AnimationExists(graphic, 18) && animations.AnimationExists(graphic, 17))
                    {
                        FGroup.SetValue(m, animations.GetReplacedObjectAnimation(graphic, 17));
                    }
                    else
                    {
                        FGroup.SetValue(m, (byte)1);
                    }
                }

                if (!animations.AnimationExists(graphic, (byte)FGroup.GetValue(m)))
                {
                    first_value = first_value == 0 ? 1 : 0;
                    FGroup.SetValue(m, IdleTable[(byte)animGroup - 1, first_value]);

                    if (!animations.AnimationExists(graphic, (byte)FGroup.GetValue(m)))
                    {
                        m.SetAnimation(original_value);
                    }
                }
            }
        }

        internal static void NewSetIdle(World world, Mobile m, FakeIdleData data)
        {
            m.PlanIdleAnimation(data, out Mobile.IdlePlan plan);
            world.Writer.IdleAnimation(m, in plan);
        }

        public enum IdleData { OutOfRange, PeopleBoth, PeopleSecond, PeopleNone, MonsterLowExtended18, MonsterLowExtendedReplaced, AnimalByLowGroup, UopAnimal, Unknown }

        internal static FakeIdleData IdleDataOf(IdleData d)
        {
            byte p1 = (byte)PeopleAnimationGroup.Fidget1, p2 = (byte)PeopleAnimationGroup.Fidget2, p3 = (byte)PeopleAnimationGroup.Fidget3;

            return d switch
            {
                IdleData.OutOfRange => new FakeIdleData { Max = 0, Type = AnimationGroupsType.Human },
                IdleData.PeopleBoth => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Human, Exists = new[] { p1, p2, p3 } },
                IdleData.PeopleSecond => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Equipment, Exists = new[] { p2 } },
                IdleData.PeopleNone => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Human, Exists = new byte[] { 3 } },
                IdleData.MonsterLowExtended18 => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Animal, Flags = AnimationFlags.CalculateOffsetLowGroupExtended, Exists = new byte[] { 18, 1 } },
                IdleData.MonsterLowExtendedReplaced => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Human, Flags = AnimationFlags.CalculateOffsetLowGroupExtended, Exists = new byte[] { 17, 9 }, Replaced = 9 },
                IdleData.AnimalByLowGroup => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Human, Flags = AnimationFlags.CalculateOffsetByLowGroup, Exists = new[] { (byte)LowAnimationGroup.Fidget2 } },
                IdleData.UopAnimal => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Animal, Flags = AnimationFlags.UseUopAnimation },
                _ => new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Unknown }
            };
        }

        public enum IdleStart { Standing, Animating, WarMode, NotExecuting, Walking }

        private static World BuildIdle(IdleStart start)
        {
            World world = Build(start == IdleStart.Walking ? Start.Walking : Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);

            switch (start)
            {
                case IdleStart.Animating: OldAnimate(m, AnimPacket.NewCharacterAnimation); break;
                case IdleStart.WarMode: m.InWarMode = true; break;
                case IdleStart.NotExecuting: m.ExecuteAnimation = false; break;
            }

            return world;
        }

        public static IEnumerable<object[]> IdleCases()
        {
            foreach (IdleStart s in Enum.GetValues(typeof(IdleStart)))
            {
                foreach (IdleData d in Enum.GetValues(typeof(IdleData)))
                {
                    yield return new object[] { s, d };
                }
            }
        }

        [Theory]
        [MemberData(nameof(IdleCases))]
        public void The_Idle_Animation_Equals_The_Old_Path(IdleStart start, IdleData data)
        {
            World oldWorld = BuildIdle(start), newWorld = BuildIdle(start);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
            FakeIdleData animations = IdleDataOf(data);

            // eight idle timers in a row (each draws new random values, so both idle actions and both pair orders are reached)
            for (int i = 1; i <= 8; i++)
            {
                Time.Ticks = NOW + (uint)(i * 40_000);
                long ca = oldWorld.Ecs.ClockWrites, cb = newWorld.Ecs.ClockWrites;
                Pair(() => OldSetIdle(a, animations), () => NewSetIdle(newWorld, b, animations));
                AssertSameWorld(oldWorld, newWorld);
                ((byte)FGroup.GetValue(b), (long)FIdle.GetValue(b), TimerFieldsOf(b)).Should().Be(((byte)FGroup.GetValue(a), (long)FIdle.GetValue(a), TimerFieldsOf(a)), $"idle {i}");
                (newWorld.Ecs.ClockWrites - cb).Should().Be(oldWorld.Ecs.ClockWrites - ca, "the same clock pushes");
            }

            Time.Ticks = NOW;
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void The_Idle_Planner_Writes_Nothing()
        {
            World world = BuildIdle(IdleStart.Animating);
            Mobile m = world.Mobiles.Get(MOB);
            int before = m.UpdateStateHash();
            long clocks = world.Ecs.ClockWrites;

            foreach (IdleData d in Enum.GetValues(typeof(IdleData)))
            {
                m.PlanIdleAnimation(IdleDataOf(d), out _);
            }

            m.UpdateStateHash().Should().Be(before);
            world.Ecs.ClockWrites.Should().Be(clocks);
        }

        internal static void OldStepSound(Mobile m, int offset, long due)
        {
            m.StepSoundOffset = offset;
            m.LastStepSoundTime = due;
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.DestroyedHeld)]
        public void StepSound_Equals_The_Old_Writes(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
            long clocks = newWorld.Ecs.ClockWrites;

            foreach ((int offset, long due) in new (int, long)[] { (1, NOW + 520), (0, NOW + 1040), (1, NOW + 1500) })
            {
                Pair(() => OldStepSound(a, offset, due), () => newWorld.Writer.StepSound(b, offset, due).Should().BeTrue());
                AssertSameWorld(oldWorld, newWorld);
                (b.StepSoundOffset, b.LastStepSoundTime).Should().Be((offset, due));
            }

            (newWorld.Ecs.ClockWrites - clocks).Should().Be(0);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void The_Idle_And_Step_Sound_Operations_Write_A_Stale_Mobile_And_Reject_Another_Worlds()
        {
            FakeIdleData data = IdleDataOf(IdleData.PeopleBoth);

            foreach (bool throws in new[] { true, false })
            {
                World world = BuildIdle(IdleStart.Animating), other = BuildIdle(IdleStart.Animating);
                world.Writer.ThrowOnViolation = throws;
                Mobile foreign = other.Mobiles.Get(MOB);
                int before = foreign.UpdateStateHash();
                foreign.PlanIdleAnimation(data, out Mobile.IdlePlan plan);
                bool sound = true;
                var acts = new Action[] { () => world.Writer.IdleAnimation(foreign, in plan), () => sound = world.Writer.StepSound(foreign, 1, NOW + 500) };

                foreach (Action act in acts)
                {
                    if (throws)
                    {
                        act.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        act();
                    }
                }

                foreign.UpdateStateHash().Should().Be(before);
                world.Writer.Violations.Should().Be(2);

                if (!throws)
                {
                    sound.Should().BeFalse("a rejected step sound plays nothing");
                }
            }

            // a stale instance of this World: written as before T1, no violation
            World w = BuildIdle(IdleStart.Animating);
            w.Writer.ThrowOnViolation = true;
            Mobile stale = Mobile.Create(w, MOB);
            stale.PlanIdleAnimation(data, out Mobile.IdlePlan p);
            w.Writer.IdleAnimation(stale, in p);
            w.Writer.StepSound(stale, 1, NOW + 777).Should().BeTrue();
            ((long)FIdle.GetValue(stale), stale.AnimationFromServer, stale.LastStepSoundTime, stale.StepSoundOffset).Should().Be((p.IdleDue, true, (long)(NOW + 777), 1));
            w.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void The_Idle_And_Step_Sound_Operations_Do_Not_Nest()
        {
            World world = BuildIdle(IdleStart.Animating);
            world.Writer.ThrowOnViolation = false;
            Mobile m = world.Mobiles.Get(MOB);
            int before = m.UpdateStateHash();
            m.PlanIdleAnimation(IdleDataOf(IdleData.PeopleBoth), out Mobile.IdlePlan plan);
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));
            world.Writer.IdleAnimation(m, in plan);
            world.Writer.StepSound(m, 1, NOW + 500).Should().BeFalse();
            world.Ecs.EndFold(world.Mobiles.Get(OTHER));

            world.Writer.Violations.Should().Be(2);
            m.UpdateStateHash().Should().Be(before);
        }

        // ---- group 2: a corpse's frame advance (Item.ProcessAnimation). The frame comes from the animation data, which the
        // tests do not load: the caller's frame (or none) is a parameter of both paths

        internal static void OldCorpseFrame(Item corpse, int frame)
        {
            if (frame >= 0)
            {
                corpse.AnimIndex = (byte)frame;
            }

            corpse.LastAnimationChangeTime = Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY;
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        public void The_Corpse_Frame_Advance_Equals_The_Old_Path(Start start)
        {
            World oldWorld = BuildCorpse(start), newWorld = BuildCorpse(start);
            newWorld.Writer.ThrowOnViolation = true;
            Item a = oldWorld.Items.Get(CORPSE), b = newWorld.Items.Get(CORPSE);
            long clocks = newWorld.Ecs.ClockWrites;

            int i = 0;

            foreach (int frame in new[] { 0, 1, 2, -1, 3, -1, 7 })
            {
                Time.Ticks = NOW + (uint)(++i * 100);
                Pair(() => OldCorpseFrame(a, frame), () => newWorld.Writer.AdvanceCorpseFrame(b, frame, Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY));
                AssertSameWorld(oldWorld, newWorld);
                (b.AnimIndex, b.LastAnimationChangeTime).Should().Be((a.AnimIndex, a.LastAnimationChangeTime), $"frame {frame}");
            }

            Time.Ticks = NOW;
            b.AnimIndex.Should().Be(7);
            (newWorld.Ecs.ClockWrites - clocks).Should().Be(0);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void AdvanceCorpseFrame_Writes_A_Stale_Item_Rejects_Another_Worlds_And_Does_Not_Nest()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildCorpse(Start.Standing), other = BuildCorpse(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Item foreign = other.Items.Get(CORPSE);
                (byte, long) before = (foreign.AnimIndex, foreign.LastAnimationChangeTime);
                Action act = () => world.Writer.AdvanceCorpseFrame(foreign, 4, NOW + 999);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                (foreign.AnimIndex, foreign.LastAnimationChangeTime).Should().Be(before);
                world.Writer.Violations.Should().Be(1);
            }

            World w = BuildCorpse(Start.Standing);
            w.Writer.ThrowOnViolation = true;
            Item stale = Item.Create(w, CORPSE);
            w.Writer.AdvanceCorpseFrame(stale, 3, NOW + 888);
            (stale.AnimIndex, stale.LastAnimationChangeTime).Should().Be(((byte)3, (long)(NOW + 888)));
            w.Writer.Violations.Should().Be(0);

            w.Writer.ThrowOnViolation = false;
            Item corpse = w.Items.Get(CORPSE);
            (byte, long) held = (corpse.AnimIndex, corpse.LastAnimationChangeTime);
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.AdvanceCorpseFrame(corpse, 5, NOW + 999);
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            (corpse.AnimIndex, corpse.LastAnimationChangeTime).Should().Be(held);
            w.Writer.Violations.Should().Be(1);
        }

        // ---- group 3: a server animation that a step ends (ProcessSteps' SetAnimation(0xFF); ProcessSteps needs Client.Game,
        // so the operation it now calls is compared with the old call)

        [Theory]
        [InlineData(AnimPacket.CharacterAnimation)]
        [InlineData(AnimPacket.NewCharacterAnimation)]

        public void EndServerAnimation_Equals_The_Old_SetAnimation(AnimPacket kind)
        {
            World oldWorld = Build(Start.Walking), newWorld = Build(Start.Walking);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
            Pair(() => OldAnimate(a, kind), () => OldAnimate(b, kind));
            b.AnimationFromServer.Should().BeTrue();
            Time.Ticks = NOW + 300;
            long ca = oldWorld.Ecs.ClockWrites, cb = newWorld.Ecs.ClockWrites;

            Pair(() => a.SetAnimation(0xFF), () => newWorld.Writer.EndServerAnimation(b));
            Time.Ticks = NOW;

            AssertSameWorld(oldWorld, newWorld);
            ((byte)FGroup.GetValue(b), b.AnimationFromServer, (long)FIdle.GetValue(b)).Should().Be(((byte)FGroup.GetValue(a), false, (long)FIdle.GetValue(a)));
            (newWorld.Ecs.ClockWrites - cb).Should().Be(oldWorld.Ecs.ClockWrites - ca).And.BeGreaterThan(0, "SetAnimation pushes the clock, as before");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void EndServerAnimation_Writes_A_Stale_Mobile_Rejects_Another_Worlds_And_Does_Not_Nest()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildIdle(IdleStart.Animating), other = BuildIdle(IdleStart.Animating);
                world.Writer.ThrowOnViolation = throws;
                Mobile foreign = other.Mobiles.Get(MOB);
                int before = foreign.UpdateStateHash();
                Action act = () => world.Writer.EndServerAnimation(foreign);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                foreign.UpdateStateHash().Should().Be(before);
                world.Writer.Violations.Should().Be(1);
            }

            World w = BuildIdle(IdleStart.Animating);
            w.Writer.ThrowOnViolation = true;
            Mobile stale = Mobile.Create(w, MOB);
            stale.AnimationFromServer = true;
            w.Writer.EndServerAnimation(stale);
            stale.AnimationFromServer.Should().BeFalse();
            w.Writer.Violations.Should().Be(0);

            w.Writer.ThrowOnViolation = false;
            Mobile m = w.Mobiles.Get(MOB);
            int held = m.UpdateStateHash();
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.EndServerAnimation(m);
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            m.UpdateStateHash().Should().Be(held);
            m.AnimationFromServer.Should().BeTrue();
            w.Writer.Violations.Should().Be(1);
        }

        // ---- group 4: the player's walk (PlayerMobile.Walk's queue writes and its coalesced turn). Walk itself needs the
        // pathfinder, the network and Client.Game: the plan it computes (PlanWalkStep) and the operations it calls are
        // compared with the old writes

        // the base revision's Walk from CloseBank to PushInputs (the walker's step record is not world state)
        internal static void OldWalkStep(PlayerMobile p, int x, int y, sbyte z, Direction direction, bool run)
        {
            if (p.Steps.Count == 0)
            {
                if (!(p.LastStepTime > Time.Ticks - Constants.PLAYER_WALKING_DELAY))
                {
                    p.SetAnimation(0xFF);
                }

                p.LastStepTime = Time.Ticks;
            }

            p.AddStep(new Mobile.Step { X = x, Y = y, Z = z, Direction = (byte)direction, Run = run });
            p.World.Ecs.PushInputs(p);
        }

        internal static void NewWalkStep(World world, PlayerMobile p, int x, int y, sbyte z, Direction direction, bool run)
        {
            p.PlanWalkStep(x, y, z, direction, run, out Mobile.StepPlan plan);
            world.Writer.QueuePlayerStep(p, in plan).Should().BeTrue();
        }

        internal static World BuildWalkingPlayer(out TestPlayer p) => BuildPlayer(out p);

        [Fact]
        public void The_Players_Walk_Steps_And_Turn_Equal_The_Old_Path()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            Pair(() => OldAnimate(a, AnimPacket.NewCharacterAnimation), () => OldAnimate(b, AnimPacket.NewCharacterAnimation));

            void Step(uint at, int x, int y, Direction d, bool run)
            {
                Time.Ticks = at;
                long ca = oldWorld.Ecs.ClockWrites, cb = newWorld.Ecs.ClockWrites;
                long ia = oldWorld.Ecs.MobileMirrorWrites, ib = newWorld.Ecs.MobileMirrorWrites;
                Pair(() => OldWalkStep(a, x, y, 0, d, run), () => NewWalkStep(newWorld, b, x, y, 0, d, run));
                AssertSameWorld(oldWorld, newWorld);
                PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
                (newWorld.Ecs.ClockWrites - cb, newWorld.Ecs.MobileMirrorWrites - ib).Should().Be((oldWorld.Ecs.ClockWrites - ca, oldWorld.Ecs.MobileMirrorWrites - ia), $"the pushes of the step at {at}");
            }

            // the walk starts while not walking (the server animation is reset), a second step queues behind it
            Step(NOW + 5_000, 1001, 1000, Direction.East, false);
            b.AnimationFromServer.Should().BeFalse();
            Step(NOW + 5_100, 1002, 1000, Direction.East, true);
            b.Steps.Count.Should().Be(2);

            // the queue drained while still walking (LastStepTime from the first step, 120 ms ago): no animation reset
            Pair(() => a.ClearSteps(), () => b.ClearSteps());
            Pair(() => OldAnimate(a, AnimPacket.NewCharacterAnimation), () => OldAnimate(b, AnimPacket.NewCharacterAnimation));
            Step(NOW + 5_120, 1001, 1001, Direction.South, false);
            b.AnimationFromServer.Should().BeTrue("still walking: no SetAnimation");

            // the coalesced turn (three unconfirmed requests): the direction alone
            Time.Ticks = NOW + 5_200;
            Pair(() => a.Direction = Direction.West, () => newWorld.Writer.SetMobileDirection(b, Direction.West));
            AssertSameWorld(oldWorld, newWorld);
            PlayerSnapshot(b).Should().Be(PlayerSnapshot(a));
            b.Direction.Should().Be(Direction.West);

            Time.Ticks = NOW;
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void PlanWalkStep_Writes_Nothing()
        {
            BuildPlayer(out TestPlayer p);
            Time.Ticks = NOW + 5_000;
            int before = p.UpdateStateHash();
            p.PlanWalkStep(1001, 1000, 0, Direction.East, true, out Mobile.StepPlan plan);
            Time.Ticks = NOW;

            p.UpdateStateHash().Should().Be(before);
            (plan.Accepted, plan.Changes, plan.StartsWalk, plan.SetsAnimation, plan.StepTime, plan.Count).Should().Be((true, true, true, true, NOW + 5_000u, 1));
            (plan.S0.X, plan.S0.Y, plan.S0.Z, plan.S0.Direction, plan.S0.Run).Should().Be((1001, 1000, (sbyte)0, (byte)Direction.East, true));
        }

        [Fact]
        public void QueuePlayerStep_Rejects_Anything_But_This_Worlds_Player_And_Does_Not_Nest()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildPlayer(out TestPlayer p), other = BuildPlayer(out TestPlayer foreign);
                world.Writer.ThrowOnViolation = throws;
                Mobile bystander = world.Mobiles.Get(OTHER);
                p.PlanWalkStep(1001, 1000, 0, Direction.East, false, out Mobile.StepPlan plan);
                int hp = foreign.UpdateStateHash(), hb = bystander.UpdateStateHash();
                bool result = true;

                foreach (Mobile m in new Mobile[] { foreign, bystander })
                {
                    Action act = () => result = world.Writer.QueuePlayerStep(m, in plan);

                    if (throws)
                    {
                        act.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        act();
                        result.Should().BeFalse();
                    }
                }

                (foreign.UpdateStateHash(), bystander.UpdateStateHash()).Should().Be((hp, hb));
                world.Writer.Violations.Should().Be(2);
            }

            World w = BuildPlayer(out TestPlayer player);
            w.Writer.ThrowOnViolation = false;
            player.PlanWalkStep(1001, 1000, 0, Direction.East, false, out Mobile.StepPlan p2);
            int before = player.UpdateStateHash();
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.QueuePlayerStep(player, in p2).Should().BeFalse();
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            player.UpdateStateHash().Should().Be(before);
            player.Steps.Count.Should().Be(0);
            w.Writer.Violations.Should().Be(1);
        }

        // ---- group 5: logout's removals (World.Clear's two loops; the rest of Clear is unchanged)

        internal static void OldClearRemovals(World world)
        {
            foreach (Mobile mobile in world.Mobiles.Values)
            {
                world.RemoveMobile(mobile);
            }

            foreach (Item item in world.Items.Values)
            {
                world.RemoveItem(item);
            }
        }

        private static List<(uint, bool)> Destroyed(World w) =>
            w.Mobiles.Values.Select(m => (m.Serial, m.IsDestroyed)).Concat(w.Items.Values.Select(i => (i.Serial, i.IsDestroyed))).OrderBy(x => x.Item1).ToList();

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.DestroyedHeld)]
        public void ClearWorld_Equals_The_Old_Removals(Start start)
        {
            World oldWorld = BuildCorpse(start), newWorld = BuildCorpse(start);
            newWorld.Writer.ThrowOnViolation = true;

            // an item worn by the mobile and a pending placement (its handler never placed it)
            foreach (World w in new[] { oldWorld, newWorld })
            {
                w.Writer.Equip(0x4000_7A00, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
                w.Writer.UpsertWorldItem(0x4000_7A01, 0x0EED, 0, 1, 1003, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            }

            newWorld.Writer.OutstandingPlacements.Should().Be(1);
            Time.Ticks = NOW + 1000;
            Pair(() => OldClearRemovals(oldWorld), () => newWorld.Writer.ClearWorld());
            Time.Ticks = NOW;

            Destroyed(newWorld).Should().Equal(Destroyed(oldWorld));
            Destroyed(newWorld).Should().OnlyContain(x => x.Item2, "every object is removed and destroyed");
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.OutstandingPlacements.Should().Be(0, "the placements of the removed objects are cancelled");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Writer.Owns(newWorld.Mobiles.Get(OTHER)).Should().BeFalse("the scope's ownership of the whole World ends with it");
        }

        [Fact]
        public void ClearWorld_Does_Not_Nest()
        {
            World world = BuildCorpse(Start.Walking);
            world.Writer.ThrowOnViolation = false;
            List<(uint, bool)> before = Destroyed(world);
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));
            world.Writer.ClearWorld();
            world.Ecs.EndFold(world.Mobiles.Get(OTHER));

            world.Writer.Violations.Should().Be(1);
            Destroyed(world).Should().Equal(before, "a rejected clear removes nothing");
            world.Mobiles.Get(OTHER).IsDestroyed.Should().BeFalse();

            world.Writer.ThrowOnViolation = true;
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));
            Action act = () => world.Writer.ClearWorld();
            act.Should().Throw<InvalidOperationException>();
            world.Ecs.EndFold(world.Mobiles.Get(OTHER));
        }

        // ---- group 6: ContainerGump.CheckItemControlPosition (the gump's bounds and the item's art need Client.Game: the
        // clamp is a parameter of the operation; ClampToBounds is compared with the old branches)

        internal static void OldClamp(World world, Item item, int boundX, int boundY, int boundWidth, int boundHeight)
        {
            if (item.X < boundX)
            {
                item.X = (ushort)boundX;
            }
            else if (item.X > boundWidth)
            {
                item.X = (ushort)boundWidth;
            }

            if (item.Y < boundY)
            {
                item.Y = (ushort)boundY;
            }
            else if (item.Y > boundHeight)
            {
                item.Y = (ushort)boundHeight;
            }

            world.Ecs.Sync(item);
        }

        internal static void NewClamp(World world, Item item, int boundX, int boundY, int boundWidth, int boundHeight)
        {
            ClassicUO.Game.UI.Gumps.ContainerGump.ClampToBounds(item.X, item.Y, boundX, boundY, boundWidth, boundHeight, out ushort x, out ushort y);
            world.Writer.ClampContainerPosition(item, x, y);
        }

        [Theory]
        [InlineData(10, 20, 44, 65, 140, 120)] // inside
        [InlineData(2, 3, 44, 65, 140, 120)] // both low
        [InlineData(300, 200, 44, 65, 140, 120)] // both high
        [InlineData(2, 200, 44, 65, 140, 120)] // x low, y high
        [InlineData(150, 70, 44, 65, 140, 120)] // x high only
        [InlineData(50, 90, 44, 65, -10, 120)] // a bound below the origin (the item's art wider than the gump)
        public void ClampContainerPosition_Equals_The_Old_Clamp(int ix, int iy, int bx, int by, int bw, int bh)
        {
            World oldWorld = BuildCorpse(Start.Standing), newWorld = BuildCorpse(Start.Standing);
            newWorld.Writer.ThrowOnViolation = true;
            Item a = oldWorld.Items.Get(CLOOT), b = newWorld.Items.Get(CLOOT);

            foreach ((World w, Item it) in new[] { (oldWorld, a), (newWorld, b) })
            {
                it.X = (ushort)ix;
                it.Y = (ushort)iy;
                w.Ecs.Sync(it);
            }

            long ma = oldWorld.Ecs.ItemMirrorWrites, mb = newWorld.Ecs.ItemMirrorWrites;
            Pair(() => OldClamp(oldWorld, a, bx, by, bw, bh), () => NewClamp(newWorld, b, bx, by, bw, bh));

            AssertSameWorld(oldWorld, newWorld);
            (b.X, b.Y).Should().Be((a.X, a.Y));
            (newWorld.Ecs.ItemMirrorWrites - mb).Should().Be(oldWorld.Ecs.ItemMirrorWrites - ma, "the same Sync");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void ClampContainerPosition_Writes_A_Stale_Item_Rejects_Another_Worlds_And_Does_Not_Nest()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildCorpse(Start.Standing), other = BuildCorpse(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Item foreign = other.Items.Get(CLOOT);
                (ushort, ushort) before = (foreign.X, foreign.Y);
                Action act = () => world.Writer.ClampContainerPosition(foreign, 77, 88);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                (foreign.X, foreign.Y).Should().Be(before);
                world.Writer.Violations.Should().Be(1);
            }

            World w = BuildCorpse(Start.Standing);
            w.Writer.ThrowOnViolation = true;
            Item stale = Item.Create(w, CLOOT);
            w.Writer.ClampContainerPosition(stale, 77, 88);
            (stale.X, stale.Y).Should().Be(((ushort)77, (ushort)88));
            w.Writer.Violations.Should().Be(0);

            w.Writer.ThrowOnViolation = false;
            Item held = w.Items.Get(CLOOT);
            (ushort, ushort) at = (held.X, held.Y);
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.ClampContainerPosition(held, 77, 88);
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            (held.X, held.Y).Should().Be(at);
            w.Writer.Violations.Should().Be(1);
        }
    }

    [Collection(nameof(FunnelCheckCollection))]
    public class PerFrameWritersFunnelCheckTests : IDisposable
    {
        public PerFrameWritersFunnelCheckTests()
        {
            WorldWriterMobileTests.PrefillHitsTexts();
            FunnelCheck.Reset();
            FunnelCheck.Enabled = true;
        }

        public void Dispose()
        {
            FunnelCheck.Enabled = false;
            FunnelCheck.Reset();
        }

        private static World BuildOne(out Mobile m)
        {
            Time.Ticks = 100_000;
            var world = new World();
            m = world.GetOrCreateMobile(0x0000_3300);
            m.Graphic = 0x190;
            m.SetInWorldTile(1000, 1000, 0);
            FunnelCheck.Reset();

            return world;
        }

        [Fact]
        public void The_Idle_Animation_And_Step_Sound_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            World world = BuildOne(out Mobile m);

            foreach (WorldWriterMobileTests.IdleData d in Enum.GetValues(typeof(WorldWriterMobileTests.IdleData)))
            {
                m.PlanIdleAnimation(WorldWriterMobileTests.IdleDataOf(d), out Mobile.IdlePlan plan);
                world.Writer.IdleAnimation(m, in plan);
            }

            world.Writer.StepSound(m, 1, 100_500).Should().BeTrue();

            (m.StepSoundOffset, m.AnimationFromServer).Should().Be((1, true));
            FunnelCheck.Total.Should().Be(0, "SetIdleAnimation (all its outcomes, SetAnimation's included) and ProcessFootstepsSound");
        }

        [Fact]
        public void The_Corpse_Frame_Advance_Makes_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            World world = BuildOne(out _);
            Item corpse = world.GetOrCreateItem(0x4000_7302);
            corpse.Graphic = 0x2006;
            corpse.SetInWorldTile(1002, 1000, 0);
            FunnelCheck.Reset();

            world.Writer.AdvanceCorpseFrame(corpse, 2, 100_080);
            world.Writer.AdvanceCorpseFrame(corpse, -1, 100_160);

            (corpse.AnimIndex, corpse.LastAnimationChangeTime).Should().Be(((byte)2, 100_160L));
            FunnelCheck.Total.Should().Be(0, "Item.ProcessAnimation's frame and due time");
        }

        [Fact]
        public void EndServerAnimation_Makes_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            World world = BuildOne(out Mobile m);
            world.Writer.AnimateMobile(m, 12, 1, 5, fromServer: true);
            FunnelCheck.Reset();

            world.Writer.EndServerAnimation(m);

            m.AnimationFromServer.Should().BeFalse();
            FunnelCheck.Total.Should().Be(0, "ProcessSteps' SetAnimation(0xFF) for a server animation a step ends");
        }

        [Fact]
        public void The_Players_Walk_Makes_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            Time.Ticks = 100_000;
            World world = WorldWriterMobileTests.BuildWalkingPlayer(out WorldWriterMobileTests.TestPlayer p);
            Time.Ticks = 105_000;
            FunnelCheck.Reset();

            p.PlanWalkStep(1001, 1000, 0, Direction.East, false, out Mobile.StepPlan plan);
            world.Writer.QueuePlayerStep(p, in plan).Should().BeTrue();
            p.PlanWalkStep(1002, 1000, 0, Direction.East, false, out plan);
            world.Writer.QueuePlayerStep(p, in plan).Should().BeTrue();
            world.Writer.SetMobileDirection(p, Direction.South);
            Time.Ticks = 100_000;

            (p.Steps.Count, p.Direction).Should().Be((2, Direction.South));
            FunnelCheck.Total.Should().Be(0, "PlayerMobile.Walk's queue writes (SetAnimation, LastStepTime, the step) and its coalesced turn");
        }

        [Fact]
        public void ClearWorld_Makes_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            World world = BuildOne(out Mobile m);
            Item bag = world.GetOrCreateItem(0x4000_7300);
            bag.Graphic = 0xE75;
            bag.SetInWorldTile(1001, 1000, 0);
            world.Writer.Equip(0x4000_7301, 0x1517, Layer.Shirt, m.Serial, 0, out _, out _);
            m.AddStep(new Mobile.Step { X = 1001, Y = 1000 });
            FunnelCheck.Reset();

            world.Writer.ClearWorld();

            (m.IsDestroyed, bag.IsDestroyed).Should().Be((true, true));
            FunnelCheck.Total.Should().Be(0, "World.Clear's removals (Destroy, the steps, the containers)");
        }

        [Fact]
        public void ClampContainerPosition_Makes_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            World world = BuildOne(out _);
            Item bag = world.GetOrCreateItem(0x4000_7300);
            bag.Graphic = 0xE75;
            bag.SetInWorldTile(1001, 1000, 0);
            Item gem = world.Writer.PlaceInContainer(0x4000_7301, 0x0F26, 1, 400, 2, 0, 0x4000_7300, out _);
            FunnelCheck.Reset();

            world.Writer.ClampContainerPosition(gem, 140, 65);

            (gem.X, gem.Y).Should().Be(((ushort)140, (ushort)65));
            FunnelCheck.Total.Should().Be(0, "ContainerGump.CheckItemControlPosition's clamp");
        }
    }
}
