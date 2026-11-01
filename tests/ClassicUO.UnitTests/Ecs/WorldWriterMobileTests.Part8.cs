using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
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
    }
}
