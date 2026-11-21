using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;
using static ClassicUO.UnitTests.Game.UI.UiInputTests;

namespace ClassicUO.UnitTests.Game.UI
{
    // G1 (gump system): closed controls drop out of UIManager's input state at once, and the container gump's highlight of
    // the hovered sub-container reads a value latched for the UI pass.
    [Collection(nameof(FunnelCheckCollection))] // UIManager and Mouse are process-wide
    public sealed class UiStaleReferenceTests
    {
        [Fact]
        public void A_Control_Closed_Under_The_Mouse_Gets_No_Exit_But_Its_Open_Gump_Does()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var a = new Window(world, 0, 0, 100, 100);
            var child = new Box("child", 0, 0, 40, 40);
            a.Add(child);
            var b = new Window(world, 200, 0, 100, 100);
            UIManager.Add(a);
            UIManager.Add(b);
            var log = new List<string>();
            child.MouseExit += (s, e) => log.Add("child exit");
            a.MouseExit += (s, e) => log.Add("A exit");
            b.MouseEnter += (s, e) => log.Add("B enter");

            UiTestScope.HandleMouse(10, 10);
            UIManager.MouseOverControl.Should().BeSameAs(child);

            child.Dispose();
            UiTestScope.HandleMouse(210, 10);

            log.Should().Equal("A exit", "B enter");
        }

        [Fact]
        public void A_Gump_Closed_Under_The_Mouse_Gets_No_Exit()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var a = new Window(world, 0, 0, 100, 100);
            var child = new Box("child", 0, 0, 40, 40);
            a.Add(child);
            UIManager.Add(a);
            var log = new List<string>();
            child.MouseExit += (s, e) => log.Add("child exit");
            a.MouseExit += (s, e) => log.Add("A exit");

            UiTestScope.HandleMouse(10, 10);
            a.Dispose();
            UiTestScope.HandleMouse(10, 10);

            log.Should().BeEmpty();
            UIManager.MouseOverControl.Should().BeNull();
        }

        [Fact]
        public void A_Closed_Control_Leaves_The_Mouse_Down_And_Focus_Slots()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var a = new Window(world, 0, 0, 100, 100);
            var field = new Box("field", 10, 10, 20, 20) { AcceptKeyboardInput = true };
            a.Add(field);
            var b = new Window(world, 200, 0, 100, 100);
            var other = new Box("other", 10, 10, 20, 20);
            b.Add(other);
            UIManager.Add(a);
            UIManager.Add(b);
            int lost = 0;
            field.FocusLost += (s, e) => lost++;

            Mouse.Position = new Point(15, 15);
            UIManager.OnMouseButtonDown(MouseButtonType.Left);
            UIManager.LastControlMouseDown(MouseButtonType.Left).Should().BeSameAs(field);
            UiTestScope.LastFocus.Should().BeSameAs(field);

            a.Dispose(); // closed while the button is down (a server close, Esc)

            UIManager.LastControlMouseDown(MouseButtonType.Left).Should().BeNull();
            UiTestScope.LastFocus.Should().BeNull();

            UIManager.Update();
            Mouse.Position = new Point(215, 15);
            UIManager.OnMouseButtonDown(MouseButtonType.Left);
            lost.Should().Be(0, "the closed control is not told it lost the focus");
            UiTestScope.LastFocus.Should().BeSameAs(other);
            UIManager.OnMouseButtonUp(MouseButtonType.Left);
        }

        [Fact]
        public void Disposing_A_Control_Not_In_Any_Slot_Keeps_The_Others()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var a = new Window(world, 0, 0, 100, 100);
            var field = new Box("field", 10, 10, 20, 20);
            a.Add(field);
            UIManager.Add(a);

            Mouse.Position = new Point(15, 15);
            UIManager.OnMouseButtonDown(MouseButtonType.Left);
            new Box("unrelated", 0, 0, 1, 1).Dispose();

            UIManager.LastControlMouseDown(MouseButtonType.Left).Should().BeSameAs(field);
            UiTestScope.LastFocus.Should().BeSameAs(field);
            UIManager.OnMouseButtonUp(MouseButtonType.Left);
        }

        [Fact]
        public void A_Closed_Modal_Gump_Stops_Blocking_Input_In_The_Same_Frame()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var front = new Window(world, 0, 0, 100, 100);
            var modal = new Window(world, 200, 200, 50, 50) { IsModal = true };
            UIManager.Add(front);
            UIManager.Add(modal, false);

            UiTestScope.Hover(10, 10).Should().BeNull();
            modal.Dispose();
            UiTestScope.Hover(10, 10).Should().BeSameAs(front, "the closed modal is still listed until Update, but no longer blocks");
            UIManager.IsModalOpen.Should().BeFalse();
        }

        [Fact]
        public void The_Selected_Container_Highlight_Reads_The_Latched_Serial()
        {
            uint saved = SelectedObject.UiSelectedContainerSerial;

            try
            {
                SelectedObject.UiSelectedContainerSerial = 0x4000_0010;
                ItemGump.IsSelectedContainer(0x4000_0010).Should().BeTrue();
                ItemGump.IsSelectedContainer(0x4000_0011).Should().BeFalse();

                SelectedObject.UiSelectedContainerSerial = 0;
                ItemGump.IsSelectedContainer(0).Should().BeFalse("no container is hovered");
            }
            finally
            {
                SelectedObject.UiSelectedContainerSerial = saved;
            }
        }

        [Fact]
        public void The_Frame_Latches_The_Selected_Container_Before_Clearing_It_For_The_Ui_Pass()
        {
            string root = WriteSiteScanner.FindRepoRoot();
            string game = File.ReadAllText(Path.Combine(root, "src/ClassicUO.Client/GameController.cs"));
            string item = File.ReadAllText(Path.Combine(root, "src/ClassicUO.Client/Game/UI/Controls/ItemGump.cs"));

            game.Should().MatchRegex(
                @"UiSelectedContainerSerial\s*=\s*SelectedObject\.SelectedContainer\?\.Serial\s*\?\?\s*0;\s*SelectedObject\.SelectedContainer\s*=\s*null;",
                "the UI pass needs the value the world draw used");
            Regex.IsMatch(item, @"SelectedObject\.SelectedContainer\b").Should().BeFalse("SelectedContainer is null during the UI pass");
        }
    }
}
