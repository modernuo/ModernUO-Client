using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Every outgoing packet the client can build without the UO files (Client.Game, FileManager): built through NetClient.Send
    // and the send queue, flushed to a capturing socket, and compared byte for byte, in every NetWriter mode (span_reader off
    // and on, and both checker modes in developer builds). The expected bytes were taken from the StackDataWriter path
    // (2026-11-21, before span_reader went default on) and checked by hand against the packet layout for the id, header and
    // length fields; a later change of the queue or the writer must keep them. Regenerate only on purpose:
    // CUO_PACKET_GOLDEN_OUT=<file> dotnet test --filter Outgoing_Golden_Dump writes name=hex for every case.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class OutgoingPacketTests : IDisposable
    {
        internal const uint PLAYER = 0x0000_1234;
        private const uint Seed = 0x0A0B0C0D;

        public void Dispose() => SpanBuffers.ModeOverride = null;

        internal sealed class CaptureSocket : SocketWrapper
        {
            public readonly List<byte> Bytes = new();
            public override bool IsConnected => true;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) => Bytes.AddRange(new ArraySegment<byte>(buffer, offset, count));
            public override int Read(byte[] buffer) => 0;
            public override void Disconnect() { }
            public override void Dispose() { }
        }

        internal static (NetClient client, CaptureSocket socket) Connected(ClientVersion version = ClientVersion.CV_70130)
        {
            var client = new NetClient();
            client.Load(version, EncryptionType.NONE);
            var socket = new CaptureSocket();
            client.AttachSocket(socket);
            client.EndHandshake(client.BeginGameHandshake(Seed));
            client.Flush();
            socket.Bytes.Clear();

            return (client, socket);
        }

        private static World PlayerWorld()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new System.Collections.Generic.List<ClassicUO.Assets.SkillEntry>());

            return world;
        }

        private sealed record Case(string Name, Action<NetClient, World> Send, string Hex);

        private static Case C(string name, Action<NetClient, World> send, string hex = "") => new(name, send, hex);

        private static readonly string Long = new string('x', 240);
        private const string Uni = "héllo 世界";
        private const string Cp = "café €";

        private static readonly Case[] Cases =
        {
            // main
            C("ACKTalk", (s, _) => s.Send_ACKTalk(), "0300282000340003DB13143F452C580F5D442E5011DF755CE03E714F3134054E181E720F59ADF500"),
            C("Ping_0", (s, _) => s.Send_Ping(0), "7300"),
            C("Ping_FF", (s, _) => s.Send_Ping(0xFF), "73FF"),
            C("ClientVersion", (s, _) => s.Send_ClientVersion("7.0.13.0"), "BD000C372E302E31332E3000"),
            C("ClientVersion_empty", (s, _) => s.Send_ClientVersion(""), "BD000400"),
            C("LogoutNotification", (s, _) => s.Send_LogoutNotification(), "D100"),
            C("GameWindowSize", (s, _) => s.Send_GameWindowSize(800, 600), "BF000D00050000032000000258"),
            C("RazorACK", (s, _) => s.Send_RazorACK(), "F00004FF"),
            C("Language", (s, _) => s.Send_Language("ENU"), "BF0009000B454E5500"),
            C("Resync", (s, _) => s.Send_Resync(), "220000"),
            C("ClientViewRange_5", (s, _) => s.Send_ClientViewRange(5), "C805"),
            C("ClientViewRange_24", (s, _) => s.Send_ClientViewRange(24), "C818"),
            C("OpenUOStore", (s, _) => s.Send_OpenUOStore(), "FA0003"),
            C("UOLive_HashResponse", (s, _) => s.Send_UOLive_HashResponse(0x01020304, 1, new ushort[] { 1, 0xFFFF, 0x1234 }), "3F001501020304000000000000FF010001FFFF1234"),
            C("UOLive_HashResponse_none", (s, _) => s.Send_UOLive_HashResponse(7, 0, Span<ushort>.Empty), "3F000F00000007000000000000FF00"),
            // login
            C("Seed", (s, _) => s.Send_Seed(0xC0A80001, 7, 0, 13, 0), "EFC0A8000100000007000000000000000D00000000"),
            C("Seed_Old", (s, _) => s.Send_Seed_Old(0xC0A80001), "C0A80001"),
            C("FirstLogin", (s, _) => s.Send_FirstLogin("user", "secret"), "80757365720000000000000000000000000000000000000000000000000000736563726574000000000000000000000000000000000000000000000000FF"),
            C("FirstLogin_max", (s, _) => s.Send_FirstLogin(new string('u', 40), new string('p', 40)), "80757575757575757575757575757575757575757575757575757575757575707070707070707070707070707070707070707070707070707070707070FF"),
            C("SelectServer", (s, _) => s.Send_SelectServer(3), "A00003"),
            C("SecondLogin", (s, _) => s.Send_SecondLogin("user", "secret", 0x11223344), "9111223344757365720000000000000000000000000000000000000000000000000000736563726574000000000000000000000000000000000000000000000000"),
            C("DeleteCharacter", (s, _) => s.Send_DeleteCharacter(2, 0x7F000001), "83000000000000000000000000000000000000000000000000000000000000000000027F000001"),
            // items
            C("DoubleClick", (s, _) => s.Send_DoubleClick(0x40000001), "0640000001"),
            C("DoubleClick_paperdoll", (s, _) => s.Send_DoubleClick(0x80000000 | PLAYER), "0680001234"),
            C("PickUpRequest", (s, _) => s.Send_PickUpRequest(0x40000001, 60000), "0740000001EA60"),
            C("DropRequest_Old", (s, _) => s.Send_DropRequest_Old(0x40000001, 100, 200, -5, 0xFFFFFFFF), "0840000001006400C8FBFFFFFFFF00"),
            C("DropRequest", (s, _) => s.Send_DropRequest(0x40000001, 0xFFFF, 0xFFFF, 0, 0, 0x40000002), "0840000001FFFFFFFF000040000002"),
            C("EquipRequest", (s, _) => s.Send_EquipRequest(0x40000001, Layer.OneHanded, PLAYER), "13400000010100001234"),
            C("ClickRequest", (s, _) => s.Send_ClickRequest(0x40000001), "0940000001"),
            C("RenameRequest", (s, _) => s.Send_RenameRequest(0x00000101, "Rex"), "7500000101526578000000000000000000000000000000000000000000000000000000"),
            C("RenameRequest_long", (s, _) => s.Send_RenameRequest(0x00000101, Long), "7500000101787878787878787878787878787878787878787878787878787878787878"),
            C("NameRequest", (s, _) => s.Send_NameRequest(0x00000101), "98000700000101"),
            C("EquipLastWeapon", (s, w) => s.Send_EquipLastWeapon(w), "D7000A00001234001E0A"),
            C("MegaClilocRequest_Old", (s, _) => s.Send_MegaClilocRequest_Old(0x40000001), "BF0009001040000001"),
            C("MegaClilocRequest_1", (s, _) => s.Send_MegaClilocRequest(new uint[] { 0x40000001 }), "D6000740000001"),
            C("MegaClilocRequest_3", (s, _) => s.Send_MegaClilocRequest(new uint[] { 1, 0x40000001, 0xFFFFFFFF }), "D6000F0000000140000001FFFFFFFF"),
            C("ShowPublicHouseContent_on", (s, _) => s.Send_ShowPublicHouseContent(true), "FB000401"),
            C("ShowPublicHouseContent_off", (s, _) => s.Send_ShowPublicHouseContent(false), "FB000400"),
            C("EquipMacroKR", (s, _) => s.Send_EquipMacroKR(new uint[] { 0x40000001, 0x40000002 }), "EC000C024000000140000002"),
            C("EquipMacroKR_none", (s, _) => s.Send_EquipMacroKR(ReadOnlySpan<uint>.Empty), "EC000400"),
            C("UnequipMacroKR", (s, _) => s.Send_UnequipMacroKR(new[] { Layer.OneHanded, Layer.Helmet }), "ED00080200010006"),
            // mobiles
            C("ChangeWarMode_on", (s, _) => s.Send_ChangeWarMode(true), "7201320000"),
            C("ChangeWarMode_off", (s, _) => s.Send_ChangeWarMode(false), "7200320000"),
            C("HelpRequest", (s, _) => s.Send_HelpRequest(), "9B0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
            C("StatusRequest", (s, _) => s.Send_StatusRequest(PLAYER), "34EDEDEDED0400001234"),
            C("SkillsRequest", (s, _) => s.Send_SkillsRequest(PLAYER), "34EDEDEDED0500001234"),
            C("SkillsStatusRequest", (s, _) => s.Send_SkillsStatusRequest(57, 2), "3A0006003902"),
            C("AttackRequest", (s, _) => s.Send_AttackRequest(0x00000200), "0500000200"),
            C("CastSpellFromBook", (s, _) => s.Send_CastSpellFromBook(42, 0x40000003), "120012273432203130373337343138323700"),
            C("UseSkill", (s, _) => s.Send_UseSkill(21), "120009243231203000"),
            C("UseSkill_0", (s, _) => s.Send_UseSkill(0), "1200082430203000"),
            C("OpenDoor", (s, _) => s.Send_OpenDoor(), "1200055800"),
            C("OpenSpellBook", (s, _) => s.Send_OpenSpellBook(1), "1200054301"),
            C("EmoteAction", (s, _) => s.Send_EmoteAction("bow"), "120008C7626F7700"),
            C("InvokeVirtueRequest", (s, _) => s.Send_InvokeVirtueRequest(2), "120006F43200"),
            C("StatLockStateRequest", (s, _) => s.Send_StatLockStateRequest(1, Lock.Locked), "BF0007001A0102"),
            C("SkillStatusChangeRequest", (s, _) => s.Send_SkillStatusChangeRequest(5, 1), "3A0006000501"),
            C("UseCombatAbility", (s, w) => s.Send_UseCombatAbility(w, 3), "D7000F00001234001900000000030A"),
            C("ToggleGargoyleFlying", (s, _) => s.Send_ToggleGargoyleFlying(), "BF000B0032000100000000"),
            C("StunRequest", (s, _) => s.Send_StunRequest(), "BF00050009"),
            C("DisarmRequest", (s, _) => s.Send_DisarmRequest(), "BF0005000A"),
            C("ChangeRaceRequest", (s, _) => s.Send_ChangeRaceRequest(0x83EA, 0x203B, 0x44E, 0, 0), "BF000F002A83EA203B044E00000000"),
            C("MultiBoatMoveRequest", (s, _) => s.Send_MultiBoatMoveRequest(0x40000009, Direction.East, 2), "BF000C003340000009020202"),
            C("WalkRequest", (s, _) => s.Send_WalkRequest(Direction.North, 0, false, 0), "02000000000000"),
            C("WalkRequest_run", (s, _) => s.Send_WalkRequest(Direction.Up, 255, true, 0xDEADBEEF), "0287FFDEADBEEF"),
            C("DeathScreen", (s, _) => s.Send_DeathScreen(), "2C02"),
            // speech, party, chat
            C("PartyInviteRequest", (s, _) => s.Send_PartyInviteRequest(), "BF000A00060100000000"),
            C("PartyRemoveRequest", (s, _) => s.Send_PartyRemoveRequest(0x00000200), "BF000A00060200000200"),
            C("PartyChangeLootTypeRequest", (s, _) => s.Send_PartyChangeLootTypeRequest(true), "BF000700060601"),
            C("PartyAccept", (s, _) => s.Send_PartyAccept(0x00000200), "BF000A00060800000200"),
            C("PartyDecline", (s, _) => s.Send_PartyDecline(0x00000200), "BF000A00060900000200"),
            C("PartyMessage_all", (s, _) => s.Send_PartyMessage("hi all", 0), "BF00140006040068006900200061006C006C0000"),
            C("PartyMessage_one", (s, _) => s.Send_PartyMessage(Uni, 0x00000200), "BF001C00060300000200006800E9006C006C006F00204E16754C0000"),
            C("PartyMessage_empty", (s, _) => s.Send_PartyMessage("", 0), "BF00080006040000"),
            C("OpenChat", (s, _) => s.Send_OpenChat("Bob"), "B5000042006F00620000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
            // gumps, menus
            C("GumpResponse", (s, _) => s.Send_GumpResponse(1, 0x12345678, 7, new uint[] { 1, 5 }, new[] { Tuple.Create((ushort)3, "text"), Tuple.Create((ushort)4, Uni) }), "B1003F0000000112345678000000070000000200000001000000050000000200030004007400650078007400040008006800E9006C006C006F00204E16754C"),
            C("GumpResponse_none", (s, _) => s.Send_GumpResponse(1, 2, 0, Array.Empty<uint>(), Array.Empty<Tuple<ushort, string>>()), "B100170000000100000002000000000000000000000000"),
            C("VirtueGumpResponse", (s, _) => s.Send_VirtueGumpResponse(PLAYER, 0x6A), "B1000F00001234000001CD0000006A"),
            C("MenuResponse", (s, _) => s.Send_MenuResponse(0x40000001, 0x1234, 2, 0x0EED, 0x0481), "7D40000001123400020EED0481"),
            C("GrayMenuResponse", (s, _) => s.Send_GrayMenuResponse(0x40000001, 0x1234, 1), "7D400000011234000100000000"),
            C("TextEntryDialogResponse", (s, _) => s.Send_TextEntryDialogResponse(0x40000001, 2, 1, "42", true), "AC000F400000010201010003343200"),
            C("TextEntryDialogResponse_cancel", (s, _) => s.Send_TextEntryDialogResponse(0x40000001, 2, 0, "", false), "AC000D40000001020000000100"),
            C("TipRequest", (s, _) => s.Send_TipRequest(5, 1), "A7000501"),
            C("DyeDataResponse", (s, _) => s.Send_DyeDataResponse(0x40000001, 0x0FAB, 0x0481), "954000000100000481"),
            C("ProfileRequest", (s, _) => s.Send_ProfileRequest(0x00000200), "B800080000000200"),
            C("ProfileUpdate", (s, _) => s.Send_ProfileUpdate(PLAYER, "about me"), "B8001C01000012340001000800610062006F007500740020006D0065"),
            C("ProfileUpdate_uni", (s, _) => s.Send_ProfileUpdate(PLAYER, Uni), "B8001C010000123400010008006800E9006C006C006F00204E16754C"),
            C("ClickQuestArrow_right", (s, _) => s.Send_ClickQuestArrow(true), "BF0006000701"),
            C("ClickQuestArrow_left", (s, _) => s.Send_ClickQuestArrow(false), "BF0006000700"),
            C("CloseStatusBarGump", (s, _) => s.Send_CloseStatusBarGump(0x00000200), "BF0009000C00000200"),
            C("RequestPopupMenu", (s, _) => s.Send_RequestPopupMenu(0x00000200), "BF0009001300000200"),
            C("PopupMenuSelection", (s, _) => s.Send_PopupMenuSelection(0x00000200, 0x000A), "BF000B001500000200000A"),
            C("MapMessage", (s, _) => s.Send_MapMessage(0x40000001, 1, 0, 100, 200), "56400000010100006400C8"),
            C("GuildMenuRequest", (s, w) => s.Send_GuildMenuRequest(w), "D7000A0000123400280A"),
            C("QuestMenuRequest", (s, w) => s.Send_QuestMenuRequest(w), "D7000A00001234003200"),
            // books, bulletin boards
            C("BulletinBoardRequestMessage", (s, _) => s.Send_BulletinBoardRequestMessage(0x40000001, 0x40000002), "71000C034000000140000002"),
            C("BulletinBoardRequestMessageSummary", (s, _) => s.Send_BulletinBoardRequestMessageSummary(0x40000001, 0x40000002), "71000C044000000140000002"),
            C("BulletinBoardPostMessage", (s, _) => s.Send_BulletinBoardPostMessage(0x40000001, 0, "Subject", "line one\nline two"), "71002A054000000100000000085375626A6563740002096C696E65206F6E6500096C696E652074776F00"),
            C("BulletinBoardPostMessage_reply_empty", (s, _) => s.Send_BulletinBoardPostMessage(0x40000001, 0x40000002, "", ""), "7100110540000001400000020100010100"),
            C("BulletinBoardRemoveMessage", (s, _) => s.Send_BulletinBoardRemoveMessage(0x40000001, 0x40000002), "71000C064000000140000002"),
            C("BookHeaderChanged_Old", (s, _) => s.Send_BookHeaderChanged_Old(0x40000001, "Title", "Author"), "9340000001000100005469746C6500000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000417574686F72000000000000000000000000000000000000000000000000"),
            C("BookHeaderChanged", (s, _) => s.Send_BookHeaderChanged(0x40000001, "Tîtle", "Au"), "D400174000000100000000000654C3AE746C6500024175"),
            C("BookHeaderChanged_empty", (s, _) => s.Send_BookHeaderChanged(0x40000001, "", ""), "D4000F400000010000000000000000"),
            C("BookPageData", (s, _) => s.Send_BookPageData(0x40000001, new[] { "one", "", Cp }, 2), "66001D400000010001000200036F6E650000636166C3A920E282AC0000"),
            C("BookPageData_none", (s, _) => s.Send_BookPageData(0x40000001, Array.Empty<string>(), 1), "66000E4000000100010001000000"),
            C("BookPageDataRequest", (s, _) => s.Send_BookPageDataRequest(0x40000001, 3), "66000D4000000100010003FFFF"),
            // targeting, trade, vendors
            C("TradeResponse", (s, _) => s.Send_TradeResponse(0x40000001, 2, true), "6F000C024000000100000001"),
            C("TradeResponse_cancel", (s, _) => s.Send_TradeResponse(0x40000001, 1, false), "6F00080140000001"),
            C("TradeUpdateGold", (s, _) => s.Send_TradeUpdateGold(0x40000001, 1000, 2), "6F00100340000001000003E800000002"),
            C("TargetObject", (s, _) => s.Send_TargetObject(0x40000001, 0x0EED, 100, 200, -3, 0x00000011, 1), "6C00000000110140000001006400C8FFFD0EED"),
            C("TargetXYZ", (s, _) => s.Send_TargetXYZ(0x0EED, 100, 200, 127, 0x00000011, 0), "6C01000000110000000000006400C8007F0EED"),
            C("TargetCancel", (s, _) => s.Send_TargetCancel(CursorTarget.Object, 0x00000011, 3), "6C00000000110300000000FFFFFFFF00000000"),
            C("QueryGuildPosition", (s, _) => s.Send_QueryGuildPosition(), "F000050101"),
            C("QueryPartyPosition", (s, _) => s.Send_QueryPartyPosition(), "F0000400"),
            C("BuyRequest", (s, _) => s.Send_BuyRequest(0x00000300, new[] { Tuple.Create(0x40000001u, (ushort)5), Tuple.Create(0x40000002u, (ushort)1) }), "3B001600000300021A4000000100051A400000020001"),
            C("BuyRequest_none", (s, _) => s.Send_BuyRequest(0x00000300, Array.Empty<Tuple<uint, ushort>>()), "3B00080000030000"),
            C("SellRequest", (s, _) => s.Send_SellRequest(0x00000300, new[] { Tuple.Create(0x40000001u, (ushort)60000) }), "9F000F00000300000140000001EA60"),
            C("TargetSelectedObject", (s, _) => s.Send_TargetSelectedObject(0x40000001, 0x40000002), "BF000D002C4000000140000002"),
            // houses
            C("CustomHouseDataRequest", (s, _) => s.Send_CustomHouseDataRequest(0x40000010), "BF0009001E40000010"),
            C("CustomHouseBackup", (s, w) => s.Send_CustomHouseBackup(w), "D7000A0000123400020A"),
            C("CustomHouseRestore", (s, w) => s.Send_CustomHouseRestore(w), "D7000A0000123400030A"),
            C("CustomHouseCommit", (s, w) => s.Send_CustomHouseCommit(w), "D7000A0000123400040A"),
            C("CustomHouseBuildingExit", (s, w) => s.Send_CustomHouseBuildingExit(w), "D7000A00001234000C0A"),
            C("CustomHouseGoToFloor", (s, w) => s.Send_CustomHouseGoToFloor(w, 2), "D7000F00001234001200000000020A"),
            C("CustomHouseSync", (s, w) => s.Send_CustomHouseSync(w), "D7000A00001234000E0A"),
            C("CustomHouseClear", (s, w) => s.Send_CustomHouseClear(w), "D7000A0000123400100A"),
            C("CustomHouseRevert", (s, w) => s.Send_CustomHouseRevert(w), "D7000A00001234001A0A"),
            C("CustomHouseResponse", (s, w) => s.Send_CustomHouseResponse(w), "D7000A00001234000A0A"),
            C("CustomHouseAddItem", (s, w) => s.Send_CustomHouseAddItem(w, 0x0064, 3, -2), "D700190000123400060000000064000000000300FFFFFFFE0A"),
            C("CustomHouseDeleteItem", (s, w) => s.Send_CustomHouseDeleteItem(w, 0x0064, 3, 4, 7), "D7001E00001234000500000000640000000003000000000400000000070A"),
            C("CustomHouseAddRoof", (s, w) => s.Send_CustomHouseAddRoof(w, 0x0065, 1, 2, 27), "D7001E000012340013000000006500000000010000000002000000001B0A"),
            C("CustomHouseDeleteRoof", (s, w) => s.Send_CustomHouseDeleteRoof(w, 0x0065, 1, 2, 27), "D7001E000012340014000000006500000000010000000002000000001B0A"),
            C("CustomHouseAddStair", (s, w) => s.Send_CustomHouseAddStair(w, 0x0066, 0, 0), "D7001900001234000D0000000066000000000000000000000A"),
        };

        internal static readonly SpanBufferMode[] Modes =
#if CUO_DEVTOOLS
            { SpanBufferMode.Old, SpanBufferMode.New, SpanBufferMode.CheckOld, SpanBufferMode.CheckNew };
#else
            { SpanBufferMode.Old, SpanBufferMode.New };
#endif

        public static IEnumerable<object[]> CaseModes() =>
            from c in Cases from m in Modes select new object[] { c.Name, m };

        private static string Build(Case c)
        {
            var (client, socket) = Connected();
            c.Send(client, PlayerWorld());
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
            hex.Length.Should().BeGreaterThan(0);
            int length = Convert.FromHexString(hex).Length;
            var table = new PacketsTable(ClientVersion.CV_70130);
            int fixedLength = table.GetPacketLength(Convert.FromHexString(hex)[0]);

            if (name == "Seed_Old")
            {
                length.Should().Be(4, "the old login seed is a bare 4-byte value, not a packet");
            }
            else if (fixedLength > 0)
            {
                length.Should().Be(fixedLength, "a fixed-length packet is exactly its table length");
            }
            else
            {
                byte[] b = Convert.FromHexString(hex);
                ((b[1] << 8) | b[2]).Should().Be(length, "a variable-length packet's length field is its size");
            }
#if CUO_DEVTOOLS
            SpanBuffers.WriterMismatches.Should().Be(before);
#endif
        }

        [Fact]
        public void Every_Case_Name_Is_Unique() => Cases.Select(c => c.Name).Should().OnlyHaveUniqueItems();

        [Fact]
        public void Outgoing_Golden_Dump()
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
