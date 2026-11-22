using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // The outgoing packets that read Client.Game (the client version, the protocol flags, the speech keywords), built through
    // the test seam (GameSeam: 7.0.13, the keywords bank and guards) and compared byte for byte like OutgoingPacketTests, in every
    // NetWriter mode. The expected bytes were taken from the StackDataWriter path (2026-11-21, span_reader off) and their
    // id, length and leading fields checked by hand against the protocol. Regenerate only on purpose:
    // CUO_PACKET_GOLDEN_OUT=<file> dotnet test --filter Outgoing_Game_Golden_Dump.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class OutgoingGameTests : IDisposable
    {
        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            _seam.Dispose();
        }

        private sealed record Case(string Name, Action<NetClient, World> Send, string Hex);

        private static Case C(string name, Action<NetClient, World> send, string hex = "") => new(name, send, hex);

        private static void CreateCharacter(NetClient s, World w, bool female, bool hair)
        {
            var skills = new List<SkillEntry> { new(0, "Alchemy", true), new(1, "Anatomy", true), new(2, "Animal Lore", true), new(3, "Archery", true) };
            w.CreatePlayer(OutgoingPacketTests.PLAYER, skills);
            PlayerMobile pl = w.Player;
            w.Writer.SetMobileBody(pl, (ushort)(female ? 0x0191 : 0x0190), 0x83EA, female ? Flags.Female : Flags.None, checkBody: true, setDirection: false, 0);
            PlayerState st = w.PlayerState;
            st.Strength = 60;
            st.Dexterity = 10;
            st.Intelligence = 20;
            st.Skills[0].ValueFixed = 300;
            st.Skills[2].ValueFixed = 500;
            st.Skills[3].ValueFixed = 100;

            if (hair)
            {
                w.Writer.Equip(0x4000_0010, 0x203B, Layer.Hair, pl.Serial, 0x044E, out _, out _);
            }

            s.Send_CreateCharacter(pl, "Avatar", st, 3, 0x7F000001, 0, 1, 2);
        }

        private static readonly Case[] Cases =
        {
            C("SelectCharacter", (s, _) => s.Send_SelectCharacter(1, "Avatar", 0x7F000001), "5DEDEDEDED417661746172000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000017F000001"),
            C("SelectCharacter_long_name", (s, _) => s.Send_SelectCharacter(0, new string('n', 40), 0), "5DEDEDEDED6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E6E0000000000000000000000000000000000000000000000000000000000000000000000000000"),
            C("ASCIISpeech", (s, _) => s.Send_ASCIISpeechRequest("hello café", MessageType.Regular, 3, 0x0034), "030013000034000368656C6C6F20636166E900"),
            C("ASCIISpeech_yell_empty", (s, _) => s.Send_ASCIISpeechRequest("", MessageType.Yell, 0, 0), "030009090000000000"),
            C("UnicodeSpeech", (s, _) => s.Send_UnicodeSpeechRequest("héllo 世界", MessageType.Regular, 3, 0x0034, "ENU"), "AD001E0000340003454E5500006800E9006C006C006F00204E16754C0000"),
            C("ASCIISpeech_keywords", (s, _) => s.Send_ASCIISpeechRequest("bank guards", MessageType.Regular, 3, 0x0034), "030014C00034000362616E6B2067756172647300"),
            C("UnicodeSpeech_keyword", (s, _) => s.Send_UnicodeSpeechRequest("bank", MessageType.Regular, 3, 0x0034, "ENU"), "AD0014C000340003454E550000100262616E6B00"),
            C("UnicodeSpeech_whisper", (s, _) => s.Send_UnicodeSpeechRequest("psst", MessageType.Whisper, 1, 0x0021, "DEU"), "AD001608002100014445550000700073007300740000"),
            C("ChatJoin", (s, _) => s.Send_ChatJoinCommand("General"), "B3001F000000000062002200470065006E006500720061006C000000220020"),
            C("ChatJoin_password", (s, _) => s.Send_ChatJoinCommand("General", "pw"), "B30025000000000062002200470065006E006500720061006C000000220020007000770000"),
            C("ChatCreate", (s, _) => s.Send_ChatCreateChannelCommand("Room", "secret"), "B300250000000000630052006F006F006D0000007B0073006500630072006500740000007D"),
            C("ChatCreate_open", (s, _) => s.Send_ChatCreateChannelCommand("Room"), "B300130000000000630052006F006F006D0000"),
            C("ChatLeave", (s, _) => s.Send_ChatLeaveChannelCommand(), "B30009000000000043"),
            C("ChatMessage", (s, _) => s.Send_ChatMessageCommand("hi 世界"), "B300150000000000610068006900204E16754C0000"),
            C("ClientType", (s, _) => s.Send_ClientType(), "BF000A000F0A00000000"),
            C("CastSpell_5", (s, _) => s.Send_CastSpell(5), "BF0009001C00020005"),
            C("CastSpell_100", (s, _) => s.Send_CastSpell(100), "BF0009001C00020064"),
            C("CreateCharacter_male", (s, w) => CreateCharacter(s, w, female: false, hair: false), "00EDEDEDEDFFFFFFFF00417661746172000000000000000000000000000000000000000000000000000000000000000000010000000002000000000000000000000000000000023C0A1402F4002C036483EA00000000000000000003000000017F00000100000000"),
            C("CreateCharacter_female_hair", (s, w) => CreateCharacter(s, w, female: true, hair: true), "00EDEDEDEDFFFFFFFF00417661746172000000000000000000000000000000000000000000000000000000000000000000010000000002000000000000000000000000000000033C0A1402F4002C036483EA203B044E000000000003000000017F00000100000000"),
        };

        private static readonly SpanBufferMode[] Modes =
#if CUO_DEVTOOLS
            { SpanBufferMode.Old, SpanBufferMode.New, SpanBufferMode.CheckOld, SpanBufferMode.CheckNew };
#else
            { SpanBufferMode.Old, SpanBufferMode.New };
#endif

        public static IEnumerable<object[]> CaseModes() =>
            from c in Cases from m in Modes select new object[] { c.Name, m };

        private static string Build(Case c)
        {
            var (client, socket) = OutgoingPacketTests.Connected();
            c.Send(client, new World());
            client.Flush();

            return Convert.ToHexString(socket.Bytes.ToArray());
        }

        [Theory]
        [MemberData(nameof(CaseModes))]
        public void Builds_The_Exact_Bytes(string name, SpanBufferMode mode)
        {
            SpanBuffers.ModeOverride = mode;
#if CUO_DEVTOOLS
            long before = SpanBuffers.WriterMismatches;
#endif
            Case c = Cases.Single(x => x.Name == name);
            string hex = Build(c);

            hex.Should().Be(c.Hex, $"{name} in mode {mode}");
            byte[] b = Convert.FromHexString(hex);
            int fixedLength = new PacketsTable(ClientVersion.CV_70130).GetPacketLength(b[0]);

            if (fixedLength > 0)
            {
                b.Length.Should().Be(fixedLength, "a fixed-length packet is exactly its table length");
            }
            else
            {
                ((b[1] << 8) | b[2]).Should().Be(b.Length, "a variable-length packet's length field is its size");
            }
#if CUO_DEVTOOLS
            SpanBuffers.WriterMismatches.Should().Be(before);
#endif
        }

        [Fact]
        public void Every_Case_Name_Is_Unique() => Cases.Select(c => c.Name).Should().OnlyHaveUniqueItems();

        [Fact]
        public void Outgoing_Game_Golden_Dump()
        {
            string path = Environment.GetEnvironmentVariable("CUO_PACKET_GOLDEN_OUT");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            SpanBuffers.ModeOverride = SpanBufferMode.Old;
            File.WriteAllLines(path, Cases.Select(c => $"{c.Name}={Build(c)}"));
        }
    }
}
