using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T5b (ecs-transition-plan.md): the idle animation and frame advance computed in the ECS (MobileAnimationAdvanceSystem over
    // AnimationTimer, MobileInputs, EquipmentSlots) against the legacy Mobile.Update of a mobile without steps: the base
    // revision's idle animation (OldSetIdle), the legacy selection input read from the Mobile, the frame count of the selected
    // action and the base revision's frame advance (OldAdvance), on one World; the system on another; identical starts and the
    // random state paired (Pair) frame by frame.
    public partial class WorldWriterMobileTests
    {
        /// <summary>The animation data both paths read, as the tests set it.</summary>
        internal struct FakeAdvanceSource : IAnimationAdvanceSource
        {
            public FakeIdleData Idle;
            public bool NoFrames;
            public bool[] Converted;

            public int MaxAnimationCount => Idle.Max;
            public AnimationGroupsType GetAnimType(ushort graphic) => Idle.Type;
            public AnimationFlags GetAnimFlags(ushort graphic) => Idle.Flags;
            public void ConvertBodyIfNeeded(ref ushort graphic, bool isParent) { }
            public bool AnimationExists(ushort graphic, byte group) => Idle.AnimationExists(graphic, group);
            public bool IsReplacedObjectAnimation(byte family, ushort group) => false;
            public byte GetReplacedObjectAnimation(ushort graphic, ushort index) => Idle.Replaced;

            public void GetAnimDirection(ref byte dir, ref bool mirror)
            {
                switch (dir)
                {
                    case 2: case 4: mirror = dir == 2; dir = 1; break;
                    case 1: case 5: mirror = dir == 1; dir = 2; break;
                    case 0: case 6: mirror = dir == 0; dir = 3; break;
                    case 3: dir = 0; break;
                    case 7: dir = 4; break;
                }
            }

            // a different count per action and direction, so a wrong selection or direction shows in the frame index
            public int FrameCount(ushort body, byte action, byte dir) => NoFrames ? 0 : 1 + (action * 3 + dir) % 7;
        }

        // the legacy selection input (Mobile.GetAnimGroupInput) without Client.Game: no gargoyle or flying rule before 7.0.0.0,
        // no hand items in these starts
        private static AnimGroupInput LegacyInput(Mobile m) => new AnimGroupInput
        {
            ServerGroup = (byte)FGroup.GetValue(m),
            FromServer = m.AnimationFromServer,
            WarMode = m.InWarMode,
            Walking = m.LastStepTime > Time.Ticks - Constants.WALKING_DELAY,
            Running = m.IsRunning,
            HasSteps = m.Steps.Count != 0,
            Mounted = false,
            GargoyleProp = m.Graphic == 0x029B,
            Flying = false,
            Dead = m.IsDead
        };

        // the base revision's Mobile.Update for a mobile without steps (idle, then AdvanceFrame with the frame count read from
        // the data, then ProcessAnimation's advance)
        internal static void OldUpdateNoSteps(Mobile m, FakeAdvanceSource src)
        {
            if (m.IsDestroyed)
            {
                return;
            }

            if ((long)FIdle.GetValue(m) < Time.Ticks)
            {
                OldSetIdle(m, src.Idle);
            }

            if (m.LastAnimationChangeTime >= Time.Ticks || !m.ExecuteAnimation || (m.LastStepTime > Time.Ticks - Constants.WALKING_DELAY && m.Steps.Count == 0))
            {
                return;
            }

            ushort id = m.GetGraphicForAnimation();
            AnimGroupInput input = LegacyInput(m);
            byte action = AnimationGroupRules.Select(in input, id, true, src);
            byte dir = (byte)((byte)m.Direction & 7);
            bool mirror = false;
            src.GetAnimDirection(ref dir, ref mirror);
            int frames = id < src.MaxAnimationCount && dir < 5 ? src.FrameCount(id, action, dir) : 0;
            OldAdvance(m, frames);
        }

        // the old World as World.UpdateMobilesByClock ran it: the slots rebuilt (as the drawn path would; neither path reads
        // them for these starts), Mobile.Update, then the clock push
        internal static void OldLoopStep(World world, Mobile m, FakeAdvanceSource src)
        {
            if (!m.IsDestroyed && world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e) && MobileAnimationSystem.TakeDirty(e))
            {
                AnimationState s = default;
                s.Flags = e.GetComponent<MobileInputs>().Flags;
                world.Ecs.Animation.Rebuild(e, in s, null, T5bTiles, T5bConv, false);
            }

            OldUpdateNoSteps(m, src);
            world.Ecs.PushClock(m);
        }

        private static readonly StaticTiles[] T5bTiles = new StaticTiles[0x10000];
        private static readonly Dictionary<ushort, Dictionary<ushort, EquipConvData>> T5bConv = new Dictionary<ushort, Dictionary<ushort, EquipConvData>>();

        // the system's step for one due mobile, as World.UpdateMobilesByClock runs it (the clock push after it)
        internal static bool NewUpdateNoSteps(World world, Mobile m, FakeAdvanceSource src)
        {
            if (m.IsDestroyed)
            {
                return false;
            }

            // the equipment slots without Client.Game (TryRun rebuilds a dirty one with the client's tiledata)
            if (world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e) && MobileAnimationSystem.TakeDirty(e))
            {
                AnimationState s = default;
                s.Flags = e.GetComponent<MobileInputs>().Flags;
                world.Ecs.Animation.Rebuild(e, in s, null, T5bTiles, T5bConv, false);
            }

            bool ran = new MobileAnimationAdvanceSystem().TryRun(world, m, in src, false);

            if (!ran)
            {
                return false;
            }

            world.Ecs.PushClock(m);

            return true;
        }

        public enum T5bStart { Standing, Repeating, Once, Held, Dead, WarMode, Walked, ServerFive, Gargoyle, ReverseServerOnce }

        public enum T5bData { People, PeopleNone, LowExtended, LowExtendedReplaced, NoFrames, UopAnimal, OutOfRange }

        private static FakeAdvanceSource T5bDataOf(T5bData d) => d switch
        {
            T5bData.People => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.PeopleBoth) },
            T5bData.PeopleNone => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.PeopleNone) },
            T5bData.LowExtended => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.MonsterLowExtended18) },
            T5bData.LowExtendedReplaced => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.MonsterLowExtendedReplaced) },
            T5bData.NoFrames => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.PeopleBoth), NoFrames = true },
            T5bData.UopAnimal => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.UopAnimal) },
            _ => new FakeAdvanceSource { Idle = IdleDataOf(IdleData.OutOfRange) }
        };

        private static World BuildT5b(T5bStart start, out uint serial)
        {
            World world = Build(Start.Standing);
            world.Ecs.AnimTimerEnabled = true;
            Mobile m = world.Mobiles.Get(MOB);
            serial = MOB;

            switch (start)
            {
                case T5bStart.Repeating: OldAnimate(m, AnimPacket.CharacterAnimation); break;
                case T5bStart.Once: OldAnimate(m, AnimPacket.NewCharacterAnimation); break;
                case T5bStart.Held: OldAnimate(m, AnimPacket.HeldFrame19); break;
                case T5bStart.Dead:
                    OldRekey(world, m);
                    world.CorpseManager.Add(0x4000_7900, DEAD, m.Direction, false);
                    OldAnimate(m, AnimPacket.Death);
                    serial = DEAD;

                    break;
                case T5bStart.WarMode:
                    m.Flags |= Flags.WarMode;
                    world.Ecs.Sync(m);

                    break;
                case T5bStart.Walked:
                    m.LastStepTime = NOW + 2000;
                    world.Ecs.PushInputs(m);

                    break;
                case T5bStart.ServerFive:
                    // a server action 5: LowGroupExtended bodies roll for it in the selection
                    m.SetAnimation(5, 0, 7, 3, true, false, true);

                    break;
                case T5bStart.ReverseServerOnce:
                    // reverse, from the server, count 1, no repeat: finishes with its frame at frames-1 after the reset
                    m.SetAnimation(9, 0, 5, 1, false, false, true);

                    break;
                case T5bStart.Gargoyle:
                    m.Graphic = 0x029B;
                    world.Ecs.Sync(m);
                    world.Ecs.PushInputs(m);

                    break;
            }

            world.Ecs.ResyncClocks();

            return world;
        }

        public static IEnumerable<object[]> T5bCases()
        {
            foreach (T5bStart s in Enum.GetValues(typeof(T5bStart)))
            {
                foreach (T5bData d in Enum.GetValues(typeof(T5bData)))
                {
                    yield return new object[] { s, d };
                }
            }
        }

        [Theory]
        [MemberData(nameof(T5bCases))]
        public void The_Ecs_Advance_Equals_The_Legacy_Update(T5bStart start, T5bData data)
        {
            World oldWorld = BuildT5b(start, out uint serial), newWorld = BuildT5b(start, out _);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(serial), b = newWorld.Mobiles.Get(serial);
            FakeAdvanceSource src = T5bDataOf(data);
            int advanced = 0, idles = 0;

            // 300 frames 37 ms apart (frames fall due at different ticks), then a jump past the idle timer, three times
            for (int round = 0; round < 3 && !a.IsDestroyed; round++)
            {
                for (int i = 1; i <= 300 && !a.IsDestroyed; i++)
                {
                    Time.Ticks += 37;

                    if (i == 150)
                    {
                        Time.Ticks += 61_000;
                        idles++;
                    }

                    long dueBefore = a.LastAnimationChangeTime;
                    Pair(() => OldLoopStep(oldWorld, a, src), () => NewUpdateNoSteps(newWorld, b, src).Should().BeTrue());

                    if (a.LastAnimationChangeTime != dueBefore)
                    {
                        advanced++;
                    }

                    b.IsDestroyed.Should().Be(a.IsDestroyed, "frame {0} of round {1}", i, round);
                    Describe(b).Should().Be(Describe(a), "frame {0} of round {1}", i, round);

                    if (!b.IsDestroyed)
                    {
                        newWorld.Ecs.Mirror.TryGetEntity(b.Serial, out EcsEntity e).Should().BeTrue();
                        MobileAnimationAdvanceSystem.Same(e.GetComponent<AnimationTimer>(), b.FullAnimationTimer).Should().BeTrue("the push copies the Mobile into the component");
                    }
                }
            }

            AssertSameWorld(oldWorld, newWorld, a.IsDestroyed ? MOB : serial);
            oldWorld.CorpseManager.Exists(0x4000_7900, 0).Should().Be(newWorld.CorpseManager.Exists(0x4000_7900, 0));

            if (start == T5bStart.Dead && data != T5bData.OutOfRange)
            {
                a.IsDestroyed.Should().BeTrue("a dying mobile's animation ends and it is removed");
            }

            if (start != T5bStart.Held && start != T5bStart.Walked && start != T5bStart.Dead)
            {
                advanced.Should().BeGreaterThan(0);
            }
        }

        private static string Describe(Mobile m) => MobileAnimationAdvanceSystem.Describe(m.FullAnimationTimer) + $" destroyed {m.IsDestroyed}";

        [Fact]
        public void The_Ecs_Advance_Draws_The_Legacy_Random_Sequence_Over_Several_Mobiles_In_Table_Order()
        {
            // three mobiles share the generator: each frame both paths update them in table order, and every draw (idle delays,
            // idle pairs, the LowGroupExtended roll of server action 5) must come in the same sequence
            World oldWorld = BuildT5b(T5bStart.ServerFive, out _), newWorld = BuildT5b(T5bStart.ServerFive, out _);
            FakeAdvanceSource src = T5bDataOf(T5bData.LowExtended);
            uint rolls = AnimationGroupRules.RandomRolls;

            for (int i = 1; i <= 400; i++)
            {
                Time.Ticks += 53;

                if (i % 100 == 0)
                {
                    Time.Ticks += 45_000;
                }

                Pair(
                    () =>
                    {
                        foreach (Mobile m in oldWorld.Mobiles.Values)
                        {
                            OldLoopStep(oldWorld, m, src);
                        }
                    },
                    () =>
                    {
                        foreach (Mobile m in newWorld.Mobiles.Values)
                        {
                            NewUpdateNoSteps(newWorld, m, src);
                        }
                    });

                foreach (Mobile m in oldWorld.Mobiles.Values)
                {
                    Describe(newWorld.Mobiles.Get(m.Serial)).Should().Be(Describe(m), "frame {0}", i);
                }
            }

            AnimationGroupRules.RandomRolls.Should().BeGreaterThan(rolls, "the selection rolled");
        }

        [Fact]
        public void A_Mobile_With_Steps_Or_A_Handles_Opening_Is_Declined_And_Unchanged()
        {
            World world = BuildT5b(T5bStart.Standing, out _);
            Mobile m = world.Mobiles.Get(MOB);
            FakeAdvanceSource src = T5bDataOf(T5bData.People);
            m.AddStep(new Mobile.Step { X = 1001, Y = 1000, Direction = 2 });
            world.Ecs.PushInputs(m);
            string before = Describe(m);
            Time.Ticks += 70_000;

            new MobileAnimationAdvanceSystem().TryRun(world, m, in src, false).Should().BeFalse();
            Describe(m).Should().Be(before);

            m.ClearStepQueue();
            m.ObjectHandlesStatus = ObjectHandlesStatus.OPEN;
            world.Ecs.PushClock(m);
            new MobileAnimationAdvanceSystem().TryRun(world, m, in src, false).Should().BeFalse();
            Describe(m).Should().Be(before);
        }

        // ecs-hygiene: TryRun reads the eligibility from one record row; a mobile whose record lacks a component is declined
        [Fact]
        public void A_Mobile_Without_The_Timer_Component_Is_Declined_And_Unchanged()
        {
            World world = BuildT5b(T5bStart.Standing, out _);
            Mobile m = world.Mobiles.Get(MOB);
            FakeAdvanceSource src = T5bDataOf(T5bData.People);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e).Should().BeTrue();
            e.RemoveComponent<AnimationTimer>();
            string before = Describe(m);
            long declined = MobileAnimationAdvanceSystem.Declined;
            Time.Ticks += 70_000;

            new MobileAnimationAdvanceSystem().TryRun(world, m, in src, false).Should().BeFalse();
            Describe(m).Should().Be(before);
            (MobileAnimationAdvanceSystem.Declined - declined).Should().BeGreaterThan(0);
        }

        // the row overload of TakeDirty: the tag read from the record, removed only when set
        [Fact]
        public void TakeDirty_From_A_Row_Removes_Only_A_Set_Tag()
        {
            var store = new Friflo.Engine.ECS.EntityStore();
            EcsEntity e = store.CreateEntity();
            e.AddComponent(new MobileInputs());

            MobileAnimationSystem.TakeDirty(e, EcsRead.Row(e)).Should().BeFalse();
            e.Tags.Has<EquipmentDirty>().Should().BeFalse();

            e.AddTag<EquipmentDirty>();
            MobileAnimationSystem.TakeDirty(e, EcsRead.Row(e)).Should().BeTrue();
            e.Tags.Has<EquipmentDirty>().Should().BeFalse();
            MobileAnimationSystem.TakeDirty(e, EcsRead.Row(e)).Should().BeFalse();
        }

        [Fact]
        public void The_Selection_Input_Comes_From_The_Pushed_Inputs_And_The_Slots()
        {
            // mounted and the hands come from the slots; war mode, running, flying, gargoyle, dead, steps from the inputs;
            // the server action from the timer (the mounted, flying and gargoyle cases the two-world test cannot build)
            var t = new AnimationTimer { Group = 12, FromServer = true };
            var inp = new MobileInputs
            {
                Flags = AnimStateFlags.WarMode | AnimStateFlags.Running | AnimStateFlags.Flying | AnimStateFlags.GargoyleProp |
                    AnimStateFlags.Dead | AnimStateFlags.HasSteps | AnimStateFlags.StepRun,
                LastStepTime = 1000,
                WalkingDelay = 150
            };
            var eq = new EquipmentSlots { Mounted = true, Hand1 = new HandItem { Present = true, AnimId = 0x263 }, Hand2 = new HandItem { Present = true, IsLight = true } };

            AnimGroupInput g = AnimationAdvanceRules.InputOf(in t, in inp, in eq, 1100);
            g.Should().BeEquivalentTo(new AnimGroupInput
            {
                ServerGroup = 12, FromServer = true, WarMode = true, Walking = true, Running = true, HasSteps = true, StepRun = true,
                Mounted = true, GargoyleProp = true, Flying = true, Dead = true, Hand1 = eq.Hand1, Hand2 = eq.Hand2
            });

            AnimationAdvanceRules.InputOf(in t, in inp, default, 1200).Should().BeEquivalentTo(new AnimGroupInput
            {
                ServerGroup = 12, FromServer = true, WarMode = true, Walking = false, Running = true, HasSteps = true, StepRun = true,
                GargoyleProp = true, Flying = true, Dead = true
            });
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void A_Mounted_Or_War_Mode_Mobile_Gets_No_Idle_Reset(bool mounted, bool warMode)
        {
            FakeAdvanceSource src = T5bDataOf(T5bData.People);
            var t = new AnimationTimer { Group = 4, Frame = 3, ExecuteAnimation = true };
            var inp = new MobileInputs { Body = 0x190, Flags = warMode ? AnimStateFlags.WarMode : 0 };
            RandomHelper.Restore(Seed);
            AnimationAdvanceRules.PlanIdle(in t, in inp, mounted, in src, out Mobile.IdlePlan plan);
            RandomHelper.State after = RandomHelper.Save();
            RandomHelper.Restore(Seed);
            long expected = Mobile.NextIdleDue();

            plan.Resets.Should().BeFalse();
            plan.IdleDue.Should().Be(expected);
            RandomHelper.Save().Should().Be(after, "one draw, the idle delay");

            AnimationAdvanceRules.ApplyIdle(ref t, in plan);
            t.Frame.Should().Be(3);
            t.Group.Should().Be(4);
            t.IdleDue.Should().Be(expected);
        }

        [Fact]
        public void A_Flying_Gargoyle_Draws_Its_Flight_Idle()
        {
            var src = new FakeAdvanceSource { Idle = new FakeIdleData { Max = 0x800, Type = AnimationGroupsType.Human, Flags = AnimationFlags.UseUopAnimation } };
            var t = new AnimationTimer { ExecuteAnimation = true };
            var inp = new MobileInputs { Body = 0x29B, Flags = AnimStateFlags.GargoyleProp | AnimStateFlags.Flying };
            RandomHelper.Restore(Seed);
            AnimationAdvanceRules.PlanIdle(in t, in inp, false, in src, out Mobile.IdlePlan plan);
            RandomHelper.Restore(Seed);
            Mobile.NextIdleDue();
            byte expected = (byte)(RandomHelper.GetValue(0, 2) != 0 ? 66 : 67);

            plan.Resets.Should().BeTrue();
            plan.Group.Should().Be(Mobile.IdleGroup.Set);
            plan.Value.Should().Be(expected);

            inp.Flags = AnimStateFlags.GargoyleProp;
            AnimationAdvanceRules.PlanIdle(in t, in inp, false, in src, out plan);
            plan.Value.Should().BeOneOf((byte)PeopleAnimationGroup.Fidget1, (byte)PeopleAnimationGroup.Fidget2, (byte)PeopleAnimationGroup.Fidget3, (byte)0);
        }
    }
}
