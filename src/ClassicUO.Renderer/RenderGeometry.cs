// SPDX-License-Identifier: BSD-2-Clause

using System;
using Microsoft.Xna.Framework;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// The one source of the client's render geometry: the window, the back buffer, the DPI scale, the UI target, the game
    /// viewport and the world and light targets, and the mappings between them. Computed once per change of its inputs
    /// (window or back buffer size, DPI or screen scale, window mode, game viewport rect); every size, composite rect and
    /// coordinate mapping derives from it, so the world, its lights, the UI and the mouse cannot disagree.
    /// <para>
    /// Units: <i>window</i> = the OS window's client coordinates (SDL window size, mouse events); <i>back buffer</i> = the
    /// pixels the frame is composited into; <i>UI</i> = gump and game-viewport coordinates, one UI unit = <see cref="ScaleX"/>
    /// by <see cref="ScaleY"/> back-buffer pixels; <i>world target</i> = pixels of the world and light targets, one per UI unit
    /// of the viewport (the camera zoom applies inside them).
    /// </para>
    /// <para>
    /// The UI target is the back buffer divided by the requested scale, rounded to whole UI units, and the per-axis scales are
    /// the exact ratios back buffer / UI target, so the UI composite covers the back buffer exactly at any size. The world and
    /// light composite rect is the viewport's edges mapped with those scales and rounded: the same rect for both targets, never
    /// truncated per field.
    /// </para>
    /// </summary>
    public readonly struct RenderGeometry : IEquatable<RenderGeometry>
    {
        public readonly int WindowWidth, WindowHeight;
        public readonly int BackBufferWidth, BackBufferHeight;
        /// <summary>The requested UI scale (display scale x screen scale).</summary>
        public readonly float Scale;
        /// <summary>The game viewport in UI units (Camera.Bounds).</summary>
        public readonly Rectangle Viewport;

        /// <summary>The UI target's size in UI units (the client's logical bounds).</summary>
        public readonly int UiWidth, UiHeight;
        /// <summary>Back-buffer pixels per UI unit, per axis (exact).</summary>
        public readonly double ScaleX, ScaleY;
        /// <summary>Where the world, light and fade layers are drawn in the back buffer.</summary>
        public readonly Rectangle WorldOnScreen;

        private RenderGeometry(int windowWidth, int windowHeight, int backBufferWidth, int backBufferHeight, float scale, Rectangle viewport)
        {
            WindowWidth = windowWidth;
            WindowHeight = windowHeight;
            BackBufferWidth = backBufferWidth;
            BackBufferHeight = backBufferHeight;
            Scale = scale;
            Viewport = viewport;

            UiWidth = UiUnits(backBufferWidth, scale);
            UiHeight = UiUnits(backBufferHeight, scale);
            ScaleX = backBufferWidth > 0 ? (double)backBufferWidth / UiWidth : scale;
            ScaleY = backBufferHeight > 0 ? (double)backBufferHeight / UiHeight : scale;

            int x0 = (int)Math.Round(viewport.X * ScaleX), x1 = (int)Math.Round((viewport.X + viewport.Width) * ScaleX);
            int y0 = (int)Math.Round(viewport.Y * ScaleY), y1 = (int)Math.Round((viewport.Y + viewport.Height) * ScaleY);
            WorldOnScreen = new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// <paramref name="windowWidth"/>/<paramref name="windowHeight"/>: the window client size in window coordinates (0 =
        /// same as the back buffer); <paramref name="viewport"/>: the game viewport in UI units.
        /// </summary>
        public static RenderGeometry Compute(int windowWidth, int windowHeight, int backBufferWidth, int backBufferHeight, float scale, Rectangle viewport)
        {
            if (!(scale > 0f) || float.IsInfinity(scale))
            {
                scale = 1f;
            }

            backBufferWidth = Math.Max(0, backBufferWidth);
            backBufferHeight = Math.Max(0, backBufferHeight);

            return new RenderGeometry(
                windowWidth > 0 ? windowWidth : backBufferWidth,
                windowHeight > 0 ? windowHeight : backBufferHeight,
                backBufferWidth,
                backBufferHeight,
                scale,
                new Rectangle(viewport.X, viewport.Y, Math.Max(0, viewport.Width), Math.Max(0, viewport.Height))
            );
        }

        /// <summary>The same window, back buffer and scale with another game viewport.</summary>
        public RenderGeometry WithViewport(Rectangle viewport) =>
            Compute(WindowWidth, WindowHeight, BackBufferWidth, BackBufferHeight, Scale, viewport);

        /// <summary>A length in back-buffer pixels as whole UI units (at least 1 for a non-empty length).</summary>
        public static int UiUnits(int pixels, float scale) => pixels <= 0 ? 0 : Math.Max(1, (int)Math.Round(pixels / (double)scale));

        public bool IsEmpty => BackBufferWidth <= 0 || BackBufferHeight <= 0;

        /// <summary>The world and light targets' size (one pixel per viewport UI unit).</summary>
        public int WorldWidth => Viewport.Width;

        public int WorldHeight => Viewport.Height;

        public Rectangle UiBounds => new Rectangle(0, 0, UiWidth, UiHeight);

        public Rectangle BackBufferBounds => new Rectangle(0, 0, BackBufferWidth, BackBufferHeight);

        /// <summary>Whether the UI target is sampled 1:1 or at an integer ratio (point sampling keeps it sharp).</summary>
        public bool IntegerScale => ScaleX == Math.Floor(ScaleX) && ScaleY == Math.Floor(ScaleY) && ScaleX == ScaleY;

        // window -> back buffer: the faux back buffer when the window and the back buffer differ (a mode change in flight)
        public Vector2 WindowToBackBuffer(float x, float y) => new Vector2(
            WindowWidth > 0 ? (float)(x * (double)BackBufferWidth / WindowWidth) : x,
            WindowHeight > 0 ? (float)(y * (double)BackBufferHeight / WindowHeight) : y
        );

        public Vector2 BackBufferToUi(float x, float y) => new Vector2((float)(x / ScaleX), (float)(y / ScaleY));

        public Vector2 UiToBackBuffer(float x, float y) => new Vector2((float)(x * ScaleX), (float)(y * ScaleY));

        /// <summary>A window position (mouse) in UI units, truncated toward the top-left UI unit that contains it.</summary>
        public Point WindowToUi(float x, float y)
        {
            Vector2 b = WindowToBackBuffer(x, y);
            Vector2 u = BackBufferToUi(b.X, b.Y);

            return new Point((int)Math.Floor(u.X), (int)Math.Floor(u.Y));
        }

        /// <summary>A world-target pixel position to the back buffer, through the composite rect.</summary>
        public Vector2 WorldTargetToBackBuffer(float x, float y) => new Vector2(
            WorldOnScreen.X + (Viewport.Width > 0 ? x * WorldOnScreen.Width / (float)Viewport.Width : 0f),
            WorldOnScreen.Y + (Viewport.Height > 0 ? y * WorldOnScreen.Height / (float)Viewport.Height : 0f)
        );

        /// <summary>A back-buffer position to the world target (the inverse of the composite).</summary>
        public Vector2 BackBufferToWorldTarget(float x, float y) => new Vector2(
            WorldOnScreen.Width > 0 ? (x - WorldOnScreen.X) * Viewport.Width / (float)WorldOnScreen.Width : 0f,
            WorldOnScreen.Height > 0 ? (y - WorldOnScreen.Y) * Viewport.Height / (float)WorldOnScreen.Height : 0f
        );

        /// <summary>A UI position (the mouse) to the world target, through the back buffer.</summary>
        public Vector2 UiToWorldTarget(float x, float y)
        {
            Vector2 b = UiToBackBuffer(x, y);

            return BackBufferToWorldTarget(b.X, b.Y);
        }

        public bool Equals(RenderGeometry other) =>
            WindowWidth == other.WindowWidth && WindowHeight == other.WindowHeight && BackBufferWidth == other.BackBufferWidth
            && BackBufferHeight == other.BackBufferHeight && Scale == other.Scale && Viewport == other.Viewport;

        public override bool Equals(object obj) => obj is RenderGeometry g && Equals(g);

        public override int GetHashCode() => HashCode.Combine(WindowWidth, WindowHeight, BackBufferWidth, BackBufferHeight, Scale, Viewport);

        public static bool operator ==(RenderGeometry a, RenderGeometry b) => a.Equals(b);

        public static bool operator !=(RenderGeometry a, RenderGeometry b) => !a.Equals(b);

        public override string ToString() => FormattableStringInvariant();

        private string FormattableStringInvariant() => FormattableString.Invariant(
            $"window {WindowWidth}x{WindowHeight} backbuffer {BackBufferWidth}x{BackBufferHeight} scale {Scale:0.####} ui {UiWidth}x{UiHeight} ({ScaleX:0.#####}x{ScaleY:0.#####}) viewport {Viewport.X},{Viewport.Y} {Viewport.Width}x{Viewport.Height} on screen {WorldOnScreen.X},{WorldOnScreen.Y} {WorldOnScreen.Width}x{WorldOnScreen.Height}");
    }
}
