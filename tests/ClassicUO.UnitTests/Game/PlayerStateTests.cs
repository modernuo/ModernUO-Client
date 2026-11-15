using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // ProfileManager.CurrentProfile is process-wide: the skills handler needs one, so these tests run alone.
    [CollectionDefinition("PlayerStateGlobals", DisableParallelization = true)]
    public class PlayerStateGlobalsCollection { }

    /// <summary>
    /// The player's session data lives in <see cref="World.PlayerState"/> (ecs-transition-plan Q4), not on the
    /// PlayerMobile. Its lifetime must stay the one the PlayerMobile fields had: fresh with each <c>CreatePlayer</c>
    /// (0x1B, relog), dropped by <c>World.Clear</c> (logout), kept through everything that keeps the player object
    /// (death and resurrection, a map change, status and skill updates).
    /// </summary>
    [Collection("PlayerStateGlobals")]
    public sealed class PlayerStateTests : IDisposable
    {
        private const uint PLAYER = 0x0000_1234;
        private const string NAME = "Tester";

        private static readonly IReadOnlyList<SkillEntry> SkillList = new List<SkillEntry>
        {
            new SkillEntry(0, "Alchemy", true),
            new SkillEntry(1, "Anatomy", true),
            new SkillEntry(2, "Animal Lore", true)
        };

        private static readonly FieldInfo BuffTableField = typeof(BuffTable).GetField("_table", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly Profile _savedProfile = ProfileManager.CurrentProfile;
        private readonly object _savedBuffTable = BuffTableField.GetValue(null);

        public void Dispose()
        {
            SetProfile(_savedProfile);
            BuffTableField.SetValue(null, _savedBuffTable);
        }

        private static void SetProfile(Profile profile) =>
            typeof(ProfileManager).GetProperty(nameof(ProfileManager.CurrentProfile))!.SetValue(null, profile);

        // inGame: a map too (World.InGame, which the skills handler needs); without one, removing the player does not reach
        // Client.Game (SendCloseStatus), which the tests do not have
        private static World Login(bool inGame = false)
        {
            var world = new World();
            world.CreatePlayer(PLAYER, SkillList);

            if (inGame)
            {
                typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            }

            // the status packet's name, so 0x11 does not retitle the window (Client.Game is not there in tests)
            world.Writer.SetStatus(world.Player, NAME, 50, 60);

            return world;
        }

        private static void Fill(PlayerState s)
        {
            s.Strength = 100;
            s.Gold = 1234;
            s.Followers = 2;
            s.FireResistance = 40;
            s.DeathScreenTimer = 99999;
            s.StrLock = Lock.Down;
            s.Skills[1].ValueFixed = 500;
            s.PrimaryAbility = Ability.ArmorIgnore;
            s.AddBuff(BuffIconType.Bless, 0x754C, 30, "bless");
            s.AutoOpenedCorpses.Add(0x4000_0001);
            s.ManualOpenedCorpses.Add(0x4000_0002);
        }

        private static void AssertFilled(PlayerState s)
        {
            s.Strength.Should().Be(100);
            s.Gold.Should().Be(1234);
            s.Followers.Should().Be(2);
            s.FireResistance.Should().Be(40);
            s.DeathScreenTimer.Should().Be(99999);
            s.StrLock.Should().Be(Lock.Down);
            s.Skills[1].ValueFixed.Should().Be(500);
            s.PrimaryAbility.Should().Be(Ability.ArmorIgnore);
            s.BuffIcons.Should().ContainKey(BuffIconType.Bless);
            s.AutoOpenedCorpses.Should().Equal(0x4000_0001u);
            s.ManualOpenedCorpses.Should().Equal(0x4000_0002u);
        }

        private static void AssertFresh(PlayerState s, int skills)
        {
            s.Skills.Should().HaveCount(skills);
            s.Strength.Should().Be(0);
            s.Gold.Should().Be(0);
            s.Followers.Should().Be(0);
            s.FireResistance.Should().Be(0);
            s.DeathScreenTimer.Should().Be(0);
            s.StrLock.Should().Be(Lock.Up);
            s.Abilities.Should().Equal(Ability.Invalid, Ability.Invalid);
            s.BuffIcons.Should().BeEmpty();
            s.AutoOpenedCorpses.Should().BeEmpty();
            s.ManualOpenedCorpses.Should().BeEmpty();

            foreach (Skill skill in s.Skills)
            {
                skill.ValueFixed.Should().Be(0);
            }
        }

        // ---- lifetime

        [Fact]
        public void CreatePlayer_Installs_A_Fresh_Store_With_The_Skill_List()
        {
            World world = Login();

            AssertFresh(world.PlayerState, SkillList.Count);
            world.PlayerState.Skills[2].Name.Should().Be("Animal Lore");
            world.PlayerState.Skills[2].Index.Should().Be(2);
        }

        [Fact]
        public void Relog_CreatePlayer_Again_Starts_From_A_Fresh_Store()
        {
            // before: a second 0x1B ran Clear and made a new PlayerMobile, whose fields started at their defaults
            World world = Login();
            PlayerState first = world.PlayerState;
            Fill(first);

            world.CreatePlayer(PLAYER, SkillList);

            world.PlayerState.Should().NotBeSameAs(first);
            AssertFresh(world.PlayerState, SkillList.Count);
            AssertFilled(first); // the old store is left alone, not cleared under a holder
        }

        [Fact]
        public void Logout_Clear_Drops_The_Store()
        {
            // before: Clear destroyed the PlayerMobile (DeathScreenTimer = 0) and nulled World.Player
            World world = Login();
            Fill(world.PlayerState);

            world.Clear();

            world.Player.Should().BeNull();
            AssertFresh(world.PlayerState, 0);
        }

        // perf-14: CanBeDrawn's race input, read once per batch (chunk load, multi build, map gumps); no player (logout) is false
        [Fact]
        public void PlayerIsGargoyle_Follows_The_Race_And_Is_False_Without_A_Player()
        {
            World world = Login();
            GameObject.PlayerIsGargoyle(world).Should().BeFalse();

            world.Writer.SetRace(world.Player, RaceType.GARGOYLE);
            GameObject.PlayerIsGargoyle(world).Should().BeTrue();

            world.Writer.SetRace(world.Player, RaceType.ELF);
            GameObject.PlayerIsGargoyle(world).Should().BeFalse();

            world.Writer.SetRace(world.Player, RaceType.GARGOYLE);
            world.Clear();
            GameObject.PlayerIsGargoyle(world).Should().BeFalse();
        }

        // perf-14: the world map image depends on the race (gargoyle-only statics), so a gargoyle's image is cached apart;
        // the other name is unchanged, so existing caches stay valid
        [Fact]
        public void WorldMap_Cache_Name_Separates_The_Gargoyle_Image()
        {
            ClassicUO.Game.UI.Gumps.WorldMapGump.MapCacheFileName(1, "abc", false).Should().Be("map1_abc.png");
            ClassicUO.Game.UI.Gumps.WorldMapGump.MapCacheFileName(1, "abc", true).Should().Be("map1_abc_gargoyle.png");
        }

        [Fact]
        public void Map_Change_Keeps_The_Player_And_Its_Store()
        {
            World world = Login();
            PlayerMobile player = world.Player;
            PlayerState state = world.PlayerState;
            Fill(state);

            world.Writer.ClearForMapChange(true);

            world.Player.Should().BeSameAs(player);
            world.PlayerState.Should().BeSameAs(state);
            AssertFilled(state);
        }

        [Fact]
        public void Death_And_Resurrection_Keep_The_Player_And_Its_Store()
        {
            // a death is the body changing to a ghost (0x77/0x20); the PlayerMobile is never re-created for it
            World world = Login();
            world.Writer.SetMobileGraphic(world.Player, 0x190, checkBody: true);
            PlayerMobile player = world.Player;
            PlayerState state = world.PlayerState;
            Fill(state);

            world.Writer.SetMobileGraphic(world.Player, 0x192, checkBody: true);
            world.Player.IsDead.Should().BeTrue();
            world.Writer.SetMobileGraphic(world.Player, 0x190, checkBody: true);
            world.Player.IsDead.Should().BeFalse();

            world.Player.Should().BeSameAs(player);
            world.PlayerState.Should().BeSameAs(state);
            AssertFilled(state);
        }

        [Fact]
        public void A_Detached_PlayerMobile_Does_Not_Touch_The_Store()
        {
            // CreateCharAppearanceGump and RaceChangeGump make preview PlayerMobiles; before, each had its own fields
            World world = Login();
            Fill(world.PlayerState);

            var preview = new PlayerMobile(world, 0);
            preview.Destroy(); // before: its Destroy zeroed its own DeathScreenTimer

            AssertFilled(world.PlayerState);
        }

        // ---- packets

        private static void Handle(World world, byte[] packet)
        {
            var handlers = (PacketHandlers.OnPacketBufferReader[])typeof(PacketHandlers)
                .GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(PacketHandlers.Handler)!;
            var reader = new StackDataReader(packet);
            reader.Seek(3); // id and length: every packet here has a variable length
            handlers[packet[0]](world, ref reader);
        }

        private sealed class Packet
        {
            private readonly List<byte> _b = new List<byte>();

            public Packet(byte id)
            {
                _b.Add(id);
                _b.Add(0);
                _b.Add(0);
            }

            public Packet U8(int v)
            {
                _b.Add((byte)v);

                return this;
            }

            public Packet U16(int v) => U8(v >> 8).U8(v);

            public Packet U32(uint v) => U16((int)(v >> 16)).U16((int)(v & 0xFFFF));

            public Packet Ascii(string s, int len)
            {
                byte[] a = Encoding.ASCII.GetBytes(s);

                for (int i = 0; i < len; i++)
                {
                    _b.Add(i < a.Length ? a[i] : (byte)0);
                }

                return this;
            }

            public byte[] Build()
            {
                byte[] a = _b.ToArray();
                a[1] = (byte)(a.Length >> 8);
                a[2] = (byte)a.Length;

                return a;
            }
        }

        [Fact]
        public void Status_0x11_Type6_Writes_Every_Field_Of_The_Store()
        {
            World world = Login();
            PlayerMobile player = world.Player;

            Packet p = new Packet(0x11).U32(PLAYER).Ascii(NAME, 30).U16(50).U16(60)
                .U8(0) // renamable
                .U8(6) // type
                .U8(0) // female
                .U16(90).U16(80).U16(70) // str dex int
                .U16(55).U16(65).U16(45).U16(75) // stam, stam max, mana, mana max
                .U32(4321) // gold
                .U16(33) // physical
                .U16(150) // weight
                .U16(400).U8(1) // weight max, race
                .U16(225).U8(3).U8(5) // stats cap, followers, followers max
                .U16(41).U16(42).U16(43).U16(44) // fire cold poison energy
                .U16(500) // luck
                .U16(11).U16(22) // damage min max
                .U32(7777); // tithing

            for (int i = 0; i < 15; i++)
            {
                p.U16(100 + i);
            }

            Handle(world, p.Build());

            PlayerState s = world.PlayerState;
            (s.Strength, s.Dexterity, s.Intelligence).Should().Be(((ushort)90, (ushort)80, (ushort)70));
            (player.Stamina, player.StaminaMax, player.Mana, player.ManaMax).Should().Be(((ushort)55, (ushort)65, (ushort)45, (ushort)75));
            s.Gold.Should().Be(4321);
            s.PhysicalResistance.Should().Be(33);
            (s.Weight, s.WeightMax).Should().Be(((ushort)150, (ushort)400));
            (s.StatsCap, s.Followers, s.FollowersMax).Should().Be(((short)225, (byte)3, (byte)5));
            (s.FireResistance, s.ColdResistance, s.PoisonResistance, s.EnergyResistance).Should().Be(((short)41, (short)42, (short)43, (short)44));
            s.Luck.Should().Be(500);
            (s.DamageMin, s.DamageMax).Should().Be(((short)11, (short)22));
            s.TithingPoints.Should().Be(7777);
            new short[]
            {
                s.MaxPhysicResistence, s.MaxFireResistence, s.MaxColdResistence, s.MaxPoisonResistence, s.MaxEnergyResistence,
                s.DefenseChanceIncrease, s.MaxDefenseChanceIncrease, s.HitChanceIncrease, s.SwingSpeedIncrease, s.DamageIncrease,
                s.LowerReagentCost, s.SpellDamageIncrease, s.FasterCastRecovery, s.FasterCasting, s.LowerManaCost
            }.Should().Equal(100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114);
        }

        [Fact]
        public void Status_0x11_For_Another_Mobile_Leaves_The_Store_Alone()
        {
            World world = Login();
            Mobile other = world.GetOrCreateMobile(0x0000_5555);
            world.Writer.SetStatus(other, "Other", 1, 1);

            Handle(world, new Packet(0x11).U32(0x0000_5555).Ascii("Other", 30).U16(1).U16(1).U8(0).U8(6).U8(0)
                .U16(90).U16(80).U16(70).U16(1).U16(1).U16(1).U16(1).U32(99).U16(1).U16(1).Build());

            AssertFresh(world.PlayerState, SkillList.Count);
        }

        [Fact]
        public void Skills_0x3A_Full_List_And_Single_Update_Write_The_Store()
        {
            SetProfile(new Profile { ShowSkillsChangedMessage = false });
            World world = Login(inGame: true);

            // type 2: a full list with caps; ids are 1-based on the wire
            Handle(world, new Packet(0x3A).U8(0x02)
                .U16(1).U16(123).U16(100).U8((int)Lock.Down).U16(1000)
                .U16(3).U16(456).U16(400).U8((int)Lock.Locked).U16(1200)
                .Build());

            Skill[] skills = world.PlayerState.Skills;
            (skills[0].ValueFixed, skills[0].BaseFixed, skills[0].CapFixed, skills[0].Lock).Should().Be(((ushort)123, (ushort)100, (ushort)1000, Lock.Down));
            (skills[2].ValueFixed, skills[2].BaseFixed, skills[2].CapFixed, skills[2].Lock).Should().Be(((ushort)456, (ushort)400, (ushort)1200, Lock.Locked));
            skills[1].ValueFixed.Should().Be(0);

            // type 0xDF: one skill with its cap, id 0-based
            Handle(world, new Packet(0x3A).U8(0xDF).U16(1).U16(250).U16(200).U8((int)Lock.Up).U16(1000).Build());

            (skills[1].ValueFixed, skills[1].BaseFixed).Should().Be(((ushort)250, (ushort)200));
            world.PlayerState.Skills.Should().BeSameAs(skills);
        }

        [Fact]
        public void Extended_0xBF_StatLocks_And_Ability_Reset_Write_The_Store()
        {
            SetProfile(new Profile()); // the status gump lookup reads it
            World world = Login();
            int state = ((int)Lock.Down << 4) | ((int)Lock.Locked << 2) | (int)Lock.Up;

            Handle(world, new Packet(0xBF).U16(0x19).U8(2).U32(PLAYER).U8(0).U8(state).Build());

            PlayerState s = world.PlayerState;
            (s.StrLock, s.DexLock, s.IntLock).Should().Be((Lock.Down, Lock.Locked, Lock.Up));

            // another serial's locks are not the player's
            Handle(world, new Packet(0xBF).U16(0x19).U8(2).U32(0x0000_5555).U8(0).U8(0).Build());
            (s.StrLock, s.DexLock, s.IntLock).Should().Be((Lock.Down, Lock.Locked, Lock.Up));

            // 0x21: the abilities' active bit (0x80) cleared
            s.PrimaryAbility = (Ability)0x85;
            s.SecondaryAbility = (Ability)0x86;
            Handle(world, new Packet(0xBF).U16(0x21).Build());
            s.Abilities.Should().Equal((Ability)0x05, (Ability)0x06);
        }

        [Fact]
        public void Buff_0xDF_Removal_Writes_The_Store()
        {
            // the table buff.txt (or its default) fills at startup
            BuffTableField.SetValue(null, typeof(BuffTable).GetField("_defaultTable", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
            World world = Login();
            const BuffIconType icon = (BuffIconType)0x03EA;
            world.PlayerState.AddBuff(icon, 0x754C, 10, "x");
            world.PlayerState.IsBuffIconExists(icon).Should().BeTrue();

            Handle(world, new Packet(0xDF).U32(PLAYER).U16((int)icon).U16(0).Build());

            world.PlayerState.IsBuffIconExists(icon).Should().BeFalse();
            world.PlayerState.BuffIcons.Should().BeEmpty();
        }

        [Fact]
        public void Handlers_Before_The_Player_Exists_Write_Nothing()
        {
            // 0x11 and 0xDF return without a player, as they did when the fields were on it
            var world = new World();
            PlayerState s = world.PlayerState;

            Handle(world, new Packet(0x11).U32(PLAYER).Ascii(NAME, 30).U16(1).U16(1).U8(0).U8(6).U8(0).U16(90).Build());
            Handle(world, new Packet(0xDF).U32(PLAYER).U16(0x03EA).U16(0).Build());

            world.PlayerState.Should().BeSameAs(s);
            AssertFresh(s, 0);
        }
    }
}
