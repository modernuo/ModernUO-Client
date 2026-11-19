using System;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// worldmap-audit P-2: RotatePoint uses precomputed cos/sin of +-PI/4 instead of two trig calls per drawn item; the
    /// result must be bit-identical to the old per-call formula.
    /// </summary>
    public sealed class WorldMapGeometryTests
    {
        private static readonly float[] Zooms = { 0.125f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 4f, 6f, 8f, 1f / 0.125f, 1f / 0.75f, 1f / 6f };

        private static Point OldRotatePoint(int x, int y, float zoom, int dist, float angle)
        {
            x = (int) (x * zoom);
            y = (int) (y * zoom);

            if (angle == 0.0f)
            {
                return new Point(x, y);
            }

            double cos = Math.Cos(dist * Math.PI / 4.0);
            double sin = Math.Sin(dist * Math.PI / 4.0);

            return new Point((int) Math.Round(cos * x - sin * y), (int) Math.Round(sin * x + cos * y));
        }

        [Fact]
        public void RotatePoint_Matches_The_Per_Call_Trig_Formula()
        {
            var rng = new Random(1234);

            foreach (float zoom in Zooms)
            {
                foreach (int dist in new[] { 1, -1, 2 })
                {
                    foreach (float angle in new[] { 45f, 0f })
                    {
                        for (int x = -300; x <= 300; x += 7)
                        {
                            for (int y = -300; y <= 300; y += 11)
                            {
                                WorldMapGump.RotatePoint(x, y, zoom, dist, angle).Should().Be(OldRotatePoint(x, y, zoom, dist, angle));
                            }
                        }

                        for (int i = 0; i < 2000; i++)
                        {
                            int x = rng.Next(-8000, 8000), y = rng.Next(-8000, 8000);
                            WorldMapGump.RotatePoint(x, y, zoom, dist, angle).Should().Be(OldRotatePoint(x, y, zoom, dist, angle));
                        }
                    }
                }
            }
        }
    }
}
