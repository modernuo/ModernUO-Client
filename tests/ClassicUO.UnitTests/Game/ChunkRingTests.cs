using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    public class ChunkRingTests
    {
        private static List<(int, int)> Cells(int minCx, int minCy, int maxCx, int maxCy, int dx, int dy)
        {
            var list = new List<(int, int)>();
            int n = ClassicUO.Game.Map.Map.RingCellCount(minCx, minCy, maxCx, maxCy, dx, dy);

            for (int i = 0; i < n; i++)
            {
                ClassicUO.Game.Map.Map.RingCell(i, minCx, minCy, maxCx, maxCy, dx, dy, out int cx, out int cy);
                list.Add((cx, cy));
            }

            return list;
        }

        [Fact]
        public void EastStep_ColumnBeyondMaxX_OneWiderEachWay()
        {
            Cells(10, 20, 14, 22, 1, 0).Should().Equal((15, 19), (15, 20), (15, 21), (15, 22), (15, 23));
        }

        [Fact]
        public void NorthStep_RowBeforeMinY()
        {
            Cells(10, 20, 12, 22, 0, -1).Should().Equal((9, 19), (10, 19), (11, 19), (12, 19), (13, 19));
        }

        [Fact]
        public void DiagonalStep_ColumnAndRow()
        {
            var cells = Cells(10, 20, 11, 21, -1, 1);
            cells.Should().HaveCount(8);
            cells.Should().Contain((9, 19)).And.Contain((9, 22)).And.Contain((12, 22));
        }

        [Fact]
        public void NoStep_NoCells()
        {
            Cells(10, 20, 14, 22, 0, 0).Should().BeEmpty();
        }

        [Fact]
        public void Switch_FollowsOverride_BothStates()
        {
            try
            {
                ClassicUO.Game.Map.Map.RingOverrideForTests = true;
                ClassicUO.Game.Map.Map.RingPrefetch.Should().BeTrue();
                ClassicUO.Game.Map.Map.RingOverrideForTests = false;
                ClassicUO.Game.Map.Map.RingPrefetch.Should().BeFalse();
            }
            finally
            {
                ClassicUO.Game.Map.Map.RingOverrideForTests = null;
            }
        }
    }
}
