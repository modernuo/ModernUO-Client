using System;
using System.Collections.Generic;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    public class RenderGeometryTests
    {
        public static readonly float[] Scales = { 1f, 1.25f, 1.5f, 2f };

        // window/viewport sizes 600..1000 in both axes, odd and even, plus the owner's pair
        public static IEnumerable<(int W, int H)> Sizes()
        {
            for (int w = 600; w <= 1000; w += 37)
            {
                for (int h = 600; h <= 1000; h += 41)
                {
                    yield return (w, h);
                }
            }

            yield return (854, 705);
            yield return (856, 704);
            yield return (853, 705);
            yield return (855, 703);
            yield return (1000, 1000);
            yield return (600, 600);
        }

        [Fact]
        public void UiTarget_CoversTheBackBufferExactly()
        {
            foreach (float s in Scales)
            {
                foreach (var (w, h) in Sizes())
                {
                    var g = RenderGeometry.Compute(0, 0, w, h, s, new Rectangle(0, 0, 100, 100));

                    Assert.Equal((double)w, g.UiWidth * g.ScaleX, 6);
                    Assert.Equal((double)h, g.UiHeight * g.ScaleY, 6);
                    Assert.InRange(Math.Abs(g.ScaleX - s), 0, s / g.UiWidth + 1e-6);
                    Assert.InRange(Math.Abs(g.ScaleY - s), 0, s / g.UiHeight + 1e-6);
                }
            }
        }

        [Fact]
        public void WorldOnScreen_IsTheViewportScaledAndRounded()
        {
            foreach (float s in Scales)
            {
                foreach (var (w, h) in Sizes())
                {
                    int bw = (int)Math.Ceiling((w + 40) * s), bh = (int)Math.Ceiling((h + 40) * s);
                    var vp = new Rectangle(15, 17, w, h);
                    var g = RenderGeometry.Compute(bw, bh, bw, bh, s, vp);
                    Rectangle r = g.WorldOnScreen;

                    Assert.InRange(Math.Abs(r.X - vp.X * g.ScaleX), 0, 0.5);
                    Assert.InRange(Math.Abs(r.Y - vp.Y * g.ScaleY), 0, 0.5);
                    Assert.InRange(Math.Abs(r.Right - vp.Right * g.ScaleX), 0, 0.5);
                    Assert.InRange(Math.Abs(r.Bottom - vp.Bottom * g.ScaleY), 0, 0.5);
                    Assert.Equal(w, g.WorldWidth);
                    Assert.Equal(h, g.WorldHeight);
                }
            }
        }

        [Fact]
        public void WorldTarget_RoundTripsThroughTheComposite()
        {
            var g = RenderGeometry.Compute(1281, 1058, 1281, 1058, 1.5f, new Rectangle(10, 10, 854, 705));

            foreach (var p in new[] { new Vector2(0, 0), new Vector2(853.5f, 704.5f), new Vector2(427, 352.5f) })
            {
                Vector2 b = g.WorldTargetToBackBuffer(p.X, p.Y);
                Vector2 back = g.BackBufferToWorldTarget(b.X, b.Y);

                Assert.Equal(p.X, back.X, 3);
                Assert.Equal(p.Y, back.Y, 3);
            }
        }

        [Fact]
        public void Window_MapsToTheBackBufferWhenTheyDiffer()
        {
            // a window of 1440x1080 showing a 1281x1058 back buffer (a size change in flight): the mouse maps by the ratio
            var g = RenderGeometry.Compute(1440, 1080, 1281, 1058, 1.5f, new Rectangle(0, 0, 640, 480));
            Vector2 b = g.WindowToBackBuffer(1440, 1080);

            Assert.Equal(1281f, b.X, 3);
            Assert.Equal(1058f, b.Y, 3);
            Assert.Equal(new Point(g.UiWidth, g.UiHeight), g.WindowToUi(1440, 1080));
        }

        [Fact]
        public void Compute_ClampsBadInputs()
        {
            var g = RenderGeometry.Compute(0, 0, 0, 0, 0f, new Rectangle(0, 0, -5, -5));

            Assert.True(g.IsEmpty);
            Assert.Equal(1f, g.Scale);
            Assert.Equal(0, g.UiWidth);
            Assert.Equal(0, g.WorldWidth);
        }

        [Fact]
        public void Equality_FollowsTheInputs()
        {
            var a = RenderGeometry.Compute(1281, 1058, 1281, 1058, 1.5f, new Rectangle(10, 10, 854, 705));
            var b = RenderGeometry.Compute(1281, 1058, 1281, 1058, 1.5f, new Rectangle(10, 10, 854, 705));

            Assert.True(a == b);
            Assert.True(a != a.WithViewport(new Rectangle(10, 10, 856, 704)));
        }
    }
}
