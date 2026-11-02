using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Game.UI
{
    // perf-8: a container window's content update reuses the item controls of items it shows again (ContainerItemControls)
    // instead of building every control anew. After any sequence of 0x25 adds, removals, amount, graphic, hue and position
    // changes, the window's item controls must be exactly the ones a full rebuild builds: same order (draw and hit order),
    // serial, graphic, hue, position, size, flags, visibility and tooltip. Sizes come from a test lookup (no game).
    [Collection("ContainerItemControls")]
    public sealed class ContainerItemControlsTests : IDisposable
    {
        private const uint BAG = 0x4000_A001, A = 0x4000_A002, B = 0x4000_A003, C = 0x4000_A004, D = 0x4000_A005, E = 0x4000_A006;
        private const ushort NO_ART = 0x3FFF;

        public ContainerItemControlsTests()
        {
            ItemGump.SpriteForTests = (ushort g, bool isGump, out int w, out int h, out bool partial) =>
            {
                w = 10 + g % 7;
                h = 12 + g % 5;
                partial = g % 2 == 0;

                return g != NO_ART;
            };
            ContainerItemControls.ShownForTests = (item, corpse) => item.Amount > 0;
        }

        public void Dispose()
        {
            ItemGump.SpriteForTests = null;
            ContainerItemControls.ShownForTests = null;
        }

        private static Item Bag(World world)
        {
            Item bag = world.Writer.UpsertWorldItem(BAG, 0x0E75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement);
            world.Writer.PlaceWorldItem(placement);

            return bag;
        }

        private static void Put(World world, uint serial, ushort graphic, ushort amount = 1, ushort x = 40, ushort y = 50, ushort hue = 0)
            => world.Writer.PlaceInContainer(serial, graphic, amount, x, y, hue, BAG, out _);

        private static void Remove(World world, uint serial) => world.Writer.RemoveWorldItem(world.Items.Get(serial), out _);

        private sealed class Window
        {
            public readonly Gump Gump;
            public readonly ContainerItemControls Controls = new ContainerItemControls();
            public List<string> Last;

            public Window(World world) => Gump = new Gump(world, 0, 0);

            // ContainerGump.UpdateContents' order: Begin (Clear), the frame controls, the items, End; then Update drops the
            // disposed children
            public void Update(bool reuse, bool chess = false, bool minimized = false, float scale = 1f)
            {
                Controls.Begin(Gump, reuse);
                Gump.Add(new Gump(Gump.World, 0, 0)); // a frame control: rebuilt every time
                int start = Gump.Children.Count;
                ContainerItemControls.AddItems(Gump, Controls, Gump.World.Items.Get(BAG), chess, false, minimized, scale);
                Last = Gump.Children.Skip(start).Select(c => Describe((ItemGump)c)).ToList();
                Controls.End(Gump);
                Gump.Children.RemoveAll(c => c.IsDisposed);
            }
        }

        private static string Describe(ItemGump c) =>
            $"0x{c.LocalSerial:X8} g=0x{c.Graphic:X4} h=0x{c.Hue:X4} {c.X},{c.Y} {c.Width}x{c.Height} partial={c.IsPartialHue} " +
            $"vis={c.IsVisible} disposed={c.IsDisposed} mouse={c.AcceptMouseInput} pick={c.CanPickUp} hl={c.HighlightOnMouseOver} " +
            $"move={c.CanMove} size={c.WantUpdateSize} page={c.Page} tip={c.Tooltip} parent={c.Parent != null}";

        private static List<string> Rebuild(World world, bool chess = false, bool minimized = false, float scale = 1f)
        {
            var w = new Window(world);
            w.Update(false, chess, minimized, scale);

            return w.Last;
        }

        private static void Same(World world, Window live, bool chess = false, bool minimized = false, float scale = 1f)
        {
            live.Update(true, chess, minimized, scale);
            live.Last.Should().Equal(Rebuild(world, chess, minimized, scale));
            live.Gump.Children.OfType<ItemGump>().Select(Describe).Should().Equal(live.Last.Where(s => s.Contains("disposed=False")));
        }

        // perf-9: the per-frame stack check (AddToRenderLists, Contains) reads the item through an EntityRef; it must return what
        // World.Items.Get returns through an amount change, a removal, a new item under the serial and a reused control
        [Fact]
        public void An_Item_Controls_Item_Is_What_The_Lookup_Returns()
        {
            var world = new World();
            Bag(world);
            Put(world, A, 0x0F3F, 5);
            Put(world, B, 0x1515);
            var g = new ItemGump(new Gump(world, 0, 0), A, 0x0F3F, 0, 40, 50);

            g.ShownItem().Should().BeSameAs(world.Items.Get(A));

            Put(world, A, 0x0F3F, 1);
            g.ShownItem().Should().BeSameAs(world.Items.Get(A));
            g.ShownItem().Amount.Should().Be(1);

            Remove(world, A);
            g.ShownItem().Should().BeSameAs(world.Items.Get(A));

            Put(world, A, 0x0F3F, 7);
            g.ShownItem().Should().BeSameAs(world.Items.Get(A));
            g.ShownItem().Amount.Should().Be(7);

            g.Reset(B, 0x1515, 0, 10, 10);
            g.ShownItem().Should().BeSameAs(world.Items.Get(B));
        }

        [Fact]
        public void A_Sequence_Of_Content_Changes_Ends_In_A_Full_Rebuilds_Controls()
        {
            var world = new World();
            Bag(world);
            Put(world, A, 0x1515, 1, 10, 20);
            Put(world, B, 0x0F3F, 50, 30, 40);
            Put(world, C, 0x09D0, 1, 60, 70, 0x0021);
            var live = new Window(world);
            Same(world, live);

            Put(world, D, 0x1716, 1, 80, 90); // 0x25 add
            Same(world, live);

            Put(world, B, 0x0F3F, 40, 30, 40); // 0x25 amount change: re-pushed to the end of the list
            Same(world, live);

            Remove(world, A); // 0x1D
            Same(world, live);

            Put(world, C, 0x09D1, 1, 60, 70, 0x0021); // graphic
            Same(world, live);

            Put(world, D, 0x1716, 1, 80, 90, 0x0482); // hue
            Put(world, B, 0x0F3F, 40, 130, 140); // position
            Same(world, live);

            Put(world, E, 0x1515, 0); // no amount: not shown
            Same(world, live);

            Put(world, C, NO_ART, 1, 60, 70); // no texture: the control disposes itself, as a new one does
            Same(world, live);

            Put(world, C, 0x09D0, 1, 60, 70); // and comes back
            Same(world, live);

            // 0x3C: everything out, then in again in another order
            foreach (uint s in new[] { B, C, D, E })
            {
                Remove(world, s);
            }

            Put(world, D, 0x1716, 3, 5, 6);
            Put(world, A, 0x1515, 1, 7, 8);
            Put(world, B, 0x0F3F, 2, 9, 10);
            Same(world, live);

            Same(world, live, minimized: true, scale: 1.5f); // window settings
            Same(world, live);

            // a chessboard window (gump pieces, the 20 px row offset) from its first update on
            var board = new Window(world);
            Same(world, board, chess: true);
            Put(world, E, 0x1716, 1, 44, 55);
            Remove(world, A);
            Same(world, board, chess: true);
        }

        [Fact]
        public void Items_Shown_Again_Keep_Their_Control_And_Removed_Ones_Are_Disposed()
        {
            var world = new World();
            Bag(world);
            Put(world, A, 0x1515);
            Put(world, B, 0x0F3F, 50);
            var live = new Window(world);
            live.Update(true);
            ItemGump a = live.Gump.Children.OfType<ItemGump>().Single(c => c.LocalSerial == A);
            ItemGump b = live.Gump.Children.OfType<ItemGump>().Single(c => c.LocalSerial == B);
            long reused = ContainerItemControls.ControlsReused, created = ContainerItemControls.ControlsCreated;

            Remove(world, B);
            Put(world, C, 0x09D0);
            live.Update(true);

            live.Gump.Children.OfType<ItemGump>().Should().Equal(a, live.Gump.Children.OfType<ItemGump>().Last());
            live.Gump.Children.OfType<ItemGump>().Last().LocalSerial.Should().Be(C);
            b.IsDisposed.Should().BeTrue();
            a.IsDisposed.Should().BeFalse();
            (ContainerItemControls.ControlsReused - reused).Should().Be(1);
            (ContainerItemControls.ControlsCreated - created).Should().Be(1);
        }

        [Fact]
        public void A_Full_Rebuild_Disposes_Every_Control_As_Clear_Did()
        {
            var world = new World();
            Bag(world);
            Put(world, A, 0x1515);
            var live = new Window(world);
            live.Update(true);
            ItemGump a = live.Gump.Children.OfType<ItemGump>().Single();
            Control frame = live.Gump.Children.First(c => !(c is ItemGump));

            live.Controls.Begin(live.Gump, reuse: false);

            a.IsDisposed.Should().BeTrue();
            frame.IsDisposed.Should().BeTrue();
            live.Gump.Children.Should().Contain(new[] { a, frame }, "left in the list for Update to drop, as Clear left them");
        }

        [Fact]
        public void Reuse_Disposes_The_Other_Children_And_Keeps_The_Item_Controls_Out_Of_The_List()
        {
            var world = new World();
            Bag(world);
            Put(world, A, 0x1515);
            var live = new Window(world);
            live.Update(true);
            ItemGump a = live.Gump.Children.OfType<ItemGump>().Single();
            Control frame = live.Gump.Children.First(c => !(c is ItemGump));

            live.Controls.Begin(live.Gump, reuse: true);

            frame.IsDisposed.Should().BeTrue();
            live.Gump.Children.Should().NotContain(a);
            a.IsDisposed.Should().BeFalse();

            live.Controls.End(live.Gump); // not shown again: disposed and back in the list for Update
            a.IsDisposed.Should().BeTrue();
            live.Gump.Children.Should().Contain(a);
        }
    }
}
