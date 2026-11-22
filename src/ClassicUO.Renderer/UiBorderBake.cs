// SPDX-License-Identifier: BSD-2-Clause

using System;
using Microsoft.Xna.Framework;

namespace ClassicUO.Renderer
{
    /// <summary>What <see cref="UiBorderBake.Next"/> asks the composite to redraw in the border texture.</summary>
    public enum BorderBakeKind
    {
        None,
        Partial,
        Full
    }

    /// <summary>
    /// ui_border_bake (G11a, default off): which part of the border texture is stale. The border texture holds the composite
    /// as it is outside the world viewport (black, the 10 % background pattern and the UI target over them; under the
    /// viewport only black and the pattern), so it changes only with the geometry, the UI target or the target itself.
    /// The caller reports where the UI target changed (<see cref="AddDirtyUi"/>, UI units) or that it changed anywhere
    /// (<see cref="InvalidateAll"/>); <see cref="Next"/> turns that into the back-buffer region to bake before the frame's
    /// composite. Pure state, no GPU: RenderTargets draws what it returns.
    /// </summary>
    public sealed class UiBorderBake
    {
        private RenderGeometry _geometry;
        private int _uiVersion = -1;
        private object _target, _background;
        private bool _valid;
        private Rectangle _dirty;

        public int FullBakes { get; private set; }
        public int PartialBakes { get; private set; }
        public long BakedPixels { get; private set; }

        /// <summary>The whole border is stale (the UI target changed in an unknown place).</summary>
        public void InvalidateAll() => _valid = false;

        /// <summary>The UI target changed inside <paramref name="ui"/> (UI-target pixels).</summary>
        public void AddDirtyUi(Rectangle ui, in RenderGeometry geometry)
        {
            if (!_valid || ui.Width <= 0 || ui.Height <= 0)
            {
                return;
            }

            Rectangle bb = UiToBackBuffer(ui, geometry);

            if (bb.Width <= 0 || bb.Height <= 0)
            {
                return;
            }

            _dirty = _dirty.Width <= 0 || _dirty.Height <= 0 ? bb : Rectangle.Union(_dirty, bb);
        }

        /// <summary>
        /// The region to bake now (back-buffer pixels) and forgets it. Full when the geometry, the UI targets' version, the
        /// border target or the background differ from the last bake, or after <see cref="InvalidateAll"/>.
        /// </summary>
        public BorderBakeKind Next(in RenderGeometry geometry, int uiVersion, object target, object background, out Rectangle region)
        {
            Rectangle bounds = new Rectangle(0, 0, geometry.BackBufferWidth, geometry.BackBufferHeight);

            if (!_valid || !geometry.Equals(_geometry) || uiVersion != _uiVersion || !ReferenceEquals(target, _target)
                || !ReferenceEquals(background, _background))
            {
                _geometry = geometry;
                _uiVersion = uiVersion;
                _target = target;
                _background = background;
                _valid = true;
                _dirty = Rectangle.Empty;
                region = bounds;
                FullBakes++;
                BakedPixels += (long)bounds.Width * bounds.Height;

                return BorderBakeKind.Full;
            }

            region = Intersect(_dirty, bounds);
            _dirty = Rectangle.Empty;

            if (region.Width <= 0 || region.Height <= 0)
            {
                region = Rectangle.Empty;

                return BorderBakeKind.None;
            }

            PartialBakes++;
            BakedPixels += (long)region.Width * region.Height;

            return BorderBakeKind.Partial;
        }

        /// <summary>
        /// The back-buffer pixels a change inside <paramref name="ui"/> (UI-target pixels) can reach in the UI composite: the
        /// rect scaled with the geometry's per-axis scales, widened by one UI pixel (linear filtering at a fractional scale reads
        /// the neighbour) and one back-buffer pixel each side, clipped to the back buffer.
        /// </summary>
        public static Rectangle UiToBackBuffer(Rectangle ui, in RenderGeometry geometry)
        {
            int x0 = (int)Math.Floor((ui.X - 1) * geometry.ScaleX) - 1;
            int y0 = (int)Math.Floor((ui.Y - 1) * geometry.ScaleY) - 1;
            int x1 = (int)Math.Ceiling((ui.X + ui.Width + 1) * geometry.ScaleX) + 1;
            int y1 = (int)Math.Ceiling((ui.Y + ui.Height + 1) * geometry.ScaleY) + 1;

            return Intersect(new Rectangle(x0, y0, x1 - x0, y1 - y0), new Rectangle(0, 0, geometry.BackBufferWidth, geometry.BackBufferHeight));
        }

        /// <summary>
        /// The border around <paramref name="world"/> inside <paramref name="bounds"/> as up to four disjoint rects (top and
        /// bottom full width, left and right between them). Returns how many were written.
        /// </summary>
        public static int BorderRects(Rectangle bounds, Rectangle world, Span<Rectangle> output)
        {
            Rectangle w = Intersect(world, bounds);

            if (w.Width <= 0 || w.Height <= 0)
            {
                output[0] = bounds;

                return bounds.Width > 0 && bounds.Height > 0 ? 1 : 0;
            }

            int n = 0;
            int right = bounds.X + bounds.Width, bottom = bounds.Y + bounds.Height;

            Add(output, ref n, new Rectangle(bounds.X, bounds.Y, bounds.Width, w.Y - bounds.Y));
            Add(output, ref n, new Rectangle(bounds.X, w.Y + w.Height, bounds.Width, bottom - (w.Y + w.Height)));
            Add(output, ref n, new Rectangle(bounds.X, w.Y, w.X - bounds.X, w.Height));
            Add(output, ref n, new Rectangle(w.X + w.Width, w.Y, right - (w.X + w.Width), w.Height));

            return n;
        }

        private static void Add(Span<Rectangle> output, ref int n, Rectangle r)
        {
            if (r.Width > 0 && r.Height > 0)
            {
                output[n++] = r;
            }
        }

        public static Rectangle Intersect(Rectangle a, Rectangle b)
        {
            int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
            int x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            return x1 > x0 && y1 > y0 ? new Rectangle(x0, y0, x1 - x0, y1 - y0) : Rectangle.Empty;
        }
    }
}
