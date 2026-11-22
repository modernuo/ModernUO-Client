using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // G1: a server-gump resend rebuilds the open gump also without a position-cache entry and starts the gump's flags over;
    // a vendor without a shop layer opens its shop instead of throwing (a throw disconnects with span_reader on).
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class ServerGumpReplaceTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint GUMP_ID = 0x0123_4567;

        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            UIManager.RemovePosition(GUMP_ID);
            _seam.Dispose();
        }

        private static World InGame()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);

            return world;
        }

        // ---- G1 the replace fix

        [Fact]
        public void Resend_Replaces_The_Open_Gump_Without_A_Position_Cache_Entry()
        {
            World world = InGame();

            Gump first = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 150, "{ text 1 2 0 0 }", new[] { "a" });
            first.X = 300;
            // a closed gump with the same id removed the entry (Gump.OnButtonClick on a nomove gump, 0xBF 0x04)
            UIManager.RemovePosition(GUMP_ID);

            Gump again = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 150, "{ text 1 2 0 0 }", new[] { "b" });

            again.Should().BeSameAs(first);
            UIManager.Gumps.Count(g => !g.IsDisposed).Should().Be(1);
            again.X.Should().Be(300);
            UIManager.GetGumpCachePosition(GUMP_ID, out var pos).Should().BeTrue();
            pos.X.Should().Be(300, "the open gump's place is cached again");
        }

        [Fact]
        public void Resend_Starts_The_Gump_Flags_Over()
        {
            World world = InGame();

            Gump first = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 0, 0, "{ nomove }{ noclose }{ nodispose }{ mastergump 77 }", Array.Empty<string>());
            first.CanMove.Should().BeFalse();
            first.CanCloseWithRightClick.Should().BeFalse();
            first.CanCloseWithEsc.Should().BeFalse();
            first.MasterGumpSerial.Should().Be(77);

            Gump again = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 0, 0, "{ page 0 }", Array.Empty<string>());

            again.Should().BeSameAs(first);
            again.CanMove.Should().BeTrue("a rebuild equals a fresh build");
            again.CanCloseWithRightClick.Should().BeTrue();
            again.CanCloseWithEsc.Should().BeTrue();
            again.MasterGumpSerial.Should().Be(0);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerGump_0xB0_Replace_Without_A_Position_Cache_Entry(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            static byte[] Packet(string layout, string line)
            {
                var b = new PacketBuilder(0xB0, true).U32(MOB).U32(GUMP_ID).U32(10).U32(20).U16(layout.Length + 1).Ascii(layout).U16(1);

                return b.U16(line.Length).UnicodeBE(line, false).Build();
            }

            var r = PacketPipe.Deliver(world, mode, read, compressed, Packet("{ text 1 2 0 0 }", "first"));
            r.Disconnects.Should().Be(0, r.Reason);
            Gump first = UIManager.Gumps.Single(g => !g.IsDisposed);
            UIManager.RemovePosition(GUMP_ID);

            r = PacketPipe.Deliver(world, mode, read, compressed, Packet("{ text 1 2 0 0 }", "second"));
            r.Disconnects.Should().Be(0, r.Reason);

            UIManager.Gumps.Where(g => !g.IsDisposed).Should().ContainSingle().Which.Should().BeSameAs(first);
            first.Children.OfType<Label>().Single(c => !c.IsDisposed).Text.Should().Be("second");
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        // ---- G1 vendor without shop layers

        private static byte[] Equip(uint serial, int graphic, Layer layer, uint parent) =>
            new PacketBuilder(0x2E, false).U32(serial).U16(graphic).I8(0).U8((int)layer).U32(parent).U16(0).Build();

        [Theory]
        [MemberData(nameof(Modes))]
        public void Vendor_0x24_0x30_Without_Shop_Layers_Opens_An_Empty_Shop(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] open = new PacketBuilder(0x24, false).U32(MOB).U16(0x0030).U16(0x007D).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, open, new PacketBuilder(0xC8, false).U8(7).Build());

            r.Disconnects.Should().Be(0, r.Reason);
            r.Packets.Should().Be(2);
            world.ClientViewRange.Should().Be(7);
            UIManager.Gumps.OfType<ShopGump>().Should().ContainSingle(g => !g.IsDisposed).Which.LocalSerial.Should().Be(MOB);
        }

        [Fact]
        public void Vendor_With_Only_The_Buy_Layer_Opens()
        {
            World world = InGame();
            var r = PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false,
                Equip(0x4000_0111, 0x2AF8, Layer.ShopBuy, MOB),
                new PacketBuilder(0x24, false).U32(MOB).U16(0x0030).U16(0x007D).Build());

            r.Disconnects.Should().Be(0, r.Reason);
            UIManager.Gumps.OfType<ShopGump>().Should().ContainSingle(g => !g.IsDisposed);
        }
    }
}
