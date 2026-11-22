using System;
using System.Collections.Generic;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Incoming 0xBF subcommands, sound and music (a silent AudioManager: the reads and the framing are what is checked),
    // weather, the quest arrow, buffs and the mega cliloc, through the test seam (GameSeam), the whole receive path, every
    // read mode, compressed or not, span_reader off and on.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingMiscTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint ITEM = 0x4000_0100;

        private static readonly FieldInfo BuffTableField = typeof(BuffTable).GetField("_table", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo BuffDefault = typeof(BuffTable).GetField("_defaultTable", BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly GameSeam _seam = new();
        private readonly object _oldBuffTable;

        public IncomingMiscTests()
        {
            // the built-in buff table (BuffTable.Load would read Data/Client/buff.txt next to the binary)
            _oldBuffTable = BuffTableField.GetValue(null);
            BuffTableField.SetValue(null, BuffDefault.GetValue(null));
        }

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            BuffTableField.SetValue(null, _oldBuffTable);
            _seam.Dispose();
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private static World InGame()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);

            return world;
        }

        private static PacketBuilder Bf(int sub) => new PacketBuilder(0xBF, true).U16(sub);

        // ---- 0xBF

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_1_And_2_Fill_The_Fast_Walk_Stack(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Bf(1).U32(11).U32(12).U32(0).U32(0).U32(0).U32(77).Build(), // the sixth key is past the 5-key stack
                Bf(2).U32(99).Build());

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            var stack = world.Player.Walker.FastWalkStack;
            var keys = new List<uint>();

            for (uint k; (k = stack.GetValue()) != 0;)
            {
                keys.Add(k);
            }

            keys.Should().Equal(11u, 12u, 99u);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_0x19_Stat_Locks_And_Bonded_Dead(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Bf(0x19).U8(2).U32(PLAYER).U8(0).U8((1 << 4) | (2 << 2) | 0).Build(),
                Bf(0x19).U8(0).U32(MOB).U8(1).Build());

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.PlayerState.StrLock.Should().Be(Lock.Down);
            world.PlayerState.DexLock.Should().Be(Lock.Locked);
            world.PlayerState.IntLock.Should().Be(Lock.Up);
            world.Mobiles.Get(MOB).IsDead.Should().BeTrue();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_0x26_Speed_Mode_Out_Of_Range_Is_Normal(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            PacketPipe.Deliver(world, mode, read, compressed, Bf(0x26).U8((int)CharacterSpeedType.FastUnmount).Build()).Disconnects.Should().Be(0);
            world.Player.SpeedMode.Should().Be(CharacterSpeedType.FastUnmount);

            PacketPipe.Deliver(world, mode, read, compressed, Bf(0x26).U8(200).Build()).Disconnects.Should().Be(0);
            world.Player.SpeedMode.Should().Be(CharacterSpeedType.Normal);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_0x21_Clears_The_Ability_Toggles_And_Unknown_Sub_Is_Skipped(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            world.PlayerState.Abilities[0] = (Ability)0x85;
            world.PlayerState.Abilities[1] = (Ability)0x02;

            var r = PacketPipe.Deliver(world, mode, read, compressed, Bf(0x7777).U32(1).U32(2).Build(), Bf(0x21).Build(),
                new PacketBuilder(0xC8, false).U8(9).Build());

            r.Packets.Should().Be(3);
            r.Disconnects.Should().Be(0);
            world.PlayerState.Abilities[0].Should().Be((Ability)0x05);
            world.PlayerState.Abilities[1].Should().Be((Ability)0x02);
            world.ClientViewRange.Should().Be(9, "the packet after an unknown subcommand is framed correctly");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_8_Map_Change(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            PacketPipe.Deliver(world, mode, read, compressed, Bf(8).U8(0).Build()).Disconnects.Should().Be(0);

            world.MapIndex.Should().Be(0);
        }

        // ---- 0x54 sound, 0x6D music (silent: nothing plays; the packet is read and the stream stays framed)

        [Theory]
        [MemberData(nameof(Modes))]
        public void Sound_0x54_And_Music_0x6D(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x54, false).U8(1).U16(0x0055).U16(0).U16(100).U16(200).U16(0).Build(),
                new PacketBuilder(0x6D, false).U16(0x0010).Build(),
                new PacketBuilder(0x6D, false).U8(0x1F).U8(0xFF).Build(),
                new PacketBuilder(0xC8, false).U8(7).Build());

            r.Packets.Should().Be(4);
            r.Disconnects.Should().Be(0);
            world.ClientViewRange.Should().Be(7);
        }

        // ---- 0x65 weather: only in a GameScene (the seam has none), so ignored here; the stream stays framed

        [Theory]
        [MemberData(nameof(Modes))]
        public void Weather_0x65_Without_A_Game_Scene_Is_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            WeatherType? before = world.Weather.CurrentWeather;

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x65, false).U8((int)WeatherType.WT_RAIN).U8(50).U8(20).Build(),
                new PacketBuilder(0xC8, false).U8(6).Build());

            r.Packets.Should().Be(2);
            world.Weather.CurrentWeather.Should().Be(before);
            world.ClientViewRange.Should().Be(6);
        }

        // ---- 0xBA quest arrow (7.0.13: with the serial)

        [Theory]
        [MemberData(nameof(Modes))]
        public void QuestArrow_0xBA_Shows_Moves_And_Hides(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] Arrow(bool on, int x, int y) => new PacketBuilder(0xBA, false).U8(on ? 1 : 0).U16(x).U16(y).U32(ITEM).Build();

            PacketPipe.Deliver(world, mode, read, compressed, Arrow(true, 100, 200)).Disconnects.Should().Be(0);
            QuestArrowGump arrow = UIManager.GetGump<QuestArrowGump>(ITEM);
            arrow.Should().NotBeNull();

            PacketPipe.Deliver(world, mode, read, compressed, Arrow(true, 300, 400)).Disconnects.Should().Be(0);
            UIManager.GetGump<QuestArrowGump>(ITEM).Should().BeSameAs(arrow, "a second arrow moves the first");

            PacketPipe.Deliver(world, mode, read, compressed, Arrow(false, 0, 0)).Disconnects.Should().Be(0);
            arrow.IsDisposed.Should().BeTrue();
        }

        // ---- 0xDF buffs

        private static PacketBuilder Buff(BuffIconType icon, int timer, uint title, uint desc, uint extra, string args)
        {
            return new PacketBuilder(0xDF, true).U32(PLAYER).U16((int)icon).U16(1)
                .U16((int)icon).U16(0).U16((int)icon).U16(1).U32(0).U16(timer).Zero(3)
                .U32(title).U32(desc).U32(extra).U16(args.Length * 2).UnicodeLE(args);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Buff_0xDF_Add_Then_Remove(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            BuffIconType icon = (BuffIconType)0x03EA;

            // one text block (RunUO/ModernUO end the packet after the first)
            var r = PacketPipe.Deliver(world, mode, read, compressed, Buff(icon, 30, 1075655, 0, 0, "\t5").Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            world.PlayerState.IsBuffIconExists(icon).Should().BeTrue();
            world.PlayerState.BuffIcons[icon].Graphic.Should().Be(BuffTable.Table[1]);
            world.PlayerState.BuffIcons[icon].Text.Should().Contain(_seam.Game.UO.FileManager.Clilocs.Translate(1075655, "\t5", true));

            byte[] remove = new PacketBuilder(0xDF, true).U32(PLAYER).U16((int)icon).U16(0).Build();
            PacketPipe.Deliver(world, mode, read, compressed, remove).Disconnects.Should().Be(0);
            world.PlayerState.IsBuffIconExists(icon).Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Buff_0xDF_Icon_Past_The_Table_Is_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            BuffIconType icon = unchecked((BuffIconType)0xFFF0);

            var r = PacketPipe.Deliver(world, mode, read, compressed, Buff(icon, 1, 1, 0, 0, "").Build(),
                new PacketBuilder(0xC8, false).U8(5).Build());

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.PlayerState.IsBuffIconExists(icon).Should().BeFalse();
            world.ClientViewRange.Should().Be(5);
        }

        // ---- 0xD6 mega cliloc

        private static PacketBuilder Mega(uint serial, uint revision, params (uint cliloc, string arg)[] lines)
        {
            var b = new PacketBuilder(0xD6, true).U16(1).U32(serial).U16(0).U32(revision);

            foreach (var (cliloc, arg) in lines)
            {
                b.U32(cliloc).U16(arg.Length * 2).UnicodeLE(arg, terminate: false);
            }

            return b.U32(0);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void MegaCliloc_0xD6_Name_And_Data(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            world.GetOrCreateItem(ITEM);
            var cl = _seam.Game.UO.FileManager.Clilocs;

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Mega(ITEM, 0x1234, (1050039, "\t5\tgold"), (1060658, "a\tb"), (1072789, "")).Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            world.OPL.TryGetNameAndData(ITEM, out string name, out string data).Should().BeTrue();
            name.Should().Be(cl.Translate(1050039, "\t5\tgold", true));
            data.Should().Be(cl.Translate(1060658, "a\tb", true) + "\n" + cl.Translate(1072789, string.Empty, true));
            world.Items.Get(ITEM).Name.Should().Be(name);
            world.OPL.IsRevisionEquals(ITEM, 0x1234).Should().BeTrue();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void MegaCliloc_0xD6_Unknown_Above_1_Is_Ignored_And_Empty_List(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            world.GetOrCreateItem(ITEM);
            byte[] ignored = new PacketBuilder(0xD6, true).U16(2).U32(ITEM).U16(0).U32(7).U32(1050039).U16(0).U32(0).Build();
            byte[] empty = Mega(MOB, 9).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, ignored, empty);

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.OPL.IsRevisionEquals(ITEM, 7).Should().BeFalse();
            world.OPL.IsRevisionEquals(MOB, 9).Should().BeTrue();
            world.OPL.TryGetNameAndData(MOB, out string name, out _);
            name.Should().BeNullOrEmpty();
        }
    }
}
