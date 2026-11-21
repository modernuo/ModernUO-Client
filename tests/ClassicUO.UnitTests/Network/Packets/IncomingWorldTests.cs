using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Incoming packets that change the player's world (PacketHandlers.World / Items / Mobiles) on an in-game World (a map,
    // a player), through the whole receive path in every read mode, compressed or not, span_reader off and on.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingWorldTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint ITEM = 0x4000_0100;

        public IncomingWorldTests()
        {
            typeof(ProfileManager).GetProperty(nameof(ProfileManager.CurrentProfile))!.SetValue(null, new Profile());
        }

        public void Dispose() => SpanBuffers.ModeOverride = null;

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private static World InGame()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new System.Collections.Generic.List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);

            return world;
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ClientViewRange_0xC8(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0xC8, false).U8(5).Build(), new PacketBuilder(0xC8, false).U8(24).Build())
                .Packets.Should().Be(2);

            world.ClientViewRange.Should().Be(24);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Warmode_0x72_On_Then_Off(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] on = new PacketBuilder(0x72, false).U8(1).U8(0).U8(0x32).U8(0).Build();

            PacketPipe.Deliver(world, mode, read, compressed, on);
            world.Player.InWarMode.Should().BeTrue();

            byte[] off = new PacketBuilder(0x72, false).U8(0).U8(0).U8(0x32).U8(0).Build();
            PacketPipe.Deliver(world, mode, read, compressed, off);
            world.Player.InWarMode.Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void EquipItem_0x2E_Puts_The_Item_On_The_Mobile(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0x2E, false).U32(ITEM).U16(0x13B9).I8(0).U8((byte)Layer.OneHanded).U32(MOB).U16(0x0481).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            Item item = world.Items.Get(ITEM);
            item.Should().NotBeNull();
            item.Graphic.Should().Be(0x13B9);
            item.Layer.Should().Be(Layer.OneHanded);
            item.Container.Should().Be(MOB);
            item.Hue.Should().Be(0x0481);
            world.Mobiles.Get(MOB).FindItemByLayer(Layer.OneHanded).Should().BeSameAs(item);
        }

        // span_reader on only: the reads end before UpdateGameObject, which needs Client.Game (not in tests)
        public static IEnumerable<object[]> NewModes() => PacketPipe.Modes().Where(m => (SpanBufferMode)m[0] == SpanBufferMode.New);

        [Theory]
        [MemberData(nameof(NewModes))]
        public void UpdateItem_0x1A_Cut_Before_A_Flagged_Field(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            // the hue bit is set but the packet ends before the hue and flags
            byte[] cut = new PacketBuilder(0x1A, true).U32(ITEM).U16(0x0EED).U16(100).U16(0x8000 | 0x4000 | 200).I8(5).U16(0x0481).U8(0).Build(cut: 3);

            var r = PacketPipe.Deliver(world, mode, read, compressed, cut);

            r.Disconnects.Should().Be(1, "span_reader: a read past the end ends the connection");
            r.Reason.Should().Contain("0x1A");
            world.Items.Get(ITEM).Should().BeNull();
        }

    }
}
