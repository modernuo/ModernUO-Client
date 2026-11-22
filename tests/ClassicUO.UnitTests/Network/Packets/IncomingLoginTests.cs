using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Login packets (0xA8, 0xA9, 0x86, 0x82, 0x53, 0x85, 0xFD) on a LoginScene that is current but not loaded (GameSeam +
    // SetSceneForTests: no UI, no music), the whole receive path, every read mode, compressed or not, span_reader off and on.
    // Not here: 0x8C (the relay connects a real socket), 0x1B (IncomingSceneTests), 0x55 (it loads a GameScene).
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingLoginTests : IDisposable
    {
        private readonly GameSeam _seam = new();

        public IncomingLoginTests()
        {
            // the last-character list, empty, so the 0xA9 handler does not create lastcharacter.json next to the test binary
            var prop = typeof(LastCharacterManager).GetProperty("LastCharacters", BindingFlags.NonPublic | BindingFlags.Static)!;
            prop.SetValue(null, Activator.CreateInstance(prop.PropertyType));
        }

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            _seam.Dispose();
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private (World world, LoginScene scene) AtLogin()
        {
            var world = new World();
            var scene = new LoginScene(world);
            _seam.Game.SetSceneForTests(scene);

            return (world, scene);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerList_0xA8_Two_Servers(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();
            byte[] p = new PacketBuilder(0xA8, true).U8(0x5D).U16(2)
                .U16(0).Ascii("ModernUO", 32).U8(10).U8(0xF8).U32(0x7F000001)
                .U16(1).Ascii("Test Center", 32).U8(100).U8(0).U32(0x0A000002).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            scene.Servers.Should().HaveCount(2);
            scene.Servers[0].Name.Should().Be("ModernUO");
            scene.Servers[0].PercentFull.Should().Be(10);
            scene.Servers[0].Address.Should().Be(0x7F000001u);
            scene.Servers[1].Index.Should().Be(1);
            scene.Servers[1].Name.Should().Be("Test Center");
            scene.CurrentLoginStep.Should().Be(LoginSteps.ServerSelection);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerList_0xA8_Empty(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0xA8, true).U8(0).U16(0).Build());

            r.Disconnects.Should().Be(0);
            scene.Servers.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Rejection_0x82_0x53_0x85_Set_The_Popup(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            foreach (byte id in new byte[] { 0x82, 0x53, 0x85 })
            {
                var (world, scene) = AtLogin();

                var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(id, false).U8(3).Build());

                r.Packets.Should().Be(1);
                r.Disconnects.Should().Be(0);
                scene.PopupMessage.Should().Be(ServerErrorMessages.GetError(id, 3), $"0x{id:X2}");
                scene.CurrentLoginStep.Should().Be(LoginSteps.PopUpMessage);
            }
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Rejection_0x85_Code_Past_The_Table_Uses_The_Last_Entry(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();

            PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x85, false).U8(0xFF).Build()).Disconnects.Should().Be(0);

            scene.PopupMessage.Should().Be(ServerErrorMessages.GetError(0x85, 5));
        }

        private static PacketBuilder CharList(params string[] names)
        {
            var b = new PacketBuilder(0xA9, true).U8(names.Length);

            foreach (string n in names)
            {
                b.Ascii(n, 30).Zero(30);
            }

            return b;
        }

        // 7.0.13 cities: index, name, building, x, y, z, map, description cliloc, 4 unused bytes
        private static PacketBuilder City(PacketBuilder b, int index, string name, string building, int x, int y, int z, int map, uint cliloc) =>
            b.U8(index).Ascii(name, 32).Ascii(building, 32).U32((uint)x).U32((uint)y).U32((uint)z).U32((uint)map).U32(cliloc).Zero(4);

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterList_0xA9_Characters_Cities_And_Flags(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();
            PacketBuilder b = CharList("Avatar", "", "Blackthorn", "", "", "").U8(2);
            City(b, 0, "Britain", "Sweet Dreams Inn", 1496, 1628, 10, 1, 1075074);
            City(b, 1, "Moonglow", "The Scholars Inn", 4408, 1168, 0, 1, 1075075);
            b.U32((uint)(CharacterListFlags.CLF_CONTEXT_MENU | CharacterListFlags.CLF_6_CHARACTER_SLOT));

            var r = PacketPipe.Deliver(world, mode, read, compressed, b.Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            scene.Characters.Should().Equal("Avatar", "", "Blackthorn", "", "", "");
            scene.Cities.Should().HaveCount(2);
            scene.Cities[1].City.Should().Be("Moonglow");
            scene.Cities[1].X.Should().Be(4408);
            scene.Cities[0].Building.Should().Be("Sweet Dreams Inn");
            world.ClientFeatures.Flags.Should().HaveFlag(CharacterListFlags.CLF_CONTEXT_MENU);
            world.ClientFeatures.Flags.Should().HaveFlag(CharacterListFlags.CLF_6_CHARACTER_SLOT);
            scene.CurrentLoginStep.Should().Be(LoginSteps.CharacterSelection);
            r.Sent.Should().BeEmpty("no autologin: the client waits for a pick");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterList_0xA9_No_Characters_Starts_Creation(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();
            PacketBuilder b = CharList("", "", "", "", "").U8(1);
            City(b, 0, "Britain", "Inn", 1, 2, 3, 0, 0);
            b.U32(0);

            var r = PacketPipe.Deliver(world, mode, read, compressed, b.Build());

            r.Disconnects.Should().Be(0);
            scene.CurrentLoginStep.Should().Be(LoginSteps.CharacterCreation);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void CharacterListUpdate_0x86_Replaces_The_Names_And_Shows_The_Selection(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();
            scene.PopupMessage = "stale";

            byte[] update = CharList("Avatar", "", "Blackthorn", "", "").Build();
            update[0] = 0x86; // the same body as 0xA9's character part

            var r = PacketPipe.Deliver(world, mode, read, compressed, update, update);

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            scene.Characters.Should().Equal("Avatar", "", "Blackthorn", "", "");
            scene.CurrentLoginStep.Should().Be(LoginSteps.CharacterSelection);
            scene.PopupMessage.Should().BeNull("a popup is dropped unless the popup step is current");
            UIManager.Gumps.OfType<ClassicUO.Game.UI.Gumps.Login.CharacterSelectionGump>().Should()
                .ContainSingle(g => !g.IsDisposed, "the second update replaces the first selection gump");
            r.Sent.Should().BeEmpty();
        }

        // 0xFD exists from 7.0.104 (2 bytes); the seam runs that version here, so the pipe frames it with that table
        [Theory]
        [MemberData(nameof(Modes))]
        public void LoginDelay_0xFD_Feeds_The_Next_Rejection(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            GameSeam.UseVersion("7.0.104.0");
            var (world, scene) = AtLogin();

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0xFD, false).U8(3).Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0, r.Reason);
            scene.LoginDelay.Should().Be((20, 30));

            r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x53, false).U8(13).Build());

            r.Disconnects.Should().Be(0, r.Reason);
            scene.PopupMessage.Should().Be(ServerErrorMessages.GetError(0x53, 13, (20, 30)), "code 13 names the delay");
            scene.LoginDelay.Should().Be(default((int, int)), "a rejection uses the delay once");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Login_Packets_In_Game_Are_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var (world, scene) = AtLogin();
            world.CreatePlayer(OutgoingPacketTests.PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0xA8, true).U8(0).U16(0).Build(), new PacketBuilder(0x82, false).U8(1).Build());

            r.Packets.Should().Be(2);
            scene.Servers.Should().BeNull();
            scene.PopupMessage.Should().BeNull();
        }
    }
}
