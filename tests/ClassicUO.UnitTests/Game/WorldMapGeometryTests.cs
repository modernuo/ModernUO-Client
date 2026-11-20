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

        /// <summary>
        /// worldmap-audit P-3: GameScene's loop hook and WorldMapEntityManager.Enabled read a static open count (set when the
        /// gump is constructed, cleared after its Dispose) instead of walking the gump list; a second Dispose or a second
        /// note does not count twice.
        /// </summary>
        [Fact]
        public void Open_Count_Follows_Open_And_Close_Once_Each()
        {
            int start = WorldMapGump.OpenCount;
            bool a = false, b = false;

            WorldMapGump.NoteOpened(ref a);
            WorldMapGump.NoteOpened(ref a);
            WorldMapGump.OpenCount.Should().Be(start + 1);
            WorldMapGump.IsOpen.Should().BeTrue();

            WorldMapGump.NoteOpened(ref b);
            WorldMapGump.OpenCount.Should().Be(start + 2);

            WorldMapGump.NoteClosed(ref a);
            WorldMapGump.NoteClosed(ref a);
            WorldMapGump.OpenCount.Should().Be(start + 1);

            WorldMapGump.NoteClosed(ref b);
            WorldMapGump.OpenCount.Should().Be(start);
            a.Should().BeFalse();
            b.Should().BeFalse();
        }

        /// <summary>
        /// worldmap-audit P-4: DrawMarker drops far markers before projecting them. The cull must never drop a marker the
        /// old bounds test (RotatePoint, then MarkerInView) would draw, flipped or not, at every zoom and gump size; and it
        /// must actually drop most far markers.
        /// </summary>
        [Fact]
        public void Marker_Cull_Never_Drops_A_Marker_The_Bounds_Test_Keeps()
        {
            var rng = new Random(4321);
            int culled = 0, total = 0;

            foreach (float zoom in new[] { 0.125f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 4f, 6f, 8f })
            {
                foreach (int width in new[] { 108, 200, 401, 1000, 2560 })
                {
                    foreach (int height in new[] { 108, 333, 768, 1440 })
                    {
                        int innerWidth = width - 8, innerHeight = height - 8;
                        int halfWidth = innerWidth >> 1, halfHeight = innerHeight >> 1;
                        const int gX = 37, gY = 59;
                        int reach = (int) (Math.Max(width, height) * 2 / zoom) + 4;

                        for (int i = 0; i < 4000; i++)
                        {
                            int sx = rng.Next(-reach, reach + 1), sy = rng.Next(-reach, reach + 1);
                            bool cull = WorldMapGump.MarkerOutsideView(sx, sy, zoom, halfWidth, halfHeight, innerWidth, innerHeight);
                            total++;

                            if (!cull)
                            {
                                continue;
                            }

                            culled++;

                            foreach (float angle in new[] { 45f, 0f })
                            {
                                Point rot = WorldMapGump.RotatePoint(sx, sy, zoom, 1, angle);
                                bool inView = WorldMapGump.MarkerInView(rot.X + gX + halfWidth, rot.Y + gY + halfHeight, gX, gY, innerWidth, innerHeight);
                                inView.Should().BeFalse($"sx {sx} sy {sy} zoom {zoom} size {width}x{height} angle {angle}");
                            }
                        }
                    }
                }
            }

            culled.Should().BeGreaterThan(total / 3, "far markers are dropped before the projection");
        }
    }
}
