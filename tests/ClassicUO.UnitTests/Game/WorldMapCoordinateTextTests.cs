using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// perf-alloc-4: the world map's coordinate captions are cached instead of formatted every frame. The text must match
    /// the per-frame formatting it replaced, and a new string is built only when an input changes. The sextant line needs
    /// the loaded map sizes (Client.Game), so these cases run with the sextant off.
    /// </summary>
    public sealed class WorldMapCoordinateTextTests
    {
        // The formatting WorldMapGump did every frame (ushort X/Y, sbyte Z), without the sextant line.
        private static string OldPlayer(ushort x, ushort y, sbyte z, int zoomIndex) => $"{x}, {y} ({z}) [{zoomIndex}]";

        private static string OldMouse(int x, int y) => $"{x} {y}";

        [Theory]
        [InlineData(1443, 1690, 0, 4, 0)]
        [InlineData(5445, 1153, -5, 9, 1)]
        [InlineData(0, 0, 127, 0, 5)]
        [InlineData(6000, 4000, -128, 2, 2)]
        [InlineData(65535, 65535, -1, 9, 0)]
        public void Player_matches_the_old_formatting(int x, int y, int z, int zoomIndex, int mapIndex)
        {
            var cache = new WorldMapCoordinateText();

            cache.Player(x, y, z, zoomIndex, mapIndex, false)
                .Should().Be(OldPlayer((ushort)x, (ushort)y, (sbyte)z, zoomIndex));
        }

        [Theory]
        [InlineData(1443, 1690, 0)]
        [InlineData(-3, 7000, 1)]
        [InlineData(5445, 1153, 3)]
        public void Mouse_matches_the_old_formatting(int x, int y, int mapIndex)
        {
            new WorldMapCoordinateText().Mouse(x, y, mapIndex, false).Should().Be(OldMouse(x, y));
        }

        [Fact]
        public void Player_reuses_the_string_until_an_input_changes()
        {
            var cache = new WorldMapCoordinateText();
            string first = cache.Player(1443, 1690, 0, 4, 0, false);

            cache.Player(1443, 1690, 0, 4, 0, false).Should().BeSameAs(first);

            cache.Player(1444, 1690, 0, 4, 0, false).Should().Be("1444, 1690 (0) [4]");
            cache.Player(1444, 1691, 0, 4, 0, false).Should().Be("1444, 1691 (0) [4]");
            cache.Player(1444, 1691, 5, 4, 0, false).Should().Be("1444, 1691 (5) [4]");
            cache.Player(1444, 1691, 5, 6, 0, false).Should().Be("1444, 1691 (5) [6]");
            string beforeMapChange = cache.Player(1444, 1691, 5, 6, 0, false);
            cache.Player(1444, 1691, 5, 6, 1, false).Should().Be("1444, 1691 (5) [6]").And.NotBeSameAs(beforeMapChange);
        }

        [Fact]
        public void Mouse_reuses_the_string_until_an_input_changes()
        {
            var cache = new WorldMapCoordinateText();
            string first = cache.Mouse(100, 200, 0, false);

            cache.Mouse(100, 200, 0, false).Should().BeSameAs(first);
            cache.Mouse(101, 200, 0, false).Should().Be("101 200");
            string second = cache.Mouse(101, 201, 0, false);
            second.Should().Be("101 201");
            cache.Mouse(101, 201, 2, false).Should().Be("101 201").And.NotBeSameAs(second);
        }
    }
}
