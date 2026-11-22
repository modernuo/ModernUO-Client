using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests
{
    // window_display / -display and window_position: the monitor and spot the window opens on (WindowPlacement). Monitors
    // left of or above the primary have negative desktop coordinates.
    public class WindowPlacementTests
    {
        // SDL order: the primary (index 0, 2560x1400 usable), a monitor to its left, one above it
        private static readonly DisplayInfo[] Displays =
        {
            new(1, "DELL U2720Q", new Rectangle(0, 0, 2560, 1400)),
            new(2, "LG HDR 4K", new Rectangle(-3840, 0, 3840, 2120)),
            new(3, "Generic PnP Monitor", new Rectangle(0, -1080, 1920, 1040)),
        };

        private const int Left = 8, Top = 31;

        [Theory]
        [InlineData(null, -1, false)]
        [InlineData("", -1, false)]
        [InlineData("  ", -1, false)]
        [InlineData("0", 0, false)]
        [InlineData("1", 1, false)]
        [InlineData(" 2 ", 2, false)]
        [InlineData("3", 0, true)]
        [InlineData("99", 0, true)]
        [InlineData("lg hdr 4k", 1, false)]
        [InlineData("PnP", 2, false)]
        [InlineData("no such monitor", 0, true)]
        public void ResolveDisplay_IndexOrName_ElseThePrimary(string choice, int expected, bool fellBack)
        {
            WindowPlacement.ResolveDisplay(choice, Displays, 0, out bool fb).Should().Be(expected);
            fb.Should().Be(fellBack);
        }

        [Fact]
        public void ResolveDisplay_OutOfRange_FallsBackToThePrimary_WhereverItIsListed()
        {
            WindowPlacement.ResolveDisplay("7", Displays, 2, out bool fb).Should().Be(2);
            fb.Should().BeTrue();
        }

        [Fact]
        public void ResolveDisplay_ExactNameWins_OverAContainingOne()
        {
            DisplayInfo[] displays =
            {
                new(1, "Monitor 2", new Rectangle(0, 0, 100, 100)),
                new(2, "Monitor", new Rectangle(100, 0, 100, 100)),
            };

            WindowPlacement.ResolveDisplay("monitor", displays, 0, out _).Should().Be(1);
        }

        [Fact]
        public void PlaceSaved_KeepsNegativeCoordinates_OnAMonitorLeftOfThePrimary()
        {
            WindowPlacement.PlaceSaved(new Point(-3000, 200), Left, Top, Displays, 0).Should().Be(new Point(-3000 + Left, 200 + Top));
        }

        [Fact]
        public void PlaceSaved_KeepsNegativeCoordinates_OnAMonitorAboveThePrimary()
        {
            WindowPlacement.PlaceSaved(new Point(100, -900), Left, Top, Displays, 0).Should().Be(new Point(100 + Left, -900 + Top));
        }

        [Fact]
        public void PlaceSaved_KeepsTheTitleBarOnTheDisplay()
        {
            // saved at the left monitor's left edge: the border would be off screen
            WindowPlacement.PlaceSaved(new Point(-3840 - Left, 0), Left, Top, Displays, 0).Should().Be(new Point(-3840 + Left, Top));
        }

        [Fact]
        public void PlaceSaved_OnNoDisplay_OpensAtThePrimary()
        {
            WindowPlacement.PlaceSaved(new Point(-9000, 50), Left, Top, Displays, 0).Should().Be(new Point(Left, Top));
            WindowPlacement.PlaceSaved(new Point(5000, 5000), Left, Top, Displays, 1).Should().Be(new Point(-3840 + Left, Top));
        }

        [Fact]
        public void PlaceOnDisplay_CentresTheWindow_OnTheChosenMonitor()
        {
            WindowPlacement.PlaceOnDisplay(null, Left, Top, 1280, 720, Displays[1])
                .Should().Be(new Point(-3840 + (3840 - 1280) / 2, (2120 - 720) / 2));
        }

        [Fact]
        public void PlaceOnDisplay_KeepsASavedSpot_OnThatMonitor_ElseCentres()
        {
            WindowPlacement.PlaceOnDisplay(new Point(-2000, 300), Left, Top, 1280, 720, Displays[1]).Should().Be(new Point(-2000 + Left, 300 + Top));
            // saved on the primary, but the left monitor is chosen
            WindowPlacement.PlaceOnDisplay(new Point(200, 300), Left, Top, 1280, 720, Displays[1])
                .Should().Be(new Point(-3840 + (3840 - 1280) / 2, (2120 - 720) / 2));
        }

        [Fact]
        public void PlaceOnDisplay_AWindowLargerThanTheMonitor_StartsAtItsTopLeft()
        {
            WindowPlacement.PlaceOnDisplay(null, Left, Top, 2560, 1440, Displays[2]).Should().Be(new Point(Left, -1080 + Top));
        }
    }
}
