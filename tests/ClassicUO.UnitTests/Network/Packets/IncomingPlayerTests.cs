using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Incoming player-state packets whose handlers reach Client.Game (the version, the window title, the cursor's held item,
    // the skill names in FileManager): run through the test seam (GameSeam), the whole receive path, every read mode,
    // compressed or not, span_reader off and on.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingPlayerTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint ITEM = 0x4000_0100;
        private const uint BAG = 0x4000_0300;

        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            _seam.Dispose();
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private static World InGame()
        {
            var world = new World();
            var skills = new List<SkillEntry> { new(0, "Alchemy", true), new(1, "Anatomy", true), new(2, "Animal Lore", true) };
            world.CreatePlayer(PLAYER, skills);
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);

            return world;
        }

        // ---- 0x11 CharacterStatus

        private static PacketBuilder Status(uint serial, string name, int hits, int hitsMax, int type) =>
            new PacketBuilder(0x11, true).U32(serial).Ascii(name, 30).U16(hits).U16(hitsMax).U8(0).U8(type);

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterStatus_0x11_Basic_For_A_Mobile(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed, Status(MOB, "a mongbat", 7, 12, 0).Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Mobile m = world.Mobiles.Get(MOB);
            m.Name.Should().Be("a mongbat");
            m.Hits.Should().Be(7);
            m.HitsMax.Should().Be(12);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterStatus_0x11_Type6_For_The_Player(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            PacketBuilder b = Status(PLAYER, "Lord British", 90, 100, 6).U8(1) // female
                .U16(80).U16(70).U16(60) // str dex int
                .U16(50).U16(55).U16(40).U16(45) // stamina, stamina max, mana, mana max
                .U32(12345).U16(30).U16(200) // gold, physical, weight
                .U16(400).U8(2) // weight max, race (elf)
                .U16(225).U8(1).U8(5) // stats cap, followers, followers max
                .U16(31).U16(32).U16(33).U16(34).U16(100).U16(5).U16(15).U32(999) // resists, luck, damage, tithing
                .U16(70).U16(71).U16(72).U16(73).U16(74); // the first five type-6 fields; the rest are absent (read as 0)

            var r = PacketPipe.Deliver(world, mode, read, compressed, b.Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            PlayerMobile pl = world.Player;
            PlayerState s = world.PlayerState;
            pl.Name.Should().Be("Lord British");
            pl.Hits.Should().Be(90);
            pl.Stamina.Should().Be(50);
            pl.StaminaMax.Should().Be(55);
            pl.Mana.Should().Be(40);
            pl.ManaMax.Should().Be(45);
            pl.IsFemale.Should().BeTrue();
            pl.Race.Should().Be(RaceType.ELF);
            s.Strength.Should().Be(80);
            s.Dexterity.Should().Be(70);
            s.Intelligence.Should().Be(60);
            s.Gold.Should().Be(12345u);
            s.PhysicalResistance.Should().Be(30);
            s.Weight.Should().Be(200);
            s.WeightMax.Should().Be(400);
            s.StatsCap.Should().Be(225);
            s.Followers.Should().Be(1);
            s.FollowersMax.Should().Be(5);
            s.FireResistance.Should().Be(31);
            s.EnergyResistance.Should().Be(34);
            s.Luck.Should().Be(100);
            s.DamageMin.Should().Be(5);
            s.DamageMax.Should().Be(15);
            s.TithingPoints.Should().Be(999u);
            s.MaxPhysicResistence.Should().Be(70);
            s.MaxEnergyResistence.Should().Be(74);
            s.DefenseChanceIncrease.Should().Be(0, "the packet ends before it");
            s.LowerManaCost.Should().Be(0);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterStatus_0x11_Type1_Without_Race_Computes_WeightMax(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            PacketBuilder b = Status(PLAYER, "p", 1, 1, 1).U8(0).U16(100).U16(10).U16(10).U16(1).U16(1).U16(1).U16(1).U32(0).U16(0).U16(0);

            PacketPipe.Deliver(world, mode, read, compressed, b.Build()).Disconnects.Should().Be(0);

            world.PlayerState.Strength.Should().Be(100);
            world.PlayerState.WeightMax.Should().Be(7 * (100 >> 1) + 40, "7.0.13 is after 5.0.0a");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterStatus_0x11_Item_And_Unknown_Serials(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            world.GetOrCreateItem(ITEM);

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Status(ITEM, "a door", 3, 4, 0).Build(),
                Status(0x0000_0999, "nobody", 1, 1, 0).Build());

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.Items.Get(ITEM).Name.Should().Be("a door");
            world.Get(0x0000_0999).Should().BeNull();
        }

        // ---- 0x16 / 0x17 NewHealthbarUpdate

        private static byte[] Healthbar(byte id, uint serial, params (int type, bool on)[] entries)
        {
            var b = new PacketBuilder(id, true).U32(serial).U16(entries.Length);

            foreach (var (type, on) in entries)
            {
                b.U16(type).U8(on ? 1 : 0);
            }

            return b.Build();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Healthbar_0x17_Poison_And_YellowBar(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed, Healthbar(0x17, MOB, (1, true), (2, true)));

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Mobile m = world.Mobiles.Get(MOB);
            m.IsPoisoned.Should().BeTrue("7.0.13 uses the SA poison bit");
            m.IsYellowHits.Should().BeTrue();

            PacketPipe.Deliver(world, mode, read, compressed, Healthbar(0x16, MOB, (1, false), (2, false), (3, true)));
            m.IsPoisoned.Should().BeFalse();
            m.IsYellowHits.Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Healthbar_0x17_Empty_List_And_Unknown_Mobile(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed, Healthbar(0x17, MOB), Healthbar(0x17, 0x0000_0999, (2, true)));

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.Mobiles.Get(MOB).IsYellowHits.Should().BeFalse();
        }

        // ---- 0x3A UpdateSkills

        private static PacketBuilder SkillEntry(PacketBuilder b, int id, int real, int baseVal, Lock l, int? cap) =>
            cap is int c ? b.U16(id).U16(real).U16(baseVal).U8((int)l).U16(c) : b.U16(id).U16(real).U16(baseVal).U8((int)l);

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateSkills_0x3A_Full_List_With_Caps(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            var b = new PacketBuilder(0x3A, true).U8(0x02); // full list, ids from 1, with caps
            SkillEntry(b, 1, 500, 450, Lock.Up, 1000);
            SkillEntry(b, 3, 1000, 1000, Lock.Locked, 1200);
            b.U16(0);

            var r = PacketPipe.Deliver(world, mode, read, compressed, b.Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Skill alchemy = world.PlayerState.Skills[0];
            alchemy.ValueFixed.Should().Be(500);
            alchemy.BaseFixed.Should().Be(450);
            alchemy.CapFixed.Should().Be(1000);
            alchemy.Lock.Should().Be(Lock.Up);
            Skill lore = world.PlayerState.Skills[2];
            lore.ValueFixed.Should().Be(1000);
            lore.CapFixed.Should().Be(1200);
            lore.Lock.Should().Be(Lock.Locked);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateSkills_0x3A_Single_Update_And_Out_Of_Range_Id(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            // 0xFF: one skill without a cap, ids from 0; id 50 is past the player's three skills and is ignored
            byte[] one = SkillEntry(new PacketBuilder(0x3A, true).U8(0xFF), 1, 321, 300, Lock.Down, null).Build();
            byte[] outOfRange = SkillEntry(new PacketBuilder(0x3A, true).U8(0xFF), 50, 1, 1, Lock.Down, null).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, one, outOfRange);

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.PlayerState.Skills[1].ValueFixed.Should().Be(321);
            world.PlayerState.Skills[1].Lock.Should().Be(Lock.Down);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateSkills_0x3A_0xFE_Replaces_The_Skill_Names(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0x3A, true).U8(0xFE).U16(2).U8(1).U8(5).Ascii("Zebra", 5).U8(0).U8(3).Ascii("Axe", 3).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            var skills = _seam.Game.UO.FileManager.Skills;
            skills.Skills.Select(s => s.Name).Should().Equal("Zebra", "Axe");
            skills.Skills[0].HasAction.Should().BeTrue();
            skills.Skills[1].HasAction.Should().BeFalse();
            skills.SortedSkills.Select(s => s.Name).Should().Equal("Axe", "Zebra");
        }

        // ---- 0x27 / 0x28 / 0x29: the held item

        private World Holding(out Item item)
        {
            World world = InGame();
            PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false,
                new PacketBuilder(0x2E, false).U32(BAG).U16(0x0E75).I8(0).U8((int)Layer.Backpack).U32(PLAYER).U16(0).Build(),
                new PacketBuilder(0x25, false).U32(ITEM).U16(0x0EED).U8(0).U16(5).U16(10).U16(20).U8(0).U32(BAG).U16(0).Build());
            item = world.Items.Get(ITEM);
            _seam.ItemHold.Set(item, 5);

            return world;
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void DenyMoveItem_0x27_Returns_The_Held_Item_And_Clears_The_Hold(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = Holding(out _);
            var said = new List<string>();
            world.MessageManager.MessageReceived += (_, e) => said.Add(e.Text);

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x27, false).U8(1).Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            _seam.ItemHold.Enabled.Should().BeFalse();
            _seam.ItemHold.Serial.Should().Be(0u);
            Item back = world.Items.Get(ITEM);
            back.Container.Should().Be(BAG);
            back.Amount.Should().Be(5);
            said.Should().ContainSingle(ServerErrorMessages.GetError(0x27, 1));
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void DenyMoveItem_0x27_Code_5_And_Up_Prints_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = Holding(out _);
            var said = new List<string>();
            world.MessageManager.MessageReceived += (_, e) => said.Add(e.Text);

            PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x27, false).U8(5).Build()).Disconnects.Should().Be(0);

            said.Should().BeEmpty();
            _seam.ItemHold.Enabled.Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void EndDragging_0x28_And_DropAccepted_0x29_End_The_Hold(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = Holding(out _);
            _seam.ItemHold.Dropped = true;

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x28, false).U32(ITEM).Build());

            r.Packets.Should().Be(1);
            _seam.ItemHold.Enabled.Should().BeFalse();
            _seam.ItemHold.Dropped.Should().BeFalse();

            _seam.ItemHold.Enabled = true;
            _seam.ItemHold.Dropped = true;
            PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x29, false).Build()).Packets.Should().Be(1);
            _seam.ItemHold.Enabled.Should().BeFalse();
            _seam.ItemHold.Dropped.Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Hold_Packets_Before_The_Game_Are_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var world = new World();
            _seam.ItemHold.Enabled = true;

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x27, false).U8(0).Build(), new PacketBuilder(0x28, false).U32(ITEM).Build(), new PacketBuilder(0x29, false).Build());

            r.Packets.Should().Be(3);
            r.Disconnects.Should().Be(0);
            _seam.ItemHold.Enabled.Should().BeTrue();
        }

        // ---- 0x2C DeathScreen

        [Theory]
        [MemberData(nameof(Modes))]
        public void DeathScreen_0x2C_Sets_The_Timer_And_Leaves_War_Mode(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false, new PacketBuilder(0x72, false).U8(1).U8(0).U8(0x32).U8(0).Build());
            world.Player.InWarMode.Should().BeTrue();

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x2C, false).U8(0).Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            world.PlayerState.DeathScreenTimer.Should().BeGreaterThan(0u);
            r.Sent.Should().Equal(new byte[] { 0x72, 0x00, 0x32, 0x00, 0x00 }, "the client asks the server to leave war mode (as the old writer sends it)");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void DeathScreen_0x2C_Action_1_Does_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x2C, false).U8(1).Build());

            r.Packets.Should().Be(1);
            r.Sent.Should().BeEmpty();
            world.PlayerState.DeathScreenTimer.Should().Be(0u);
        }
    }
}
