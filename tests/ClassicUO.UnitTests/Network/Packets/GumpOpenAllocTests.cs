using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Network.Packets
{
    // G0: bytes allocated on the main thread per gump open, per gump type, on the construction seam (GameSeam). Each open is
    // measured after two warm-up opens (static caches, first-call paths) and closed again before the next. The bounds are
    // regression guards about 25 % above the measured value; the numbers are printed (dotnet test --logger
    // "console;verbosity=detailed") for the survey's bytes-per-open table.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class GumpOpenAllocTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint BAG = 0x4000_0100;
        private const uint GUMP_ID = 0x0765_4321;

        private static readonly System.Reflection.PropertyInfo StaticSocket = typeof(NetClient).GetProperty(nameof(NetClient.Socket))!;

        private readonly GameSeam _seam = new();
        private readonly ITestOutputHelper _out;
        private readonly object _previousSocket;

        public GumpOpenAllocTests(ITestOutputHelper output)
        {
            _out = output;
            // gumps that ask the server for a status (health bars) send through NetClient.Socket: a loaded, unconnected client
            var client = new NetClient();
            client.Load(PacketPipe.Version, ClassicUO.Network.Encryption.EncryptionType.NONE);
            _previousSocket = StaticSocket.GetValue(null);
            StaticSocket.SetValue(null, client);
        }

        public void Dispose()
        {
            StaticSocket.SetValue(null, _previousSocket);
            GumpLayoutSwitch.OverrideForTests = null;
            GumpLayoutCache.OverrideForTests = null;
            FontLayoutPoolSwitch.OverrideForTests = null;
            GumpLayoutCache.Clear();
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

        private static void Setup(World world, params byte[][] packets)
        {
            var r = PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false, packets);
            r.Disconnects.Should().Be(0, r.Reason);
        }

        private static byte[] Equip(uint serial, int graphic, Layer layer, uint parent) =>
            new PacketBuilder(0x2E, false).U32(serial).U16(graphic).I8(0).U8((int)layer).U32(parent).U16(0).Build();

        private static byte[] Contained(uint serial, int graphic, int amount, uint container) =>
            new PacketBuilder(0x25, false).U32(serial).U16(graphic).U8(0).U16(amount).U16(10).U16(20).U8(0).U32(container).U16(0).Build();

        private long Measure(string what, Func<Gump> open)
        {
            for (int i = 0; i < 2; i++)
            {
                open()?.Dispose();
                UIManager.Gumps.Clear();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            Gump gump = open();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            gump.Should().NotBeNull();
            gump.Dispose();
            UIManager.Gumps.Clear();
            _out.WriteLine($"{what}: {bytes} B per open");

            return bytes;
        }

        // ---- server gump: 50 entries, 20 text lines (a ModernUO-style list gump)

        internal void MakeFixtureResident() => ServerGumpFixture.MakeResident(_seam);

        [Fact]
        public void ServerGump_Fixture_Has_50_Entries_And_20_Lines()
        {
            var (layout, lines) = ServerGumpFixture.Build();

            layout.Count(c => c == '{').Should().Be(50);
            lines.Should().HaveCount(20);
        }

        [Theory]
        [InlineData(false, false, 94_000)]
        [InlineData(true, false, 58_000)]
        [InlineData(true, true, 34_000)]
        public void ServerGump_50_Entries_20_Lines(bool v2, bool pool, long bound)
        {
            World world = InGame();
            MakeFixtureResident();
            GumpLayoutSwitch.OverrideForTests = v2;
            FontLayoutPoolSwitch.OverrideForTests = pool;
            var (layout, lines) = ServerGumpFixture.Build();

            long bytes = Measure($"server gump 50/20 gump_layout_v2={v2} font_layout_pool={pool}", () =>
                GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines));

            bytes.Should().BeLessThan(bound);
        }

        [Fact]
        public void ServerGump_V2_Saves_The_Token_Strings()
        {
            World world = InGame();
            MakeFixtureResident();
            var (layout, lines) = ServerGumpFixture.Build();

            GumpLayoutSwitch.OverrideForTests = false;
            long old = Measure("server gump 50/20 old", () => GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines));
            GumpLayoutSwitch.OverrideForTests = true;
            long v2 = Measure("server gump 50/20 v2", () => GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines));

            (old - v2).Should().BeGreaterThan(20_000, "the old path's token strings and lists are gone");
        }

        [Theory]
        [InlineData(false, false, false, 92_000)]
        [InlineData(true, false, false, 56_000)]
        [InlineData(true, true, false, 53_000)]
        [InlineData(true, false, true, 32_000)]
        [InlineData(true, true, true, 28_000)]
        public void ServerGump_Resend_Of_The_Open_Gump(bool v2, bool cache, bool pool, long bound)
        {
            World world = InGame();
            MakeFixtureResident();
            GumpLayoutSwitch.OverrideForTests = v2;
            GumpLayoutCache.OverrideForTests = cache;
            FontLayoutPoolSwitch.OverrideForTests = pool;
            var (layout, lines) = ServerGumpFixture.Build();
            Gump open = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);

            for (int i = 0; i < 2; i++)
            {
                GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            Gump again = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _out.WriteLine($"server gump 50/20 resend gump_layout_v2={v2} gump_layout_cache={cache} font_layout_pool={pool}: {bytes} B");

            again.Should().BeSameAs(open);
            bytes.Should().BeLessThan(bound);
        }

        // ---- client gumps

        [Fact]
        public void Container_With_20_Items()
        {
            World world = InGame();
            var packets = new List<byte[]> { Equip(BAG, 0x0E75, Layer.Backpack, PLAYER) };

            for (uint i = 0; i < 20; i++)
            {
                packets.Add(Contained(0x4000_1000 + i, 0x0EED + (int)(i % 4), 1 + (int)i, BAG));
            }

            Setup(world, packets.ToArray());
            _seam.Resident(0x003C, 175, 180);

            // gold piles: the displayed graphic follows the amount
            for (int g = 0x0EE0; g < 0x0F20; g++)
            {
                _seam.ResidentArt((ushort)g, 20, 18);
            }
            Item bag = world.Items.Get(BAG);
            int items = 0;

            long bytes = Measure("container 20 items (open + first Update)", () =>
            {
                ContainerGump built = GumpFactory.OpenContainer(world, bag, 0x003C);
                built.Update();
                items = built.Children.OfType<ItemGump>().Count(c => !c.IsDisposed);

                return built;
            });

            _out.WriteLine($"container item controls: {items}");
            items.Should().Be(20);
            bytes.Should().BeLessThan(13_000);
        }

        [Fact]
        public void Paperdoll_Of_The_Player()
        {
            World world = InGame();

            long bytes = Measure("paperdoll (player, open + first Update)", () =>
            {
                var g = new PaperDollGump(world, PLAYER, true);
                UIManager.Add(g);
                g.Update();

                return g;
            });

            bytes.Should().BeLessThan(30_000);
        }

        [Theory]
        [InlineData(false, 3_000, 5_500)]
        [InlineData(true, 2_700, 5_200)]
        public void HealthBar_Of_A_Mobile(bool pool, long classicBound, long customBound)
        {
            World world = InGame();
            FontLayoutPoolSwitch.OverrideForTests = pool;
            Mobile mob = world.Mobiles.Get(MOB);

            long classic = Measure("health bar (classic)", () =>
            {
                var g = new HealthBarGump(world, mob);
                UIManager.Add(g);

                return g;
            });
            long custom = Measure("health bar (custom)", () =>
            {
                var g = new HealthBarGumpCustom(world, mob);
                UIManager.Add(g);

                return g;
            });

            _out.WriteLine($"font_layout_pool={pool}");
            classic.Should().BeLessThan(classicBound);
            custom.Should().BeLessThan(customBound);
        }

        [Theory]
        [InlineData(false, 27_000)]
        [InlineData(true, 22_500)]
        public void Status_Of_The_Player(bool pool, long bound)
        {
            World world = InGame();
            FontLayoutPoolSwitch.OverrideForTests = pool;

            long bytes = Measure($"status font_layout_pool={pool}", () => StatusGumpBase.AddStatusGump(world, 100, 100));

            bytes.Should().BeLessThan(bound);
        }

        [Fact]
        public void NameOverhead_Of_A_Mobile()
        {
            World world = InGame();

            long bytes = Measure("name overhead gump", () =>
            {
                var g = new NameOverheadGump(world, MOB);
                UIManager.Add(g);

                return g;
            });

            bytes.Should().BeLessThan(1_100);
        }
    }
}
