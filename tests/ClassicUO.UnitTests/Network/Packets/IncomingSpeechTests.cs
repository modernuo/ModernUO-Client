using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Incoming speech (0x1C, 0xAE, 0xC1, 0xCC) through the test seam (GameSeam: the profile's chat font, the cliloc table
    // without a pack, so a cliloc prints its fallback text), the whole receive path, every read mode, compressed or not,
    // span_reader off and on. The text is read back from MessageManager.MessageReceived. System messages only: a message
    // over an entity renders overhead text, which needs the fonts.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingSpeechTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;

        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            _seam.Dispose();
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private static (World world, List<MessageEventArgs> said) InGame()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);
            var said = new List<MessageEventArgs>();
            world.MessageManager.MessageReceived += (_, e) => said.Add(e);

            return (world, said);
        }

        // ---- 0x1C Talk (ASCII)

        private static PacketBuilder Talk(uint serial, int graphic, MessageType type, int hue, int font, string name) =>
            new PacketBuilder(0x1C, true).U32(serial).U16(graphic).U8((int)type).U16(hue).U16(font).Ascii(name, 30);

        [Theory]
        [MemberData(nameof(Modes))]
        public void Talk_0x1C_System_Message(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Talk(0xFFFF_FFFF, 0xFFFF, MessageType.System, 0x0035, 3, "System").Ascii("Welcome, café").Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            said.Should().ContainSingle();
            said[0].Text.Should().Be("Welcome, café");
            said[0].Name.Should().Be("System");
            said[0].Hue.Should().Be(0x0035);
            said[0].Type.Should().Be(MessageType.System);
            said[0].TextType.Should().Be(TextType.SYSTEM);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Talk_0x1C_Header_Only_Says_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();

            // 44 bytes: no text after the name; an empty text is not printed
            var r = PacketPipe.Deliver(world, mode, read, compressed, Talk(0, 0, MessageType.System, 0, 3, "x").Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            said.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Talk_0x1C_System_Handshake_Is_Acknowledged(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Talk(0, 0, MessageType.Regular, 0xFFFF, 0xFFFF, "SYSTEM").Ascii("x").Build());

            r.Packets.Should().Be(1);
            said.Should().BeEmpty();
            r.Sent.Should().NotBeEmpty();
            r.Sent[0].Should().Be(0x03, "the client answers with its ACK talk");
        }

        // ---- 0xAE UnicodeTalk

        private static PacketBuilder UTalk(uint serial, int graphic, MessageType type, int hue, int font, string lang, string name) =>
            new PacketBuilder(0xAE, true).U32(serial).U16(graphic).U8((int)type).U16(hue).U16(font).Ascii(lang, 4).Ascii(name, 30);

        [Theory]
        [MemberData(nameof(Modes))]
        public void UnicodeTalk_0xAE_System_Message(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                UTalk(0xFFFF_FFFF, 0xFFFF, MessageType.System, 0x0481, 3, "ENU", "System").UnicodeBE("héllo 世界").Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            said.Should().ContainSingle();
            said[0].Text.Should().Be("héllo 世界");
            said[0].Language.Should().Be("ENU");
            said[0].IsUnicode.Should().BeTrue();
            said[0].Hue.Should().Be(0x0481);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UnicodeTalk_0xAE_Guild_And_Unterminated_Text(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                UTalk(0xFFFF_FFFF, 0, MessageType.Guild, 0x44, 3, "ENU", "Bob").UnicodeBE("guild news", terminate: false).Build());

            r.Disconnects.Should().Be(0);
            said.Should().ContainSingle();
            said[0].Text.Should().Be("guild news");
            said[0].TextType.Should().Be(TextType.GUILD_ALLY);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UnicodeTalk_0xAE_Before_The_Game_Is_Dropped(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var world = new World();
            var said = new List<MessageEventArgs>();
            world.MessageManager.MessageReceived += (_, e) => said.Add(e);

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                UTalk(0xFFFF_FFFF, 0, MessageType.System, 0, 3, "ENU", "System").UnicodeBE("early").Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            said.Should().BeEmpty();
        }

        // ---- 0xC1 / 0xCC DisplayClilocString (no pack: the cliloc table is empty, so the text is the fallback)

        [Theory]
        [MemberData(nameof(Modes))]
        public void Cliloc_0xC1_System_Message_With_Arguments(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();
            byte[] p = new PacketBuilder(0xC1, true).U32(0xFFFF_FFFF).U16(0xFFFF).U8((int)MessageType.System).U16(0x03B2).U16(3)
                .U32(1042971).Ascii("System", 30).UnicodeLE("abc").Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            said.Should().ContainSingle();
            said[0].Text.Should().Be(_seam.Game.UO.FileManager.Clilocs.Translate(1042971, "abc"));
            said[0].Hue.Should().Be(0x03B2);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Cliloc_0xCC_Affix_Appended_And_Prepended(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();
            string bare = _seam.Game.UO.FileManager.Clilocs.Translate(500000, null);
            PacketBuilder Affix(AffixType flags, string affix) =>
                new PacketBuilder(0xCC, true).U32(0xFFFF_FFFF).U16(0).U8((int)MessageType.Regular).U16(0).U16(3)
                    .U32(500000).U8((int)flags).Ascii("System", 30).Ascii(affix);

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Affix(AffixType.Append, " (end)").Build(),
                Affix(AffixType.Prepend | AffixType.System, "start: ").Build());

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            said.Should().HaveCount(2);
            said[0].Text.Should().Be(bare + " (end)");
            said[1].Text.Should().Be("start: " + bare);
            said[1].Type.Should().Be(MessageType.System);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Cliloc_0xC1_Label_On_An_Unknown_Object_Is_Dropped(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, said) = InGame();
            byte[] p = new PacketBuilder(0xC1, true).U32(0x4000_0999).U16(0).U8((int)MessageType.Label).U16(0).U16(3)
                .U32(1042971).Ascii("", 30).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            said.Should().BeEmpty();
        }
    }
}
