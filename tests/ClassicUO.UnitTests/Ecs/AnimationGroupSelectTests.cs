using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // AnimationGroupRules.Select over a stub animation table (IAnimationSource) in place of the client's Animations.
    // the random rolls share AnimationGroupRules' static roll tape with ProbeIsolationTests: one collection, not in parallel
    [Collection("AnimationRolls")]
    public class AnimationGroupSelectTests
    {
        private const ushort HUMAN = 400, EQUIP = 401, ANIMAL = 100, MONSTER = 300, SEA = 200, ANIMAL_UOP = 101, HUMAN_UOP = 402, EQUIP_UOP = 403,
            MONSTER_8 = 301, MONSTER_UOP = 302, ALIAS = 500, LOW_EXT = 303, LOW_GROUP = 304, PEOPLE_GROUP = 305;

        private sealed class Table
        {
            public int Max = 1000;
            public readonly Dictionary<ushort, (AnimationGroupsType Type, AnimationFlags Flags)> Bodies = new Dictionary<ushort, (AnimationGroupsType, AnimationFlags)>();
            public readonly HashSet<(ushort, byte)> Exists = new HashSet<(ushort, byte)>();
            public readonly Dictionary<ushort, ushort> Convert = new Dictionary<ushort, ushort>();
            public readonly HashSet<(byte, ushort)> Replaced = new HashSet<(byte, ushort)>();
        }

        private readonly struct Stub : IAnimationSource
        {
            private readonly Table _t;

            public Stub(Table t) => _t = t;

            public int MaxAnimationCount => _t.Max;

            public AnimationGroupsType GetAnimType(ushort graphic) => _t.Bodies.TryGetValue(graphic, out var b) ? b.Type : AnimationGroupsType.Monster;

            public AnimationFlags GetAnimFlags(ushort graphic) => _t.Bodies.TryGetValue(graphic, out var b) ? b.Flags : 0;

            public void ConvertBodyIfNeeded(ref ushort graphic, bool isParent)
            {
                if (_t.Convert.TryGetValue(graphic, out ushort to))
                {
                    graphic = to;
                }
            }

            public bool AnimationExists(ushort graphic, byte group) => _t.Exists.Contains((graphic, group));

            public bool IsReplacedObjectAnimation(byte family, ushort group) => _t.Replaced.Contains((family, group));
        }

        private readonly Table _table = new Table();

        public AnimationGroupSelectTests()
        {
            Body(HUMAN, AnimationGroupsType.Human);
            Body(EQUIP, AnimationGroupsType.Equipment);
            Body(HUMAN_UOP, AnimationGroupsType.Human, AnimationFlags.UseUopAnimation);
            Body(EQUIP_UOP, AnimationGroupsType.Equipment, AnimationFlags.UseUopAnimation);
            Body(ANIMAL, AnimationGroupsType.Animal);
            Body(ANIMAL_UOP, AnimationGroupsType.Animal, AnimationFlags.UseUopAnimation);
            Body(SEA, AnimationGroupsType.SeaMonster);
            Body(MONSTER, AnimationGroupsType.Monster);
            Body(MONSTER_8, AnimationGroupsType.Monster, AnimationFlags.IdleAt8Frame | AnimationFlags.CanFlying);
            Body(MONSTER_UOP, AnimationGroupsType.Monster, AnimationFlags.UseUopAnimation);
            Body(LOW_EXT, AnimationGroupsType.Animal, AnimationFlags.CalculateOffsetLowGroupExtended);
            Body(LOW_GROUP, AnimationGroupsType.Monster, AnimationFlags.CalculateOffsetByLowGroup);
            Body(PEOPLE_GROUP, AnimationGroupsType.Monster, AnimationFlags.CalculateOffsetByPeopleGroup);
        }

        private void Body(ushort graphic, AnimationGroupsType type, AnimationFlags flags = 0) => _table.Bodies[graphic] = (type, flags);

        private byte Select(AnimGroupInput m, ushort graphic, bool isParent = true)
        {
            if (m.ServerGroup == 0)
            {
                m.ServerGroup = 0xFF;
            }

            return AnimationGroupRules.Select(in m, graphic, isParent, new Stub(_table));
        }

        private static AnimGroupInput Input(bool war = false, bool walking = false, bool running = false) =>
            new AnimGroupInput { ServerGroup = 0xFF, WarMode = war, Walking = walking, Running = running };

        // ----- humans -----

        [Fact]
        public void A_Body_Past_The_Table_Gets_Action_Zero()
        {
            Select(Input(), 1000).Should().Be(0);
            Select(Input(), 5000).Should().Be(0);
        }

        [Theory]
        [InlineData(false, false, false, 4)]  // idle in peace
        [InlineData(true, false, false, 7)]   // idle in war mode, empty hands
        [InlineData(false, true, false, 0)]   // walk
        [InlineData(false, true, true, 2)]    // run
        [InlineData(true, true, false, 15)]   // walk in war mode
        [InlineData(true, true, true, 2)]     // run in war mode
        public void Human_Idle_Walk_And_Run(bool war, bool walking, bool running, int expected)
        {
            Select(Input(war, walking, running), HUMAN).Should().Be((byte)expected);
        }

        [Fact]
        public void A_Dead_Human_Idles_And_Walks_As_In_Peace_Even_In_War_Mode()
        {
            AnimGroupInput idle = Input(war: true);
            idle.Dead = true;
            Select(idle, HUMAN).Should().Be(4);

            AnimGroupInput walk = Input(war: true, walking: true);
            walk.Dead = true;
            Select(walk, HUMAN).Should().Be(0);
        }

        [Fact]
        public void Steps_Make_A_Human_Walk_Or_Run_Whatever_The_Walking_Flags_Say()
        {
            AnimGroupInput stepWalk = Input();
            stepWalk.HasSteps = true;
            Select(stepWalk, HUMAN).Should().Be(0);

            AnimGroupInput stepRun = Input();
            stepRun.HasSteps = true;
            stepRun.StepRun = true;
            Select(stepRun, HUMAN).Should().Be(2);

            AnimGroupInput runningButStepWalking = Input(walking: true, running: true);
            runningButStepWalking.HasSteps = true;
            Select(runningButStepWalking, HUMAN).Should().Be(0, "with steps the front step decides");
        }

        [Fact]
        public void A_Two_Handed_Item_Changes_The_Walk_And_Run_Actions()
        {
            AnimGroupInput walk = Input(walking: true);
            walk.Hand2 = new HandItem { Present = true, AnimId = 0x0241 };
            Select(walk, HUMAN).Should().Be(1);

            AnimGroupInput run = Input(walking: true, running: true);
            run.Hand2 = new HandItem { Present = true, AnimId = 0x0241 };
            Select(run, HUMAN).Should().Be(3);

            AnimGroupInput outsideRange = Input(walking: true);
            outsideRange.Hand2 = new HandItem { Present = true, AnimId = 0x0100 };
            Select(outsideRange, HUMAN).Should().Be(1);
        }

        [Fact]
        public void Mounted_Humans_Idle_Walk_And_Run_On_The_Mount_Actions()
        {
            AnimGroupInput idle = Input();
            idle.Mounted = true;
            Select(idle, HUMAN).Should().Be(25);

            AnimGroupInput walk = Input(walking: true);
            walk.Mounted = true;
            Select(walk, HUMAN).Should().Be(23);

            AnimGroupInput run = Input(walking: true, running: true);
            run.Mounted = true;
            Select(run, HUMAN).Should().Be(24);
        }

        [Fact]
        public void A_Light_In_The_Off_Hand_That_Is_The_Body_Itself_Changes_The_Idle()
        {
            var light = new HandItem { Present = true, IsLight = true, AnimId = HUMAN };

            AnimGroupInput mounted = Input();
            mounted.Mounted = true;
            mounted.Hand2 = light;
            Select(mounted, HUMAN).Should().Be(28);

            AnimGroupInput peace = Input();
            peace.Hand2 = light;
            Select(peace, HUMAN).Should().Be(0);

            AnimGroupInput war = Input(war: true);
            war.Hand2 = light;
            Select(war, HUMAN).Should().Be(2);

            AnimGroupInput other = Input();
            other.Hand2 = new HandItem { Present = true, IsLight = true, AnimId = HUMAN + 1 };
            Select(other, HUMAN).Should().Be(4, "a light for another body doesn't count");
        }

        [Theory]
        [InlineData(0x0263, 8)] // the base hand weapons draw action 8
        [InlineData(0x0269, 8)]
        [InlineData(0x026A, 7)] // between the base ids
        [InlineData(0x0100, 7)]
        public void An_Only_Off_Hand_Item_In_War_Mode_Picks_Seven_Or_Eight(int hand2Anim, int expected)
        {
            AnimGroupInput m = Input(war: true);
            m.Hand2 = new HandItem { Present = true, AnimId = (ushort)hand2Anim };
            Select(m, HUMAN).Should().Be((byte)expected);
        }

        [Fact]
        public void A_Main_Hand_Item_In_War_Mode_Idles_With_Action_Seven()
        {
            AnimGroupInput m = Input(war: true);
            m.Hand1 = new HandItem { Present = true, AnimId = 0x0263 };
            Select(m, HUMAN).Should().Be(7);
        }

        [Fact]
        public void A_Flying_Gargoyle_Uses_The_Flight_Actions()
        {
            AnimGroupInput idle = Input();
            idle.GargoyleProp = true;
            idle.Flying = true;
            Select(idle, HUMAN).Should().Be(64);

            idle.WarMode = true;
            Select(idle, HUMAN).Should().Be(65);

            AnimGroupInput empty = Input(war: true);
            empty.GargoyleProp = true;
            empty.Flying = true;
            Select(empty, HUMAN_UOP).Should().Be(65);
        }

        [Fact]
        public void A_Uop_Human_Walks_And_Runs_With_Uop_Actions_When_They_Exist()
        {
            Select(Input(walking: true), HUMAN_UOP).Should().Be(0);

            _table.Exists.Add((HUMAN_UOP, 24));
            Select(Input(walking: true, running: true), HUMAN_UOP).Should().Be(24);

            AnimGroupInput flyWalk = Input(walking: true);
            flyWalk.GargoyleProp = true;
            flyWalk.Flying = true;
            Select(flyWalk, HUMAN_UOP).Should().Be(62);

            AnimGroupInput flyRun = Input(walking: true, running: true);
            flyRun.GargoyleProp = true;
            flyRun.Flying = true;
            Select(flyRun, HUMAN_UOP).Should().Be(63);
        }

        [Fact]
        public void A_Uop_Human_Run_Without_Action_24_Falls_Back_To_Two()
        {
            Select(Input(walking: true, running: true), HUMAN_UOP).Should().Be(2);
        }

        [Fact]
        public void Uop_Equipment_Falls_Back_When_Its_Own_Actions_Are_Missing()
        {
            Select(Input(walking: true, running: true), EQUIP_UOP).Should().Be(3, "no action 24 and no action 2");
            Select(Input(walking: true), EQUIP_UOP).Should().Be(1, "no action 0");

            _table.Exists.Add((EQUIP_UOP, 0));
            _table.Exists.Add((EQUIP_UOP, 2));
            Select(Input(walking: true, running: true), EQUIP_UOP).Should().Be(2);
            Select(Input(walking: true), EQUIP_UOP).Should().Be(0);
        }

        [Fact]
        public void Uop_Equipment_Idles_With_Action_37_Only_When_It_Exists()
        {
            Select(Input(), EQUIP_UOP).Should().Be(4);

            _table.Exists.Add((EQUIP_UOP, 37));
            Select(Input(), EQUIP_UOP).Should().Be(37);
            Select(Input(), HUMAN_UOP).Should().Be(4, "only the Equipment type asks");
        }

        [Fact]
        public void Plain_Equipment_Is_Selected_Like_A_Human()
        {
            Select(Input(), EQUIP).Should().Be(4);
            Select(Input(walking: true), EQUIP).Should().Be(0);
        }

        // ----- animals, sea monsters, monsters -----

        [Fact]
        public void Animals_Idle_Walk_And_Run()
        {
            Select(Input(), ANIMAL).Should().Be(2);
            Select(Input(walking: true), ANIMAL).Should().Be(0);
            Select(Input(walking: true, running: true), ANIMAL).Should().Be(2, "no action 1 to run with");

            _table.Exists.Add((ANIMAL, 1));
            Select(Input(walking: true, running: true), ANIMAL).Should().Be(1);
        }

        [Fact]
        public void Uop_Animals_Use_The_High_Actions()
        {
            Select(Input(), ANIMAL_UOP).Should().Be(25);
            Select(Input(walking: true, running: true), ANIMAL_UOP).Should().Be(24);
            Select(Input(walking: true), ANIMAL_UOP).Should().Be(22);

            _table.Exists.Add((ANIMAL_UOP, 1));
            Select(Input(war: true), ANIMAL_UOP).Should().Be(1, "war mode with action 1 present");
            Select(Input(), ANIMAL_UOP).Should().Be(25);

            _table.Exists.Add((ANIMAL_UOP, 0));
            Select(Input(war: true, walking: true), ANIMAL_UOP).Should().Be(0, "war mode with action 0 present");
        }

        [Fact]
        public void Sea_Monsters_Idle_Walk_And_Run()
        {
            Select(Input(), SEA).Should().Be(2);
            Select(Input(walking: true), SEA).Should().Be(0);
            Select(Input(walking: true, running: true), SEA).Should().Be(1);
        }

        [Fact]
        public void Monsters_Idle_Walk_And_Run()
        {
            Select(Input(), MONSTER).Should().Be(1);
            Select(Input(walking: true), MONSTER).Should().Be(0);
            Select(Input(walking: true, running: true), MONSTER).Should().Be(0);
        }

        [Fact]
        public void A_Flying_Monster_Uses_Action_19()
        {
            AnimGroupInput m = Input();
            m.Flying = true;
            Select(m, MONSTER).Should().Be(19);
        }

        [Fact]
        public void Monster_Flags_Pick_Eight_Frame_Idles_Flight_Runs_And_Uop_Actions()
        {
            Select(Input(), MONSTER_8).Should().Be(1, "no action 8");
            _table.Exists.Add((MONSTER_8, 8));
            Select(Input(), MONSTER_8).Should().Be(8);

            Select(Input(walking: true, running: true), MONSTER_8).Should().Be(0);
            _table.Exists.Add((MONSTER_8, 19));
            Select(Input(walking: true, running: true), MONSTER_8).Should().Be(19);

            Select(Input(), MONSTER_UOP).Should().Be(25);
            Select(Input(war: true), MONSTER_UOP).Should().Be(1);
            Select(Input(walking: true), MONSTER_UOP).Should().Be(22);
            Select(Input(walking: true, war: true), MONSTER_UOP).Should().Be(0);
            Select(Input(walking: true, running: true), MONSTER_UOP).Should().Be(24);
        }

        [Fact]
        public void The_Low_And_People_Group_Offsets_Replace_The_Monster_Defaults()
        {
            Select(Input(), LOW_GROUP).Should().Be(2);
            Select(Input(walking: true), LOW_GROUP).Should().Be(0);
            Select(Input(walking: true, running: true), LOW_GROUP).Should().Be(1);

            Select(Input(), PEOPLE_GROUP).Should().Be(0);
            Select(Input(walking: true, running: true), PEOPLE_GROUP).Should().Be(0);
        }

        [Fact]
        public void An_Extended_Low_Group_Animal_Calculates_Like_A_Monster()
        {
            Select(Input(), LOW_EXT).Should().Be(1);
            Select(Input(walking: true), LOW_EXT).Should().Be(0);
            Select(Input(walking: true, running: true), LOW_EXT).Should().Be(0);
        }

        // ----- conversion and server actions -----

        [Fact]
        public void The_Type_And_Flags_Come_From_The_Converted_Body()
        {
            _table.Convert[ALIAS] = ANIMAL;
            Body(ALIAS, AnimationGroupsType.Human);

            Select(Input(), ALIAS).Should().Be(2, "the animal the alias converts to, not the human it is listed as");
        }

        [Fact]
        public void The_Parent_Flag_Reaches_The_Conversion()
        {
            bool? parent = null;
            var table = new Table();
            table.Bodies[HUMAN] = (AnimationGroupsType.Human, 0);
            AnimGroupInput m = Input();

            AnimationGroupRules.Select(in m, HUMAN, false, new RecordingStub(table, p => parent = p));

            parent.Should().BeFalse();

            AnimationGroupRules.Select(in m, HUMAN, true, new RecordingStub(table, p => parent = p));

            parent.Should().BeTrue();
        }

        private readonly struct RecordingStub : IAnimationSource
        {
            private readonly Table _t;
            private readonly System.Action<bool> _onConvert;

            public RecordingStub(Table t, System.Action<bool> onConvert)
            {
                _t = t;
                _onConvert = onConvert;
            }

            public int MaxAnimationCount => _t.Max;

            public AnimationGroupsType GetAnimType(ushort graphic) => _t.Bodies[graphic].Type;

            public AnimationFlags GetAnimFlags(ushort graphic) => _t.Bodies[graphic].Flags;

            public void ConvertBodyIfNeeded(ref ushort graphic, bool isParent) => _onConvert(isParent);

            public bool AnimationExists(ushort graphic, byte group) => false;

            public bool IsReplacedObjectAnimation(byte family, ushort group) => false;
        }

        [Fact]
        public void A_Server_Action_Of_0xFF_Falls_Through_To_The_Calculated_One()
        {
            AnimGroupInput m = Input();
            m.FromServer = true;
            m.ServerGroup = 0xFF;

            Select(m, HUMAN).Should().Be(4);
        }

        [Fact]
        public void A_Server_Action_Maps_Through_The_Body_Types_Table()
        {
            uint rolls = AnimationGroupRules.RandomRolls;

            AnimGroupInput human = Input();
            human.FromServer = true;
            human.ServerGroup = 6;
            byte humanAction = Select(human, HUMAN);

            AnimGroupInput animal = Input();
            animal.FromServer = true;
            animal.ServerGroup = 6;
            byte animalAction = Select(animal, ANIMAL);

            // pinned from the rule as it stands (moved verbatim from Mobile.GetGroupForAnimation)
            humanAction.Should().Be(Select(human, HUMAN), "a server action is deterministic for a human");
            animalAction.Should().Be(Select(animal, ANIMAL));
            AnimationGroupRules.RandomRolls.Should().Be(rolls, "no random roll for these");
        }

        [Fact]
        public void A_Random_Server_Action_Counts_Its_Roll()
        {
            AnimGroupInput m = Input();
            m.FromServer = true;
            m.ServerGroup = 5;
            uint rolls = AnimationGroupRules.RandomRolls;

            byte result = Select(m, LOW_EXT);

            AnimationGroupRules.RandomRolls.Should().Be(rolls + 1);
            result.Should().BeOneOf((byte)5, (byte)6);
        }

        [Fact]
        public void The_Eight_Frame_Idle_Flag_Makes_The_Random_Server_Action_Fixed()
        {
            Body(LOW_EXT, AnimationGroupsType.Animal, AnimationFlags.CalculateOffsetLowGroupExtended | AnimationFlags.IdleAt8Frame);
            AnimGroupInput m = Input();
            m.FromServer = true;
            m.ServerGroup = 5;
            uint rolls = AnimationGroupRules.RandomRolls;

            Select(m, LOW_EXT).Should().Be(4);
            AnimationGroupRules.RandomRolls.Should().Be(rolls);
        }
    }
}
