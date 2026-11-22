// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.IO;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Dev-only render geometry probe (CUO_GEOM_LOG=1, or the driver's <c>geom</c>): logs what each pass of a frame was drawn
    /// with (target size, device viewport, the batcher's projection size, the camera matrix, the Gpu world camera) next to the
    /// window, back buffer, DPI scale, camera bounds and composite rects, whenever any of them changes. With CUO_GEOM_SHOTS
    /// set, a world shot is saved 30 frames after each change.
    /// </summary>
    internal static class GeometryProbe
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_GEOM_LOG") == "1";
        private static readonly string _shots = Environment.GetEnvironmentVariable("CUO_GEOM_SHOTS");

        private static string _last;
        private static int _shotIn = -1, _shotIndex;
        private static bool _forced;
        private static string _tag = "";

        // the frame's passes
        private static Point _worldTarget, _lightTarget, _worldVp, _lightVp, _worldProj, _lightProj;
        private static Matrix _worldMatrix, _lightMatrix;
        private static double _gpuCx, _gpuCy;
        private static int _gpuVw, _gpuVh, _offX, _offY, _lights;
        private static bool _gpu;
        private static int _firstLightX, _firstLightY;

        public static bool Active => On || _forced;

        public static void Force(string tag)
        {
            _forced = true;
            _tag = tag;
            _last = null;
        }

        public static void OnWorldPass(GraphicsDevice device, UltimaBatcher2D batcher, RenderTarget2D target, in Matrix matrix, bool gpu,
            double gpuCx, double gpuCy, int gpuVw, int gpuVh, int offX, int offY)
        {
            _worldTarget = target == null ? Point.Zero : new Point(target.Width, target.Height);
            _worldVp = new Point(device.Viewport.Width, device.Viewport.Height);
            _worldProj = batcher.AppliedProjectionSize;
            _worldMatrix = matrix;
            _gpu = gpu;
            (_gpuCx, _gpuCy, _gpuVw, _gpuVh, _offX, _offY) = (gpuCx, gpuCy, gpuVw, gpuVh, offX, offY);
        }

        public static void OnLightPass(GraphicsDevice device, UltimaBatcher2D batcher, RenderTarget2D target, in Matrix matrix, int lights, int firstX, int firstY)
        {
            _lightTarget = target == null ? Point.Zero : new Point(target.Width, target.Height);
            _lightVp = new Point(device.Viewport.Width, device.Viewport.Height);
            _lightProj = batcher.AppliedProjectionSize;
            _lightMatrix = matrix;
            _lights = lights;
            _firstLightX = firstX;
            _firstLightY = firstY;
        }

        /// <summary>After the composite.</summary>
        public static void AfterComposite(GameController game, RenderTargets targets, Rectangle cameraBounds, float zoom)
        {
            if (!Active)
            {
                return;
            }

            var cb = game.Window.ClientBounds;
            var pp = game.GraphicsDevice.PresentationParameters;
            var ui = targets.UiRenderTarget;
            string s = FormattableString.Invariant(
                $"window {cb.Width}x{cb.Height} backbuffer {pp.BackBufferWidth}x{pp.BackBufferHeight} dpi {game.DpiScale:0.####} uiTarget {ui?.Width}x{ui?.Height} clientBounds {game.ClientBounds.Width}x{game.ClientBounds.Height} camera {cameraBounds.X},{cameraBounds.Y} {cameraBounds.Width}x{cameraBounds.Height} zoom {zoom:0.###} composite {targets.WorldSceneOnScreen} | world {(_gpu ? "gpu" : "legacy")} target {_worldTarget.X}x{_worldTarget.Y} vp {_worldVp.X}x{_worldVp.Y} proj {_worldProj.X}x{_worldProj.Y} m {_worldMatrix.M11:0.####},{_worldMatrix.M41:0.##},{_worldMatrix.M42:0.##} gpuCam {_gpuCx:0.##},{_gpuCy:0.##} {_gpuVw}x{_gpuVh} offset {_offX},{_offY} | light target {_lightTarget.X}x{_lightTarget.Y} vp {_lightVp.X}x{_lightVp.Y} proj {_lightProj.X}x{_lightProj.Y} m {_lightMatrix.M11:0.####},{_lightMatrix.M41:0.##},{_lightMatrix.M42:0.##} lights {_lights}");

            if (s != _last)
            {
                _last = s;
                Log.Info($"[geom]{(_tag.Length > 0 ? " " + _tag : "")} {s}");
                _shotIn = 30;
            }
            else if (_shotIn > 0 && --_shotIn == 0 && !string.IsNullOrEmpty(_shots))
            {
                string name = FormattableString.Invariant($"g{_shotIndex++:00}_{targets.WorldSceneOnScreen.Width}x{targets.WorldSceneOnScreen.Height}_{(_gpu ? "gpu" : "legacy")}");
                game.RequestWorldShot(Path.Combine(_shots, name + ".png"));
                Log.Info($"[geom] shot {name} (camera {cameraBounds.Width}x{cameraBounds.Height}, first light at {_firstLightX},{_firstLightY})");
            }

            if (_forced && _shotIn < 0)
            {
                _forced = false;
            }
        }
    }
}
#endif
