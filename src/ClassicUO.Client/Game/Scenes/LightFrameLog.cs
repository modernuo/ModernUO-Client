// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Dev-only light flicker probe (DevBench plan entry <c>lightframes:N</c>, or <c>CUO_LIGHT_FRAMES=N</c> at the first
    /// in-game frame): for N consecutive drawn frames it logs the light pass's inputs (light state, the light list drawn and
    /// its hash) and reads back the light target and the composited world area, with each one's change from the previous
    /// frame. A frame whose light list is unchanged but whose light target changed points at the GPU side; a list that
    /// changes while nothing moves points at the light collection. Reads back every frame: never measure with it on.
    /// </summary>
    internal static class LightFrameLog
    {
        private static int _left, _frame, _jumps, _listChanges, _targetOnlyChanges;
        private static bool _lightsSeen;
        private static int _count, _offX, _offY, _worldX, _worldY, _lagFrames, _lagMax;
        private static uint _hash, _prevHash;
        private static bool _drawn, _alt, _use, _held;
        private static float _level;
        private static Color[] _light, _lightPrev, _screen, _screenPrev;

        // CUO_LIGHT_FRAMES_READBACK=0: log the light pass only, without the per-frame readbacks (which slow every frame)
        private static readonly bool _readback = Environment.GetEnvironmentVariable("CUO_LIGHT_FRAMES_READBACK") != "0";

        public static bool Active => _left > 0;

        public static void Start(int frames)
        {
            _left = frames;
            _frame = _jumps = _listChanges = _targetOnlyChanges = 0;
            _lightsSeen = false;
            _lagFrames = _lagMax = 0;
            _prevHash = 0;
            _lightPrev = _screenPrev = null;
            Log.Info($"[light-frames] start {frames} frames");
        }

        public static void Stop()
        {
            if (_left > 0)
            {
                _left = 0;
                Summary();
            }
        }

        private static void Summary() =>
            Log.Info($"[light-frames] done: {_frame} frames, {_jumps} with a pixel jump > 48, light list changed {_listChanges}, light target changed with the same list {_targetOnlyChanges}, lights drawn at another offset than the world {_lagFrames} (max {_lagMax} px)");

        /// <summary>The light pass of this frame (GameScene.PrepareLightsRendering): <paramref name="drawn"/> false = lights off.</summary>
        public static void OnLightPass(bool drawn, bool use, bool alt, float level, int count, uint hash, int offsetX, int offsetY)
        {
            if (_left <= 0)
            {
                return;
            }

            _lightsSeen = true;
            _drawn = drawn;
            _use = use;
            _alt = alt;
            _level = level;
            _count = count;
            _hash = hash;
            _offX = offsetX;
            _offY = offsetY;
            _held = false;
        }

        /// <summary>The offset this frame's world was drawn with (after its list fill).</summary>
        public static void OnWorldOffset(int x, int y)
        {
            _worldX = x;
            _worldY = y;
        }

        public static void OnHeld()
        {
            if (_left <= 0)
            {
                return;
            }

            _lightsSeen = true;
            _held = true;
        }

        /// <summary>After the composite, before Present.</summary>
        public static void AfterComposite(GraphicsDevice device, RenderTargets targets, int backWidth, int backHeight)
        {
            if (_left <= 0 || !_lightsSeen)
            {
                return;
            }

            _lightsSeen = false;
            var lt = targets.LightRenderTarget;
            Rectangle world = targets.WorldSceneOnScreen;

            if (lt == null || lt.IsDisposed || backWidth <= 0 || backHeight <= 0)
            {
                return;
            }

            double lMean = 0, lDiff = 0, sMean = 0, sDiff = 0;
            int lBig = 0, lMaxD = 0, sBig = 0, sMaxD = 0;
            bool first = _frame == 0;

            if (_readback)
            {
                Readback(device, lt, world, backWidth, backHeight, first, out lMean, out lDiff, out lBig, out lMaxD, out sMean, out sDiff, out sBig, out sMaxD);
            }

            bool listChanged = !first && _hash != _prevHash;
            int lag = _held || !_drawn ? 0 : Math.Abs(_worldX - _offX) + Math.Abs(_worldY - _offY);

            if (lag != 0)
            {
                _lagFrames++;
                _lagMax = Math.Max(_lagMax, lag);
            }
            bool targetJump = !first && lBig > 0;

            if (listChanged)
            {
                _listChanges++;
            }
            else if (targetJump)
            {
                _targetOnlyChanges++;
            }

            if (!first && (sBig > 0 || lBig > 0))
            {
                _jumps++;
            }

            Log.Info(FormattableString.Invariant(
                $"[light-frames] f {_frame} held {(_held ? 1 : 0)} drawn {(_drawn ? 1 : 0)} use {(_use ? 1 : 0)} alt {(_alt ? 1 : 0)} level {_level:0.000} lights {_count}{(_count >= Assets.LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT ? " CAP" : "")} hash {_hash:X8}{(listChanged ? " LIST" : "")} lights at {_offX},{_offY} world at {_worldX},{_worldY}{(lag != 0 ? " LAG " + lag : "")} ") + FormattableString.Invariant(
                $"| light mean {lMean:0.00} diff {lDiff:0.000} big {lBig} maxd {lMaxD} | world mean {sMean:0.00} diff {sDiff:0.000} big {sBig} maxd {sMaxD}"));

            if (_readback)
            {
                (_light, _lightPrev) = (_lightPrev, _light);
                (_screen, _screenPrev) = (_screenPrev, _screen);
            }
            _prevHash = _hash;
            _frame++;

            if (--_left == 0)
            {
                Summary();
            }
        }

        private static void Readback(GraphicsDevice device, RenderTarget2D lt, Rectangle world, int backWidth, int backHeight, bool first,
            out double lMean, out double lDiff, out int lBig, out int lMaxD, out double sMean, out double sDiff, out int sBig, out int sMaxD)
        {
            int ln = lt.Width * lt.Height;
            Ensure(ref _light, ln);
            Ensure(ref _lightPrev, ln);
            lt.GetData(_light, 0, ln);

            int sn = backWidth * backHeight;
            Ensure(ref _screen, sn);
            Ensure(ref _screenPrev, sn);
            device.GetBackBufferData(_screen);

            (lMean, lDiff, lBig, lMaxD) = Stats(_light, first ? null : _lightPrev, 0, 0, lt.Width, lt.Height, lt.Width);
            world = Rectangle.Intersect(world, new Rectangle(0, 0, backWidth, backHeight));
            (sMean, sDiff, sBig, sMaxD) = Stats(_screen, first ? null : _screenPrev, world.X, world.Y, world.Width, world.Height, backWidth);
        }

        private static void Ensure(ref Color[] a, int n)
        {
            if (a == null || a.Length != n)
            {
                a = new Color[n];
            }
        }

        // mean luminance (0..255), mean absolute change, pixels changed by more than 48 and the largest change
        private static (double, double, int, int) Stats(Color[] cur, Color[] prev, int x0, int y0, int w, int h, int stride)
        {
            long sum = 0, dsum = 0;
            int big = 0, maxd = 0;

            for (int y = y0; y < y0 + h; y++)
            {
                int row = y * stride;

                for (int x = x0; x < x0 + w; x++)
                {
                    Color c = cur[row + x];
                    int l = (c.R * 3 + c.G * 6 + c.B) / 10;
                    sum += l;

                    if (prev != null)
                    {
                        Color p = prev[row + x];
                        int d = Math.Abs(l - (p.R * 3 + p.G * 6 + p.B) / 10);
                        dsum += d;

                        if (d > 48)
                        {
                            big++;
                        }

                        if (d > maxd)
                        {
                            maxd = d;
                        }
                    }
                }
            }

            double n = Math.Max(1, (long)w * h);

            return (sum / n, dsum / n, big, maxd);
        }
    }
}
#endif
