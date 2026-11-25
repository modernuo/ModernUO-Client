// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // view_margin_cull (vm-legacy): view_margin grows the walk by the largest art's reach, so the Legacy walk marks every
        // chunk-mesh sprite of the margin visible (land and small statics included) and the batcher draws them off screen;
        // the Gpu world draws its baked chunks whole and its walk skips them. With the switch on, a mesh sprite whose anchor
        // is in the margin and whose quad misses the view is left out (still stamped drawn, its light still added).
        // _vmCount: the margin is tested this frame (the switch, FrameStats counters or CUO_VIEW_MARGIN_CHECK).
        private bool _vmCull, _vmCount;
        private int _vmMarginMesh, _vmMarginCulled, _vmMarginList;

        private void BeginMarginCull()
        {
            _vmCull = _vmSprites && _gpuWorld == null && ViewMargin.CullEnabled;
            _vmCount = _vmSprites && _gpuWorld == null && (_vmCull || (FrameStats.On && ViewMargin.WhatIf) || ViewMargin.Check);
#if CUO_DEVTOOLS
            if (_vmCount && ViewMargin.Check)
            {
                _vcVisMin = Camera.ScreenToWorld(Point.Zero);
                _vcVisMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            }
#endif
        }

        /// <summary>view_margin_cull: whether the walk leaves out <paramref name="obj"/>'s mesh sprite (its anchor in the
        /// margin, its quad off the view).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MarginCulled(GameObject obj, MeshLayer layer)
        {
            return _vmCount && MarginCulledSlow(obj, layer);
        }

        private bool MarginCulledSlow(GameObject obj, MeshLayer layer)
        {
            int ax = obj.RealScreenPosition.X, ay = obj.RealScreenPosition.Y;
            float left = _viewMinPixel.X, top = _viewMinPixel.Y, right = _viewMaxPixel.X, bottom = _viewMaxPixel.Y;

            if (ax >= left && ax <= right && ay >= top && ay <= bottom)
            {
                return false;
            }

            _vmMarginMesh++;
            layer.QuadBounds(obj.MeshSpriteIndex, out float x0, out float y0, out float x1, out float y1);
            float ox = _offset.X, oy = _offset.Y;

            if (!ViewMargin.MarginCulls(ax, ay, x0 - ox, y0 - oy, x1 - ox, y1 - oy, left, top, right, bottom))
            {
                return false;
            }

            _vmMarginCulled++;
#if CUO_DEVTOOLS
            if (ViewMargin.Check)
            {
                CheckMarginCulled(obj);
            }
#endif
            return _vmCull;
        }

        private void CountMarginListed(GameObject obj)
        {
            int ax = obj.RealScreenPosition.X, ay = obj.RealScreenPosition.Y;

            if (ax < _viewMinPixel.X || ax > _viewMaxPixel.X || ay < _viewMinPixel.Y || ay > _viewMaxPixel.Y)
            {
                _vmMarginList++;
            }
        }

        private void FlushMarginCull()
        {
            if (FrameStats.On && _vmCount)
            {
                FrameStats.Add(FrameCounter.VmMarginMesh, _vmMarginMesh);
                FrameStats.Add(FrameCounter.VmMarginCulled, _vmMarginCulled);
                FrameStats.Add(FrameCounter.VmMarginList, _vmMarginList);
            }

#if CUO_DEVTOOLS
            if (ViewMargin.Check && _vmCount)
            {
                _vcFrames++;
                _vcCulled += _vmMarginCulled;

                if (Time.Ticks >= _vcSummaryAt)
                {
                    _vcSummaryAt = Time.Ticks + 5000;
                    Log.Info($"[view-margin-check] cull: view_margin_cull {(_vmCull ? "on" : "off")}, {_vcFrames} frames, " +
                             $"{_vcCulled} margin sprites off the view{(_vmCull ? " left out" : " (would be left out)")}, " +
                             $"{_vcMismatch} of them reaching the view by their art box");
                }
            }
#endif
            _vmMarginMesh = 0;
            _vmMarginCulled = 0;
            _vmMarginList = 0;
        }

#if CUO_DEVTOOLS
        private Point _vcVisMin, _vcVisMax;
        private long _vcFrames, _vcCulled, _vcMismatch;
        private uint _vcSummaryAt;
        private int _vcLogged;

        // CUO_VIEW_MARGIN_CHECK (view_margin_cull): a sprite the quad test puts off the view must not reach the visible world
        // (no slack) by the object's own box: a land tile's from its anchor and corner offsets, a static's or multi piece's
        // from its art size. Runs in both switch states on the sprites the switch leaves out or would.
        private void CheckMarginCulled(GameObject obj)
        {
            int ax = obj.RealScreenPosition.X, ay = obj.RealScreenPosition.Y, x0, y0, x1, y1;

            if (obj is Land land)
            {
                ViewMargin.LandBox(ax, ay, land.Z, land.IsStretched, land.YOffsets.Top, land.YOffsets.Right, land.YOffsets.Bottom,
                    land.YOffsets.Left, out x0, out y0, out x1, out y1);
            }
            else if (Client.Game.UO.FileManager.Arts.TrySize(obj.Graphic + 0x4000, out int w, out int h))
            {
                ViewMargin.StaticBox(ax, ay, w, h, out x0, out y0, out x1, out y1);
            }
            else
            {
                return;
            }

            if (x1 < _vcVisMin.X || x0 > _vcVisMax.X || y1 < _vcVisMin.Y || y0 > _vcVisMax.Y)
            {
                return;
            }

            _vcMismatch++;

            if (_vcLogged < 30)
            {
                _vcLogged++;
                Log.Warn($"[view-margin-check] cull: {obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z} off the view by its " +
                         $"mesh quad but its box {x0},{y0}..{x1},{y1} meets the view {_vcVisMin.X},{_vcVisMin.Y}..{_vcVisMax.X},{_vcVisMax.Y}");
            }
        }
#endif
    }
}
