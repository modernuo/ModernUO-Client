using System;
using System.Collections.Generic;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    /// <summary>
    /// By construction, over window and viewport sizes 600..1000 x scales 1, 1.25, 1.5 and 2, windowed, borderless (a window
    /// whose back buffer has not followed yet) and fullscreen: a light and a world sprite at the same world position land on
    /// the same composited pixel, a click maps back to the world pixel under it, and a gump control receives a click at its
    /// window pixel. Each mapping is the production one: Camera's matrix (FNA passes: lights, Legacy), Camera.GpuCenter and
    /// WorldRenderer.SnapCenter (the Gpu world), RenderGeometry (composite, mouse) and GameScene.MouseToWorld (picking).
    /// </summary>
    public class RenderGeometryConstructionTests
    {
        private static readonly float[] Scales = { 1f, 1.25f, 1.5f, 2f };
        private static readonly float[] Zooms = { 1f, 1.25f, 0.75f };

        private static IEnumerable<(int W, int H)> ViewportSizes()
        {
            for (int w = 600; w <= 1000; w += 53)
            {
                for (int h = 600; h <= 1000; h += 59)
                {
                    yield return (w, h);
                }
            }

            yield return (854, 705);
            yield return (856, 704);
            yield return (853, 705);
            yield return (855, 703);
            yield return (1000, 1000);
        }

        // windowed: the back buffer fits the viewport plus a margin; borderless: the window is a display mode but the back
        // buffer is still the windowed one (FNA resizes it after the window event); fullscreen: both the display mode
        private static IEnumerable<(string Mode, int WinW, int WinH, int BackW, int BackH)> Windows(int w, int h, float scale)
        {
            int bw = (int)Math.Ceiling((w + 37) * scale), bh = (int)Math.Ceiling((h + 41) * scale);
            yield return ("windowed", bw, bh, bw, bh);
            yield return ("borderless", bw + 157, bh + 93, bw, bh);
            yield return ("fullscreen", 2560, 1440, 2560, 1440);
        }

        private static Camera MakeCamera(Rectangle bounds, float zoom, int offX, int offY)
        {
            var camera = new Camera(0.5f, 2.5f, 0.1f) { Bounds = bounds };
            camera.Zoom = zoom;
            camera.SetWorldOffset(offX, offY);
            camera.Update(true, 0f, Point.Zero);

            return camera;
        }

        [Fact]
        public void LightAndGpuWorldSprite_LandOnTheSameCompositedPixel()
        {
            int failures = 0, checks = 0;
            string first = null;

            foreach (float scale in Scales)
            foreach (float zoom in Zooms)
            foreach (var (w, h) in ViewportSizes())
            foreach (var win in Windows(w, h, scale))
            {
                var viewport = new Rectangle(17, 23, w, h);
                var g = RenderGeometry.Compute(win.WinW, win.WinH, win.BackW, win.BackH, scale, viewport);
                // GetViewPort's offset: the player's pixel minus the viewport centre (integer halves)
                int offX = 40404 - (w >> 1), offY = 81818 - (h >> 1);
                Camera camera = MakeCamera(viewport, zoom, offX, offY);
                Matrix m = camera.ViewTransformMatrix;
                var (cx, cy, s) = camera.GpuCenter(g.WorldWidth, g.WorldHeight);
                double scx = WorldRenderer.SnapCenter(cx, g.WorldWidth, s), scy = WorldRenderer.SnapCenter(cy, g.WorldHeight, s);

                for (int k = 0; k < 5; k++)
                {
                    int wx = offX + k * w / 4, wy = offY + k * h / 4;

                    // the light pass: DrawX = world - offset through the batcher's matrix; the target viewport is the target
                    float lx = (wx - offX) * m.M11 + m.M41, ly = (wy - offY) * m.M22 + m.M42;
                    // the Gpu world: the vertex shader's viewport / 2 + (world - centre) * scale
                    float gx = (float)(g.WorldWidth * 0.5 + (wx - scx) * s), gy = (float)(g.WorldHeight * 0.5 + (wy - scy) * s);

                    Vector2 a = g.WorldTargetToBackBuffer(lx, ly), b = g.WorldTargetToBackBuffer(gx, gy);
                    checks++;

                    if (Math.Abs(a.X - b.X) > 0.5f || Math.Abs(a.Y - b.Y) > 0.5f)
                    {
                        failures++;
                        first ??= FormattableString.Invariant($"{win.Mode} {w}x{h} scale {scale} zoom {zoom}: light {a} world {b}");
                    }
                }
            }

            Assert.True(failures == 0, $"{failures}/{checks} light vs world mismatches over 0.5 px; first: {first}");
        }

        [Fact]
        public void Click_MapsBackToTheWorldPixelUnderIt()
        {
            int failures = 0, checks = 0;
            string first = null;

            foreach (float scale in Scales)
            foreach (float zoom in Zooms)
            foreach (var (w, h) in ViewportSizes())
            foreach (var win in Windows(w, h, scale))
            {
                var viewport = new Rectangle(17, 23, w, h);
                var g = RenderGeometry.Compute(win.WinW, win.WinH, win.BackW, win.BackH, scale, viewport);
                int offX = 40404 - (w >> 1), offY = 81818 - (h >> 1);
                Camera camera = MakeCamera(viewport, zoom, offX, offY);
                Matrix m = camera.ViewTransformMatrix;
                // one world pixel per screen pixel at most is resolvable: compare in target pixels
                float tol = Math.Max(1f, 1f / m.M11) + 1f;

                for (int k = 1; k < 4; k++)
                {
                    // a tile's centre pixel, relative to the draw offset
                    int px = k * w / 4, py = k * h / 4;
                    float tx = px * m.M11 + m.M41, ty = py * m.M22 + m.M42;
                    Vector2 onScreen = g.WorldTargetToBackBuffer(tx + 0.5f, ty + 0.5f);
                    // the OS reports the click in window coordinates
                    float wx = onScreen.X * g.WindowWidth / g.BackBufferWidth, wy = onScreen.Y * g.WindowHeight / g.BackBufferHeight;
                    Vector2 b = g.WindowToBackBuffer(wx, wy);
                    Vector2 ui = g.BackBufferToUi(b.X, b.Y);
                    Point world = GameScene.MouseToWorld(g, camera, ui);
                    checks++;

                    // a tile is 44 x 44 world pixels: the click must land within a pixel or two of the drawn pixel
                    if (Math.Abs(world.X - px) > tol || Math.Abs(world.Y - py) > tol)
                    {
                        failures++;
                        first ??= FormattableString.Invariant($"{win.Mode} {w}x{h} scale {scale} zoom {zoom}: drawn {px},{py} picked {world}");
                    }
                }
            }

            Assert.True(failures == 0, $"{failures}/{checks} picks off by more than a pixel; first: {first}");
        }

        [Fact]
        public void GumpControl_ReceivesAClickAtItsWindowPixels()
        {
            int failures = 0, checks = 0;
            string first = null;

            foreach (float scale in Scales)
            foreach (var (w, h) in ViewportSizes())
            foreach (var win in Windows(w, h, scale))
            {
                var g = RenderGeometry.Compute(win.WinW, win.WinH, win.BackW, win.BackH, scale, new Rectangle(0, 0, w, h));
                // a 20 x 10 control at the UI target's right and bottom edge, and one in the middle
                var controls = new[]
                {
                    new Rectangle(g.UiWidth - 20, g.UiHeight - 10, 20, 10),
                    new Rectangle(g.UiWidth / 2, g.UiHeight / 2, 20, 10),
                    new Rectangle(0, 0, 20, 10)
                };

                foreach (Rectangle c in controls)
                {
                    // every back-buffer pixel the composite fills from the control's UI units
                    Vector2 p0 = g.UiToBackBuffer(c.X, c.Y), p1 = g.UiToBackBuffer(c.Right, c.Bottom);

                    for (int py = (int)Math.Ceiling(p0.Y - 0.5f); py + 0.5f < p1.Y; py += Math.Max(1, (int)((p1.Y - p0.Y) / 3)))
                    for (int px = (int)Math.Ceiling(p0.X - 0.5f); px + 0.5f < p1.X; px += Math.Max(1, (int)((p1.X - p0.X) / 3)))
                    {
                        float wx = (px + 0.5f) * g.WindowWidth / g.BackBufferWidth, wy = (py + 0.5f) * g.WindowHeight / g.BackBufferHeight;
                        Point ui = g.WindowToUi(wx, wy);
                        checks++;

                        if (!c.Contains(ui))
                        {
                            failures++;
                            first ??= FormattableString.Invariant($"{win.Mode} {w}x{h} scale {scale}: control {c} pixel {px},{py} -> {ui}");
                        }
                    }
                }
            }

            Assert.True(failures == 0, $"{failures}/{checks} clicks outside their control; first: {first}");
        }

        [Fact]
        public void WorldAndLightTargets_ShareOneSizeAndCompositeRect()
        {
            foreach (float scale in Scales)
            foreach (var (w, h) in ViewportSizes())
            {
                var g = RenderGeometry.Compute(0, 0, (int)Math.Ceiling((w + 40) * scale), (int)Math.Ceiling((h + 40) * scale), scale, new Rectangle(15, 15, w, h));

                Assert.Equal(w, g.WorldWidth);
                Assert.Equal(h, g.WorldHeight);
                Assert.True(g.WorldOnScreen.Width > 0 && g.WorldOnScreen.Height > 0);
            }
        }

        [Fact]
        public void ViewportPartlyOffWindow_DrawsAndPicksTheVisiblePart()
        {
            foreach (float scale in new[] { 1f, 1.5f })
            {
                int bw = (int)Math.Ceiling(1280 * scale), bh = (int)Math.Ceiling(900 * scale);
                var g0 = RenderGeometry.Compute(bw, bh, bw, bh, scale, Rectangle.Empty);
                const int w = 854, h = 705;
                // gump placements (border included) off each edge and corner, past the clamp, then clamped as the gump does
                var asked = new[]
                {
                    new Point(-700, 50), new Point(g0.UiWidth - 40, 50), new Point(50, -600), new Point(50, g0.UiHeight - 10),
                    new Point(-5000, -5000), new Point(5000, -5000), new Point(-5000, 5000), new Point(5000, 5000), new Point(-300, -200)
                };

                foreach (Point ask in asked)
                {
                    int gw = w + 10, gh = h + 10;
                    Point at = RenderGeometry.ClampPlacement(ask, gw, gh, g0.UiWidth, g0.UiHeight);
                    var gump = new Rectangle(at.X, at.Y, gw, gh);
                    var visible = Rectangle.Intersect(gump, g0.UiBounds);

                    Assert.True(visible.Width >= RenderGeometry.ViewportKeepVisible && visible.Height >= RenderGeometry.ViewportKeepVisible,
                        $"scale {scale} ask {ask}: only {visible} of the viewport gump stays on screen");

                    var viewport = new Rectangle(at.X + 5, at.Y + 5, w, h);
                    var g = g0.WithViewport(viewport);

                    // the passes' device viewport is the target's bounds (FNA rejects negative or out-of-target viewports)
                    Rectangle vp = g.WorldTargetBounds;
                    Assert.True(vp.X == 0 && vp.Y == 0 && vp.Width == w && vp.Height == h);

                    Camera camera = MakeCamera(viewport, 1f, 1000 - (w >> 1), 2000 - (h >> 1));
                    Matrix m = camera.ViewTransformMatrix;
                    Rectangle onScreen = Rectangle.Intersect(g.WorldOnScreen, g.BackBufferBounds);

                    for (int k = 0; k < 9; k++)
                    {
                        // a back-buffer pixel inside the visible part of the world composite
                        int px = onScreen.X + (k % 3 + 1) * onScreen.Width / 4, py = onScreen.Y + (k / 3 + 1) * onScreen.Height / 4;

                        if (onScreen.Width <= 0 || onScreen.Height <= 0)
                        {
                            break;
                        }

                        Vector2 t = g.BackBufferToWorldTarget(px + 0.5f, py + 0.5f);
                        Vector2 ui = g.BackBufferToUi(px + 0.5f, py + 0.5f);
                        Point world = GameScene.MouseToWorld(g, camera, ui);
                        // the world pixel drawn at that target pixel
                        float wx = (t.X - m.M41) / m.M11, wy = (t.Y - m.M42) / m.M22;

                        Assert.InRange(Math.Abs(world.X - wx), 0, 2);
                        Assert.InRange(Math.Abs(world.Y - wy), 0, 2);
                    }
                }
            }
        }

        // The same checks with the formulas of the code before RenderGeometry (b0feb5877), kept as models: they must fail where
        // that code was wrong, at least the owner's 854x705 viewport at scale 1.5.

        // base: GetGpuCamera centre = offset + viewport / 2 (+ peek), Uo.Render rounded the centre (not the screen origin)
        private static double BaseGpuScreen(int world, int offset, int viewport, float zoom)
        {
            float s = 1f / zoom;
            double center = offset + viewport * 0.5;
            double snapped = Math.Round(center * s) / s;

            return viewport * 0.5 + (world - snapped) * s;
        }

        // base: Camera matrix without snapping, p = world - offset, (p - c) * s + c
        private static double BaseLightScreen(int world, int offset, int viewport, float zoom)
        {
            float s = 1f / zoom;
            double c = viewport * 0.5;

            return (world - offset - c) * s + c;
        }

        [Fact]
        public void BaseCode_LightAndGpuWorld_DisagreeAt854x705Scale15()
        {
            const int w = 854, h = 705;
            const float scale = 1.5f;
            int offY = 81818 - (h >> 1);
            // base composite rect: (int)(Bounds / (1 / dpi)), the same for both layers
            int rectH = (int)(h / (1 / scale));
            double light = BaseLightScreen(offY + 100, offY, h, 1f), gpu = BaseGpuScreen(offY + 100, offY, h, 1f);
            double screenDelta = Math.Abs(light - gpu) * rectH / h;

            Assert.True(screenDelta > 0.5, $"base light vs Gpu world: {screenDelta} px");

            // Math.Round rounds the half-pixel centre to even: the error's sign flips with the offset's parity, so the Gpu
            // world jitters against the lights by a target pixel while walking
            double d0 = BaseLightScreen(offY + 100, offY, h, 1f) - BaseGpuScreen(offY + 100, offY, h, 1f);
            double d1 = BaseLightScreen(offY + 101, offY + 1, h, 1f) - BaseGpuScreen(offY + 101, offY + 1, h, 1f);
            Assert.Equal(1.0, Math.Abs(d0 - d1), 6);
            Assert.Equal(0.0, Math.Abs(BaseLightScreen(offY + 100, offY, 704, 1f) - BaseGpuScreen(offY + 100, offY, 704, 1f)), 6);
        }

        [Fact]
        public void BaseCode_MouseMapping_MissesControls()
        {
            int failures = 0;

            foreach (var (mode, winW, winH, backW, backH) in Windows(854, 705, 1.5f))
            {
                // base: UI target (int)(back buffer / dpi) stretched over the back buffer; mouse
                // (int)(x * (backW / winW) / dpi) with an integer division of the sizes
                int uiW = (int)(backW / 1.5f), uiH = (int)(backH / 1.5f);
                var c = new Rectangle(uiW - 20, uiH - 10, 20, 10);
                double sx = (double)backW / uiW, sy = (double)backH / uiH;
                float px = (float)((c.X + 10.5) * sx), py = (float)((c.Y + 5.5) * sy);
                float wx = px * winW / backW, wy = py * winH / backH;
                var ui = new Point((int)(wx * (backW / winW) / 1.5f), (int)(wy * (backH / winH) / 1.5f));

                if (!c.Contains(ui))
                {
                    failures++;
                }
            }

            Assert.True(failures > 0);
        }
    }
}
