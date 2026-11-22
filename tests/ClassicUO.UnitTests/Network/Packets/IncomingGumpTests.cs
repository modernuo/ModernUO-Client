using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

namespace ClassicUO.UnitTests.Network.Packets
{
    // Packets that open gumps (GumpFactory), through the test seam (GameSeam: real renderer front ends without a device,
    // the gumps a test names made resident), the whole receive path, every read mode, compressed or not, span_reader off
    // and on. The assertions are the gump's type and serials, the parsed fields it exposes, the control-tree shape for server
    // gumps (kinds in order, bounds, button ids, text, hues) and the world effects; not pixels, not private fields.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingGumpTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint BAG = 0x4000_0100;
        private const uint ITEM = 0x4000_0101;
        private const uint ITEM2 = 0x4000_0102;
        private const uint RESTOCK = 0x4000_0110;
        private const uint BUY = 0x4000_0111;
        private const uint GUMP_ID = 0x0123_4567;

        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            UIManager.RemovePosition(GUMP_ID);
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

        private static void Setup(World world, params byte[][] packets)
        {
            var r = PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false, packets);
            r.Disconnects.Should().Be(0);
        }

        private static byte[] Equip(uint serial, int graphic, Layer layer, uint parent) =>
            new PacketBuilder(0x2E, false).U32(serial).U16(graphic).I8(0).U8((int)layer).U32(parent).U16(0).Build();

        private static byte[] Contained(uint serial, int graphic, int amount, uint container) =>
            new PacketBuilder(0x25, false).U32(serial).U16(graphic).U8(0).U16(amount).U16(10).U16(20).U8(0).U32(container).U16(0).Build();

        private static World WithBag()
        {
            World world = InGame();
            Setup(world, Equip(BAG, 0x0E75, Layer.Backpack, PLAYER));

            return world;
        }

        private static IEnumerable<Control> Descendants(Control c)
        {
            foreach (Control child in c.Children)
            {
                if (child.IsDisposed)
                {
                    continue;
                }

                yield return child;

                foreach (Control d in Descendants(child))
                {
                    yield return d;
                }
            }
        }

        private static List<T> Open<T>() where T : Gump => UIManager.Gumps.OfType<T>().Where(g => !g.IsDisposed).ToList();

        private static void NoDisconnect(PacketPipe.Result r, int packets)
        {
            r.Disconnects.Should().Be(0, r.Reason);
            r.Packets.Should().Be(packets);
        }

        // ---- 0x24 open container

        [Theory]
        [MemberData(nameof(Modes))]
        public void OpenContainer_0x24_Opens_The_Container_Gump_Once(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();
            byte[] open = new PacketBuilder(0x24, false).U32(BAG).U16(0x003C).U16(0x007D).Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, open, open), 2);

            var gumps = Open<ContainerGump>();
            gumps.Should().ContainSingle("the second open replaces the first");
            gumps[0].LocalSerial.Should().Be(BAG);
            gumps[0].Graphic.Should().Be(0x003C);
            world.Items.Get(BAG).Opened.Should().BeTrue();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void OpenContainer_0x24_Unknown_Item_Opens_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x24, false).U32(BAG).U16(0x003C).U16(0x007D).Build(),
                new PacketBuilder(0x24, false).U32(BAG).U16(0xFFFF).U16(0x007D).Build(),
                new PacketBuilder(0x24, false).U32(0x0000_0999).U16(0x0030).U16(0x007D).Build(),
                new PacketBuilder(0xC8, false).U8(9).Build());

            NoDisconnect(r, 4);
            UIManager.Gumps.Should().BeEmpty();
            world.ClientViewRange.Should().Be(9);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void OpenContainer_0x24_FFFF_Opens_The_Spellbook(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x24, false).U32(BAG).U16(0xFFFF).U16(0x007D).Build()), 1);

            Open<SpellbookGump>().Should().ContainSingle().Which.LocalSerial.Should().Be(BAG);
            Open<ContainerGump>().Should().BeEmpty();
            world.Items.Get(BAG).Opened.Should().BeTrue();
        }

        // 7.0.60+ with large container gumps on: a container whose large gump exists opens that one, the others keep theirs
        [Theory]
        [MemberData(nameof(Modes))]
        public void OpenContainer_0x24_Large_Container_Gumps_From_7_0_60(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            GameSeam.UseVersion("7.0.60.0");
            World world = WithBag();
            Setup(world, Equip(ITEM, 0x0E7C, Layer.Bank, PLAYER));
            ClassicUO.Configuration.ProfileManager.CurrentProfile.UseLargeContainerGumps = true;
            _seam.Resident(0x06E8, 230, 200); // the large backpack; 0x0049's large gump (0x9CDF) is not in the pack

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x24, false).U32(BAG).U16(0x0048).U16(0x007D).Build(),
                new PacketBuilder(0x24, false).U32(ITEM).U16(0x0049).U16(0x007D).Build()), 2);

            Open<ContainerGump>().Select(g => (g.LocalSerial, g.Graphic)).Should().BeEquivalentTo(new[] { (BAG, (ushort)0x06E8), (ITEM, (ushort)0x0049) });
        }

        private const uint CORPSE = 0x4000_0300;

        // grid loot 1: the corpse opens no container gump; the first item the server puts in it opens the grid loot gump.
        // Grid loot 2: both.
        [Theory]
        [MemberData(nameof(Modes))]
        public void OpenContainer_0x24_Corpse_With_Grid_Loot_Opens_The_Grid_On_Its_Contents(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            foreach (int gridLoot in new[] { 1, 2 })
            {
                UIManager.Gumps.Clear();
                World world = InGame();
                ClassicUO.Configuration.ProfileManager.CurrentProfile.GridLootType = gridLoot;
                Setup(world, new PacketBuilder(0x1A, true).U32(CORPSE).U16(0x2006).U16(100).U16(200).I8(0).Build());

                NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                    new PacketBuilder(0x24, false).U32(CORPSE).U16(0x0009).U16(0x007D).Build()), 1);

                Open<GridLootGump>().Should().BeEmpty("the grid waits for the corpse's contents");
                Open<ContainerGump>().Should().HaveCount(gridLoot == 2 ? 1 : 0);

                NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, Contained(ITEM, 0x0EED, 5, CORPSE), Contained(ITEM2, 0x0EED, 1, CORPSE)), 2);

                Open<GridLootGump>().Should().ContainSingle($"grid loot {gridLoot}: one grid for both items").Which.LocalSerial.Should().Be(CORPSE);
                Open<ContainerGump>().Should().HaveCount(gridLoot == 2 ? 1 : 0);
                world.Items.Get(CORPSE).Opened.Should().Be(gridLoot == 2, "grid loot 1 returns before the corpse is marked opened");
            }
        }

        // ---- 0x74 buy list + 0x24 0x0030 vendor window

        [Theory]
        [MemberData(nameof(Modes))]
        public void BuyList_0x74_Then_OpenContainer_0x30_Lists_The_Vendor_Items(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            Setup(world, Equip(RESTOCK, 0x2AF8, Layer.ShopBuyRestock, MOB), Equip(BUY, 0x2AF8, Layer.ShopBuy, MOB),
                Contained(ITEM, 0x0EED, 5, BUY), Contained(ITEM2, 0x0F0E, 1, BUY));

            byte[] list = new PacketBuilder(0x74, true).U32(BUY).U8(2)
                .U32(12).U8(5).Ascii("Gold", 5).U32(34).U8(7).Ascii("Bottle", 7).Build();
            byte[] open = new PacketBuilder(0x24, false).U32(MOB).U16(0x0030).U16(0x007D).Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, list, open), 2);

            var shop = Open<ShopGump>().Should().ContainSingle().Subject;
            shop.LocalSerial.Should().Be(MOB);
            shop.IsBuyGump.Should().BeTrue();
            world.Items.Get(ITEM).Price.Should().Be(12);
            world.Items.Get(ITEM).Name.Should().Be("Gold");
            world.Items.Get(ITEM2).Price.Should().Be(34);
            world.Items.Get(ITEM2).Name.Should().Be("Bottle");
            Descendants(shop).Select(c => c.LocalSerial).Where(s => s == ITEM || s == ITEM2).Should().BeEquivalentTo(new[] { ITEM, ITEM2 });
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void BuyList_0x74_Unknown_Container_Opens_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x74, true).U32(BUY).U8(1).U32(1).U8(2).Ascii("x", 2).Build(),
                new PacketBuilder(0xC8, false).U8(8).Build());

            NoDisconnect(r, 2);
            UIManager.Gumps.Should().BeEmpty();
            world.ClientViewRange.Should().Be(8);
        }

        // ---- 0x9E sell list

        [Theory]
        [MemberData(nameof(Modes))]
        public void SellList_0x9E_Opens_The_Sell_Window_With_The_Items(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            static PacketBuilder Item(PacketBuilder b, uint serial, string name) =>
                b.U32(serial).U16(0x0EED).U16(0).U16(3).U16(15).U16(name.Length).Ascii(name, name.Length);

            byte[] sell = Item(Item(new PacketBuilder(0x9E, true).U32(MOB).U16(2), ITEM, "Gold"), ITEM2, "").Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, sell, sell), 2);

            var shop = Open<ShopGump>().Should().ContainSingle("the second list replaces the first").Subject;
            shop.LocalSerial.Should().Be(MOB);
            shop.IsBuyGump.Should().BeFalse();
            Descendants(shop).Select(c => c.LocalSerial).Where(s => s == ITEM || s == ITEM2).Should().BeEquivalentTo(new[] { ITEM, ITEM2 });

        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void SellList_0x9E_Empty_Or_Unknown_Vendor_Opens_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x9E, true).U32(MOB).U16(0).Build(),
                new PacketBuilder(0x9E, true).U32(0x0999).U16(1).U32(ITEM).U16(1).U16(0).U16(1).U16(1).U16(0).Build(),
                new PacketBuilder(0xC8, false).U8(8).Build());

            NoDisconnect(r, 3);
            UIManager.Gumps.Should().BeEmpty();
        }

        // ---- 0xB0 / 0xDD server gumps

        private const string Layout =
            "{ page 0 }{ resizepic 0 0 9200 300 200 }{ gumppic 20 30 5100 hue=33 }{ text 40 50 1152 0 }" +
            "{ button 60 70 4005 4007 1 0 7 }{ page 1 }{ button 80 90 4005 4007 0 2 0 }{ xmfhtmlgump 10 120 200 40 1015000 0 0 }" +
            "{ croppedtext 10 170 100 20 37 1 }";

        private void MakeLayoutGumpsResident()
        {
            _seam.Resident(5100, 44, 55);
            _seam.Resident(4005, 30, 22);
            _seam.Resident(4007, 30, 22);

            for (ushort i = 0; i < 9; i++)
            {
                _seam.Resident((ushort)(9200 + i), 16, 16);
            }
        }

        private static string Shape(Control c) => c switch
        {
            Button b => $"Button {b.Bounds} page{b.Page} id{b.ButtonID} action{(int)b.ButtonAction} to{b.ToPage}",
            Label l => $"Label {l.Bounds} page{l.Page} '{l.Text}' hue{l.Hue}",
            HtmlControl h => $"Html {h.Bounds} page{h.Page} '{h.Text}'",
            GumpPic g => $"GumpPic {g.Bounds} page{g.Page} g{g.Graphic} hue{g.Hue}",
            ResizePic r => $"ResizePic {r.Bounds} page{r.Page} g{r.Graphic}",
            _ => $"{c.GetType().Name} {c.Bounds} page{c.Page}"
        };

        private string[] ExpectedShape()
        {
            string cliloc = _seam.Game.UO.FileManager.Clilocs.GetString(1015000);

            return new[]
            {
                "ResizePic {X:0 Y:0 Width:300 Height:200} page0 g9200",
                "GumpPic {X:20 Y:30 Width:44 Height:55} page0 g5100 hue34",
                "Label {X:40 Y:50 Width:39 Height:12} page0 'first' hue1153",
                "Button {X:60 Y:70 Width:30 Height:22} page0 id7 action1 to0",
                "Button {X:80 Y:90 Width:30 Height:22} page1 id0 action0 to2",
                $"Html {{X:10 Y:120 Width:200 Height:40}} page1 '{cliloc}'",
                "CroppedText {X:10 Y:170 Width:100 Height:20} page1"
            };
        }

        private static byte[] ServerGump(uint sender, string layout, params string[] lines)
        {
            var b = new PacketBuilder(0xB0, true).U32(sender).U32(GUMP_ID).U32(100).U32(150).U16(layout.Length + 1).Ascii(layout).U16(lines.Length);

            foreach (string l in lines)
            {
                b.U16(l.Length).UnicodeBE(l, false);
            }

            return b.Build();
        }

        private static byte[] Zlib(byte[] data)
        {
            var ms = new MemoryStream();

            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
            {
                z.Write(data);
            }

            return ms.ToArray();
        }

        private static byte[] CompressedGump(uint sender, string layout, params string[] lines)
        {
            byte[] raw = Encoding.UTF8.GetBytes(layout);
            byte[] packed = Zlib(raw);
            var b = new PacketBuilder(0xDD, true).U32(sender).U32(GUMP_ID).U32(100).U32(150)
                .U32((uint)packed.Length + 4).U32((uint)raw.Length).Bytes(packed).U32((uint)lines.Length);

            if (lines.Length > 0)
            {
                var t = new List<byte>();

                foreach (string l in lines)
                {
                    t.Add((byte)(l.Length >> 8));
                    t.Add((byte)l.Length);
                    t.AddRange(Encoding.BigEndianUnicode.GetBytes(l));
                }

                byte[] text = Zlib(t.ToArray());
                b.U32((uint)text.Length + 4).U32((uint)t.Count).Bytes(text);
            }

            return b.Build();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerGump_0xB0_Builds_The_Layout(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            MakeLayoutGumpsResident();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, ServerGump(ITEM, Layout, "first", "second")), 1);

            Gump gump = UIManager.Gumps.Single(g => !g.IsDisposed);
            gump.LocalSerial.Should().Be(ITEM);
            gump.ServerSerial.Should().Be(GUMP_ID);
            gump.IsFromServer.Should().BeTrue();
            gump.X.Should().Be(100);
            gump.Y.Should().Be(150);
            gump.Children.Where(c => !c.IsDisposed).Select(Shape).Should().Equal(ExpectedShape());
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerGump_0xDD_Compressed_Builds_The_Same_Layout(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            MakeLayoutGumpsResident();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, CompressedGump(ITEM, Layout, "first", "second")), 1);

            Gump gump = UIManager.Gumps.Single(g => !g.IsDisposed);
            gump.LocalSerial.Should().Be(ITEM);
            gump.ServerSerial.Should().Be(GUMP_ID);
            gump.Children.Where(c => !c.IsDisposed).Select(Shape).Should().Equal(ExpectedShape());
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerGump_0xB0_Same_Sender_And_Id_Is_Rebuilt_In_Place(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            MakeLayoutGumpsResident();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, ServerGump(ITEM, Layout, "first")), 1);
            Gump first = UIManager.Gumps.Single(g => !g.IsDisposed);

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, ServerGump(ITEM, "{ text 1 2 0 0 }", "again")), 1);

            UIManager.Gumps.Where(g => !g.IsDisposed).Should().ContainSingle().Which.Should().BeSameAs(first);
            first.Children.Where(c => !c.IsDisposed).Select(Shape).Should().Equal("Label {X:1 Y:2 Width:39 Height:12} page0 'again' hue1");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ServerGump_Missing_Pictures_Empty_Layout_And_Bad_Streams(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] dd = CompressedGump(ITEM, Layout, "first");
            // a stream the inflater refuses: the layout opens empty (no gump) and the stream stays framed
            dd[3 + 16 + 8] ^= 0xFF;

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                ServerGump(ITEM, "", "x"),
                dd,
                ServerGump(MOB, "{ gumppic 1 1 5100 }{ text 0 0 0 5 }"),
                new PacketBuilder(0xC8, false).U8(5).Build());

            NoDisconnect(r, 4);
            world.ClientViewRange.Should().Be(5);
            Gump gump = UIManager.Gumps.Single(g => !g.IsDisposed);
            gump.LocalSerial.Should().Be(MOB);
            gump.Children.Where(c => !c.IsDisposed).Select(Shape).Should().Equal(
                new[] { "Label {X:0 Y:0 Width:0 Height:0} page0 '' hue1" }, "a picture missing from the pack drops out; a text index past the lines is empty");
        }

        // ---- 0x7C menus

        [Theory]
        [MemberData(nameof(Modes))]
        public void Menu_0x7C_With_Pictures_Opens_A_Menu_Gump(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            _seam.ResidentArt(0x0EED, 20, 30);
            _seam.ResidentArt(0x0F0E, 25, 50);

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x7C, true).U32(ITEM).U16(5).U8(4).Ascii("Menu", 4).U8(2)
                    .U16(0x0EED).U16(0).U8(3).Ascii("abc", 3).U16(0x0F0E).U16(0x21).U8(2).Ascii("de", 2).Build()), 1);

            var menu = Open<MenuGump>().Should().ContainSingle().Subject;
            menu.LocalSerial.Should().Be(ITEM);
            menu.ServerSerial.Should().Be(5u);
            // items side by side, each centred in the 47-high strip unless taller
            Descendants(menu).Where(c => c.GetType().Name == "ItemView")
                .Select(c => $"{c.X},{c.Y} {c.Width}x{c.Height} {c.Tooltip}")
                .Should().Equal("0,8 20x30 abc", "20,0 25x50 de");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Menu_0x7C_Without_Pictures_Opens_A_Gray_Menu_Of_The_Names(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            _seam.Resident(0x138A, 20, 20);
            _seam.Resident(0x138B, 20, 20);
            _seam.Resident(0x1450, 60, 20);
            _seam.Resident(0x1451, 60, 20);
            _seam.Resident(0x13B2, 60, 20);
            _seam.Resident(0x13B3, 60, 20);

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x7C, true).U32(ITEM).U16(6).U8(5).Ascii("Gray?", 5).U8(2)
                    .U16(0).U16(0).U8(3).Ascii("Yes", 3).U16(0).U16(0).U8(2).Ascii("No", 2).Build()), 1);

            var menu = Open<GrayMenuGump>().Should().ContainSingle().Subject;
            menu.LocalSerial.Should().Be(ITEM);
            menu.ServerSerial.Should().Be(6u);
            Descendants(menu).OfType<RadioButton>().Select(b => b.Text).Should().Equal("Yes", "No");
            Descendants(menu).OfType<Button>().Select(b => b.ButtonID).Should().Equal(0, 1);
        }

        // ---- 0x90 / 0xF5 maps, 0x56 map plot

        [Theory]
        [MemberData(nameof(Modes))]
        public void Map_0x90_And_0xF5_Open_Map_Gumps_And_0x56_Edits_Them(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x90, false).U32(BAG).U16(0x139D).U16(0).U16(0).U16(100).U16(100).U16(200).U16(180).Build(),
                new PacketBuilder(0xF5, false).U32(ITEM).U16(0x139D).U16(0).U16(0).U16(100).U16(100).U16(200).U16(180).U16(1).Build(),
                new PacketBuilder(0x56, false).U32(BAG).U8(7).U8(3).U16(0).U16(0).Build());

            NoDisconnect(r, 3);
            var maps = Open<MapGump>();
            maps.Select(m => m.LocalSerial).Should().BeEquivalentTo(new[] { BAG, ITEM });
            world.Items.Get(BAG).Opened.Should().BeTrue();
            maps.Single(m => m.LocalSerial == BAG).PlotState.Should().Be(3);
        }

        // ---- 0x95 dye picker

        [Theory]
        [MemberData(nameof(Modes))]
        public void Dye_0x95_Opens_The_Picker_And_Replaces_It_For_Another_Graphic(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] Dye(int graphic) => new PacketBuilder(0x95, false).U32(ITEM).U16(0).U16(graphic).Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, Dye(0x0FAB)), 1);
            ColorPickerGump first = Open<ColorPickerGump>().Should().ContainSingle().Subject;
            first.LocalSerial.Should().Be(ITEM);
            first.Graphic.Should().Be(0x0FAB);

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, Dye(0x0FAB)), 1);
            Open<ColorPickerGump>().Should().ContainSingle().Which.Should().BeSameAs(first, "the same graphic keeps the open picker");

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, Dye(0x0EFF)), 1);
            Open<ColorPickerGump>().Should().ContainSingle().Which.Graphic.Should().Be(0x0EFF);
            first.IsDisposed.Should().BeTrue();
        }

        // ---- 0x93 / 0xD4 books, 0x66 pages

        [Theory]
        [MemberData(nameof(Modes))]
        public void Book_0xD4_Opens_Requests_Page_1_And_0x66_Fills_It(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0xD4, true).U32(ITEM).U8(1).U8(1).U16(4).U16(6).Ascii("Title").U16(5).Ascii("Auth").Build());

            NoDisconnect(r, 1);
            var book = Open<ModernBookGump>().Should().ContainSingle().Subject;
            book.LocalSerial.Should().Be(ITEM);
            book.BookPageCount.Should().Be(4);
            book.UseNewHeader.Should().BeTrue();
            r.Sent.Should().Equal(new PacketBuilder(0x66, true).U32(ITEM).U16(1).U16(1).U16(0xFFFF).Build());

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x66, true).U32(ITEM).U16(1).U16(1).U16(2).Ascii("line one").Ascii("line two").Build()), 1);

            book.KnownPages.Should().Contain(0);
            // the gump keeps each line with its line break
            book.BookLines[0].TrimEnd('\n').Should().Be("line one");
            book.BookLines[1].TrimEnd('\n').Should().Be("line two");
            book.BookLines[2].TrimEnd('\n').Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Book_0x93_Old_Header_And_A_Second_Open_Updates_The_Book(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] Old(bool editable, string title) =>
                new PacketBuilder(0x93, false).U32(ITEM).U8(editable ? 1 : 0).U8(1).U16(3).Ascii(title, 60).Ascii("Auth", 30).Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, Old(false, "Old")), 1);
            var book = Open<ModernBookGump>().Should().ContainSingle().Subject;
            book.BookPageCount.Should().Be(3);
            book.IsEditable.Should().BeFalse();

            var r = PacketPipe.Deliver(world, mode, read, compressed, Old(true, "Again"));

            NoDisconnect(r, 1);
            Open<ModernBookGump>().Should().ContainSingle().Which.Should().BeSameAs(book);
            book.IsEditable.Should().BeTrue();
            book.UseNewHeader.Should().BeFalse();
            r.Sent.Should().BeEmpty("an open book is not requested again");
        }

        // ---- 0x71 bulletin boards

        [Theory]
        [MemberData(nameof(Modes))]
        public void Bulletin_0x71_Opens_The_Board_Lists_A_Summary_And_Opens_A_Message(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();
            const uint MSG = 0x4000_0200;
            static PacketBuilder Str(PacketBuilder b, string s) => b.U8(s.Length + 1).Ascii(s);

            var summary = Str(Str(Str(new PacketBuilder(0x71, true).U8(1).U32(BAG).U32(MSG).U32(0), "poster"), "subject"), "today");
            var message = Str(Str(Str(new PacketBuilder(0x71, true).U8(2).U32(BAG).U32(MSG), "poster"), "subject"), "today")
                .U32(0).U8(0).U8(2);
            message = Str(Str(message, "hello"), "world");

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x71, true).U8(0).U32(BAG).Ascii("Board", 22).U32(0).Build(),
                summary.Build(),
                message.Build());

            NoDisconnect(r, 3);
            var board = Open<BulletinBoardGump>().Should().ContainSingle().Subject;
            board.LocalSerial.Should().Be(BAG);
            world.Items.Get(BAG).Opened.Should().BeTrue();
            Descendants(board).OfType<BulletinBoardObject>().Should().ContainSingle().Which.LocalSerial.Should().Be(MSG);
            var item = Open<BulletinBoardItem>().Should().ContainSingle().Subject;
            item.LocalSerial.Should().Be(BAG);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Bulletin_0x71_Summary_And_Message_Without_A_Board_Are_Skipped(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0x71, true).U8(1).U32(BAG).U32(1).U32(0).U8(1).U8(0).U8(1).U8(0).U8(1).U8(0).Build(),
                new PacketBuilder(0x71, true).U8(0).U32(BAG).Ascii("Board", 22).U32(0).Build(),
                new PacketBuilder(0xC8, false).U8(4).Build());

            NoDisconnect(r, 3);
            UIManager.Gumps.Should().BeEmpty("the board item is unknown");
            world.ClientViewRange.Should().Be(4);
        }

        // ---- 0xB8 profile

        [Theory]
        [MemberData(nameof(Modes))]
        public void Profile_0xB8_Opens_The_Profile_Once(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] Profile(uint serial) => new PacketBuilder(0xB8, true).U32(serial).Ascii("head").UnicodeBE("foot").UnicodeBE("body").Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, Profile(MOB), Profile(MOB), Profile(PLAYER)), 3);

            Open<ProfileGump>().Select(g => g.LocalSerial).Should().BeEquivalentTo(new[] { MOB, PLAYER });
        }

        // ---- 0xBF 0x14 popup, 0xBF 0x2A race change

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_0x14_Shows_The_Popup_Menu(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            for (ushort g = 0x0A3C; g < 0x0A3C + 9; g++)
            {
                _seam.Resident(g, 10, 10);
            }

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0xBF, true).U16(0x14).U16(2).U32(MOB).U8(2)
                    .U32(3006123).U16(1).U16(0).U32(3006150).U16(2).U16(1).Build()), 1);

            UIManager.PopupMenu.Should().NotBeNull();
            UIManager.PopupMenu.IsDisposed.Should().BeFalse();
            UIManager.Gumps.Should().Contain(UIManager.PopupMenu);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Extended_0xBF_0x2A_Opens_One_Race_Change_Gump(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] race = new PacketBuilder(0xBF, true).U16(0x2A).U8(1).U8(2).Build();

            NoDisconnect(PacketPipe.Deliver(world, mode, read, compressed, race, race), 2);

            Open<RaceChangeGump>().Should().ContainSingle();
        }
    }
}
