using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // G0 (gump system): hit testing, modal routing, focus and drag as they work today, so the gump-system stages (dirty
    // tracking, the layout builder, the registries) can be proven not to change them.
    [Collection(nameof(FunnelCheckCollection))] // UIManager and Mouse are process-wide
    public sealed class UiInputTests
    {
        internal sealed class Box : Control
        {
            public readonly string Name;
            public System.Func<int, int, bool> Shape; // per-pixel Contains (GumpPic's alpha test), in local coordinates
            public readonly List<(int X, int Y)> ContainsCalls = new();

            public Box(string name, int x, int y, int w, int h, bool mouse = true)
            {
                Name = name;
                X = x;
                Y = y;
                Width = w;
                Height = h;
                AcceptMouseInput = mouse;
            }

            public override bool Contains(int x, int y)
            {
                ContainsCalls.Add((x, y));

                return Shape == null ? base.Contains(x, y) : Shape(x, y);
            }

            public override string ToString() => Name;
        }

        internal sealed class Window : Gump
        {
            public readonly List<string> Log = new();

            public Window(World world, int x, int y, int w, int h) : base(world, 0, 0)
            {
                X = x;
                Y = y;
                Width = w;
                Height = h;
                AcceptMouseInput = true;
                CanMove = true;
            }

            // Gump.OnDragEnd clamps to Client.Game's window, which the tests don't have
            protected override void OnDragEnd(int x, int y) => Log.Add($"dragend {x},{y}");

            protected override void OnDragBegin(int x, int y) => Log.Add($"dragbegin {x},{y}");

            protected override void OnMove(int x, int y) => Log.Add($"move {x},{y}");
        }

        private static Control Hit(Control root, int x, int y)
        {
            Control res = null;
            root.HitTest(x, y, ref res);

            return res;
        }

        [Fact]
        public void Children_Hit_In_Window_Coordinates_And_A_Later_Child_Of_Equal_Priority_Wins()
        {
            var root = new Box("root", 100, 50, 200, 200);
            var a = new Box("a", 10, 10, 50, 50);
            var b = new Box("b", 30, 30, 50, 50);
            root.Add(a);
            root.Add(b);

            Hit(root, 115, 65).Should().BeSameAs(a);
            Hit(root, 140, 90).Should().BeSameAs(b, "overlap: the later child (drawn on top) wins");
            Hit(root, 105, 55).Should().BeSameAs(root);
            Hit(root, 99, 55).Should().BeNull();
        }

        [Fact]
        public void Priority_Decides_Overlaps_Before_Child_Order()
        {
            var root = new Box("root", 0, 0, 100, 100);
            var high = new Box("high", 0, 0, 50, 50) { Priority = ClickPriority.High };
            var low = new Box("low", 0, 0, 50, 50) { Priority = ClickPriority.Low };
            var def = new Box("default", 0, 0, 50, 50);
            root.Add(high);
            root.Add(def);
            root.Add(low);

            Hit(root, 10, 10).Should().BeSameAs(high, "a later Default or Low child does not take the hit from a High one");

            root.Children.Remove(high);
            Hit(root, 10, 10).Should().BeSameAs(def, "Low does not beat Default");

            var later = new Box("later high", 0, 0, 50, 50) { Priority = ClickPriority.High };
            root.Add(later);
            Hit(root, 10, 10).Should().BeSameAs(later);
        }

        [Fact]
        public void Per_Pixel_Contains_Gets_Local_Coordinates_And_A_Miss_Skips_The_Subtree()
        {
            var root = new Box("root", 100, 100, 100, 100);
            var pic = new Box("pic", 20, 20, 40, 40) { Shape = (x, y) => x < 20 }; // left half opaque
            var inside = new Box("inside", 0, 0, 40, 40);
            pic.Add(inside);
            root.Add(pic);

            Hit(root, 125, 125).Should().BeSameAs(inside);
            pic.ContainsCalls[^1].Should().Be((5, 5));
            Hit(root, 150, 125).Should().BeSameAs(root, "a transparent pixel hits neither the control nor its children");
        }

        [Fact]
        public void Hidden_Disabled_Other_Page_And_No_Mouse_Input_Controls()
        {
            var root = new Box("root", 0, 0, 100, 100) { ActivePage = 1 };
            var passive = new Box("passive", 0, 0, 50, 50, mouse: false);
            var child = new Box("child", 0, 0, 20, 20);
            passive.Add(child);
            var page2 = new Box("page2", 60, 60, 20, 20);
            var hidden = new Box("hidden", 0, 60, 20, 20) { IsVisible = false };
            var disabled = new Box("disabled", 60, 0, 20, 20) { IsEnabled = false };
            root.Add(passive);
            root.Add(page2, 2);
            root.Add(hidden);
            root.Add(disabled);

            Hit(root, 10, 10).Should().BeSameAs(child, "a control without mouse input still passes hits to its children");
            Hit(root, 30, 30).Should().BeSameAs(root);
            Hit(root, 65, 65).Should().BeSameAs(root, "page 2 is not the active page");
            Hit(root, 5, 65).Should().BeSameAs(root);
            Hit(root, 65, 5).Should().BeSameAs(root);

            root.ActivePage = 2;
            Hit(root, 65, 65).Should().BeSameAs(page2);
        }

        [Fact]
        public void The_Front_Gump_Takes_The_Hit_And_A_Modal_Gump_Blocks_The_Others()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var back = new Window(world, 0, 0, 100, 100);
            var front = new Window(world, 50, 50, 100, 100);
            UIManager.Add(back);
            UIManager.Add(front); // front = First

            UiTestScope.Hover(60, 60).Should().BeSameAs(front);
            UiTestScope.Hover(10, 10).Should().BeSameAs(back);
            UiTestScope.Hover(300, 300).Should().BeNull();

            var modal = new Window(world, 200, 200, 50, 50) { IsModal = true };
            UIManager.Add(modal, false); // behind the others
            UiTestScope.Hover(60, 60).Should().BeNull("only modal gumps take input while one is open");
            UIManager.IsModalOpen.Should().BeTrue();
            UiTestScope.Hover(210, 210).Should().BeSameAs(modal);

            modal.Dispose();
            UIManager.Update(); // drops the disposed gump from the list
            UiTestScope.Hover(60, 60).Should().BeSameAs(front);
            UIManager.IsModalOpen.Should().BeFalse();
        }

        [Fact]
        public void A_Click_Outside_Closes_Modal_Gumps_That_Ask_For_It()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var closes = new Window(world, 0, 0, 50, 50) { IsModal = true, ModalClickOutsideAreaClosesThisControl = true };
            var stays = new Window(world, 100, 0, 50, 50) { IsModal = true };
            UIManager.Add(closes);
            UIManager.Add(stays);

            Mouse.Position = new Point(300, 300);
            UIManager.OnMouseButtonDown(MouseButtonType.Left);

            closes.IsDisposed.Should().BeTrue();
            stays.IsDisposed.Should().BeFalse();
            Mouse.CancelDoubleClick = false;
        }

        [Fact]
        public void Mouse_Down_Raises_The_Gump_And_Moves_Focus()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var a = new Window(world, 0, 0, 100, 100);
            var b = new Window(world, 50, 0, 100, 100);
            var field = new Box("field", 10, 10, 20, 20) { AcceptKeyboardInput = true };
            a.Add(field);
            var other = new Box("other", 70, 10, 20, 20) { AcceptKeyboardInput = true };
            b.Add(other);
            UIManager.Add(a);
            UIManager.Add(b);
            var log = new List<string>();
            field.FocusEnter += (s, e) => log.Add("field enter");
            field.FocusLost += (s, e) => log.Add("field lost");
            other.FocusEnter += (s, e) => log.Add("other enter");

            Mouse.Position = new Point(15, 15);
            UIManager.OnMouseButtonDown(MouseButtonType.Left);

            UIManager.Gumps.First.Value.Should().BeSameAs(a, "the clicked gump comes to the front");
            UIManager.KeyboardFocusControl.Should().BeSameAs(field);
            UIManager.LastControlMouseDown(MouseButtonType.Left).Should().BeSameAs(field);
            field.IsFocused.Should().BeTrue();

            UIManager.OnMouseButtonUp(MouseButtonType.Left);
            UIManager.LastControlMouseDown(MouseButtonType.Left).Should().BeNull();

            Mouse.Position = new Point(125, 15);
            UIManager.OnMouseButtonDown(MouseButtonType.Left);
            UIManager.KeyboardFocusControl.Should().BeSameAs(other);
            log.Should().Equal("field enter", "field lost", "other enter");
            UIManager.OnMouseButtonUp(MouseButtonType.Left);
        }

        [Fact]
        public void Hover_Change_Raises_Exit_Then_Enter_And_Root_Events_Only_Across_Gumps()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var a = new Window(world, 0, 0, 100, 100);
            var a1 = new Box("a1", 0, 0, 40, 40);
            var a2 = new Box("a2", 50, 0, 40, 40);
            a.Add(a1);
            a.Add(a2);
            var b = new Window(world, 200, 0, 100, 100);
            UIManager.Add(a);
            UIManager.Add(b);
            var log = new List<string>();

            foreach (var (c, n) in new (Control, string)[] { (a, "A"), (a1, "a1"), (a2, "a2"), (b, "B") })
            {
                c.MouseEnter += (s, e) => log.Add(n + " enter");
                c.MouseExit += (s, e) => log.Add(n + " exit");
            }

            UiTestScope.HandleMouse(10, 10);
            UIManager.MouseOverControl.Should().BeSameAs(a1);
            UiTestScope.HandleMouse(60, 10);
            UiTestScope.HandleMouse(210, 10);
            UiTestScope.HandleMouse(500, 500);
            UIManager.MouseOverControl.Should().BeNull();

            log.Should().Equal("a1 enter", "A enter", "a1 exit", "a2 enter", "a2 exit", "A exit", "B enter", "B exit");
        }

        [Fact]
        public void Drag_Moves_The_Root_By_The_Mouse_Delta()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var w = new Window(world, 100, 100, 50, 50);
            UIManager.Add(w);

            UiTestScope.StartDrag(w, new Point(110, 110));
            UIManager.IsDragging.Should().BeTrue();
            UiTestScope.Hover(400, 400).Should().BeSameAs(w, "the dragged gump keeps the mouse");

            Mouse.Position = new Point(130, 115);
            UIManager.OnMouseDragging();
            w.Location.Should().Be(new Point(120, 105));

            UiTestScope.EndDrag(new Point(140, 125));
            w.Location.Should().Be(new Point(130, 115));
            UIManager.IsDragging.Should().BeFalse();
            UIManager.DraggingControl.Should().BeNull();
            w.Log.Should().Equal("move -100,-100", "move -120,-105", "dragend 10,10");
        }
    }
}
