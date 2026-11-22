// SPDX-License-Identifier: BSD-2-Clause
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO
{
    // The client's render geometry (RenderGeometry): recomputed only when one of its inputs changed (window or back buffer
    // size, display or screen scale, window mode, the game viewport). RefreshGeometry runs at the start of every Update and
    // Draw and from the window and display-scale events; everything else reads Geometry.
    internal unsafe partial class GameController
    {
        private RenderGeometry _geometry;
        private int _windowX, _windowY;

        /// <summary>The render geometry as of the last <see cref="RefreshGeometry"/>.</summary>
        public RenderGeometry Geometry => _geometry;

        /// <summary>Recomputes <see cref="Geometry"/> if the window, back buffer, scale or game viewport changed.</summary>
        public void RefreshGeometry()
        {
            if (Window == null || GraphicsDevice == null)
            {
                return; // a controller from CreateForTests
            }

            Rectangle window = Window.ClientBounds;
            var pp = GraphicsDevice.PresentationParameters;
            float scale = DpiScale;
            Rectangle viewport = Scene?.Camera.Bounds ?? Rectangle.Empty;

            _windowX = window.X;
            _windowY = window.Y;

            if (window.Width == _geometry.WindowWidth && window.Height == _geometry.WindowHeight && pp.BackBufferWidth == _geometry.BackBufferWidth
                && pp.BackBufferHeight == _geometry.BackBufferHeight && scale == _geometry.Scale && viewport == _geometry.Viewport)
            {
                return;
            }

            _geometry = RenderGeometry.Compute(window.Width, window.Height, pp.BackBufferWidth, pp.BackBufferHeight, scale, viewport);
        }

        /// <summary>Test seam: sets the geometry a controller from CreateForTests reports.</summary>
        internal void SetGeometryForTests(RenderGeometry geometry) => _geometry = geometry;
    }
}
