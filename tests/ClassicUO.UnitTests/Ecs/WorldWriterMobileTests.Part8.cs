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
    }
}
