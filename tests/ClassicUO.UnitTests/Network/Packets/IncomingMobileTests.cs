using System;
using System.Reflection;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Incoming mobile and item state packets (PacketHandlers.Mobiles / Items): built as the server sends them, delivered through
    // the whole receive path in every read mode, compressed or not, with span_reader off and on; the effect is read back
    // from the world (the ECS record behind the properties).
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingMobileTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint ITEM = 0x4000_0100;


        public void Dispose() => SpanBuffers.ModeOverride = null;

        public static System.Collections.Generic.IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private static World WorldWithMobile()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new System.Collections.Generic.List<ClassicUO.Assets.SkillEntry>());
            world.GetOrCreateMobile(MOB);

            return world;
        }

        private static byte[] Stat(byte id, uint serial, int max, int cur) =>
            new PacketBuilder(id, false).U32(serial).U16(max).U16(cur).Build();

        [Theory]
        [MemberData(nameof(Modes))]
        public void Hits_Mana_Stamina_0xA1_0xA2_0xA3(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WorldWithMobile();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Stat(0xA1, MOB, 100, 37), Stat(0xA2, MOB, 0xFFFF, 0), Stat(0xA3, MOB, 1, 1),
                Stat(0xA1, 0x0000_0999, 50, 50)); // unknown serial: ignored

            r.Packets.Should().Be(4);
            r.Disconnects.Should().Be(0);
            Mobile m = world.Mobiles.Get(MOB);
            m.HitsMax.Should().Be(100);
            m.Hits.Should().Be(37);
            m.ManaMax.Should().Be(0xFFFF);
            m.Mana.Should().Be(0);
            m.StaminaMax.Should().Be(1);
            m.Stamina.Should().Be(1);
            world.Get(0x0000_0999).Should().BeNull();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void MobileAttributes_0x2D_Sets_All_Three(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WorldWithMobile();
            byte[] p = new PacketBuilder(0x2D, false).U32(MOB).U16(90).U16(80).U16(70).U16(60).U16(50).U16(40).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            Mobile m = world.Mobiles.Get(MOB);
            (m.HitsMax, m.Hits, m.ManaMax, m.Mana, m.StaminaMax, m.Stamina).Should().Be(((ushort)90, (ushort)80, (ushort)70, (ushort)60, (ushort)50, (ushort)40));
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void DeleteObject_0x1D_Removes_A_World_Item_And_Keeps_The_Player(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WorldWithMobile();
            world.GetOrCreateItem(ITEM);
            world.Get(ITEM).Should().NotBeNull();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x1D, false).U32(ITEM).Build(),
                new PacketBuilder(0x1D, false).U32(PLAYER).Build(), // the player is never deleted
                new PacketBuilder(0x1D, false).U32(0x4000_7777).Build()); // unknown: ignored

            r.Packets.Should().Be(3);
            world.Get(ITEM).Should().BeNull();
            world.Get(PLAYER).Should().NotBeNull();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void A_Fixed_Packet_Cut_At_The_Stream_End_Is_Never_Dispatched(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WorldWithMobile();
            byte[] whole = Stat(0xA1, MOB, 100, 37);

            var r = PacketPipe.Deliver(world, mode, read, compressed, whole.AsSpan(0, whole.Length - 1).ToArray());

            r.Packets.Should().Be(0);
            r.Disconnects.Should().Be(0);
            world.Mobiles.Get(MOB).HitsMax.Should().Be(0);
        }
    }
}
