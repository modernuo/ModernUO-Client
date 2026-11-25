// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // view_margin_walk (vm-walk): view_margin grows every chunk's walk by max(its overhang, the mobile 160/260 px), so each
        // land tile and static of the margin is visited (position, alpha, mesh mark, pick test) for the few tall sprites and
        // mobiles that reach the view from there. With the switch on (chunk_lights, always on, lights the margin's statics from
        // the chunks' emitter lists), a margin tile is walked only when Chunk.ReachCells or the dynamic index has it, and an
        // object whose anchor is in the margin is processed only when its box meets the view (ViewMargin.RingSkips).
        // _vmRing: the margin decision is computed this frame (the switch, the CUO_VIEW_MARGIN_WHATIF counters or the checker).
        private bool _vmWalk, _vmRing;
        private FillDiamond.Bounds _vmViewBounds;
        private int _vmRingTilesSkipped, _vmRingObjectsSkipped;

        private void BeginMarginWalk()
        {
            _vmWalk = _vmSprites && ViewMargin.WalkEnabled;
            _vmRing = _vmSprites && (_vmWalk || (FrameStats.On && ViewMargin.WhatIf) || ViewMargin.WalkCheck);

            if (_vmRing)
            {
                _vmViewBounds = FillDiamond.Compute(_offset.X, _offset.Y, _viewMinPixel, _viewMaxPixel, false, default, 0);
            }
        }

        /// <summary>view_margin_walk: the chunk's tiles the walk visits (<see cref="ViewMargin.RingTiles"/>);
        /// <paramref name="ringWalk"/>: the tiles the switch walks (also when off, for the counters and the checker).</summary>
        private ulong MarginWalkTiles(Chunk chunk, int chunkX, int chunkY, ulong inWalk, out ulong ringWalk)
        {
#if CUO_DEVTOOLS
            if (MarginLog && _vmSprites && inWalk != 0)
            {
                NoteMarginChunk(chunk);
            }
#endif
            if (!_vmRing || inWalk == 0)
            {
                ringWalk = inWalk;

                return inWalk;
            }

            ulong viewMask = FillDiamond.ChunkMask(chunkX, chunkY, in _vmViewBounds);
            ringWalk = ViewMargin.RingTiles(inWalk, viewMask, chunk.ReachCells, chunk.Dynamic?.Cells ?? 0);
            _vmRingTilesSkipped += System.Numerics.BitOperations.PopCount(inWalk & ~ringWalk);

            return _vmWalk ? ringWalk : inWalk;
        }

        /// <summary>view_margin_walk: whether the walk leaves out <paramref name="obj"/>, an object of a whole-tile walk with
        /// its position up to date (anchor in the margin, box off the view).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MarginWalkSkips(GameObject obj)
        {
            if (!_vmRing)
            {
                return false;
            }

            int ax = obj.RealScreenPosition.X, ay = obj.RealScreenPosition.Y;

            if (ViewMargin.InView(ax, ay, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y) || !RingSkipsAt(obj, ax, ay))
            {
                return false;
            }

            _vmRingObjectsSkipped++;

            return _vmWalk;
        }

        private bool RingSkipsAt(GameObject obj, int ax, int ay)
        {
            ViewMargin.RingKind kind = ViewMargin.RingKind.Keep;
            int w = 0, h = 0, slack = 0, top = 0, right = 0, bottom = 0, left = 0;
            bool stretched = false;

            switch (obj)
            {
                case Land land:
                    kind = ViewMargin.RingKind.Land;
                    stretched = land.IsStretched;
                    top = land.YOffsets.Top;
                    right = land.YOffsets.Right;
                    bottom = land.YOffsets.Bottom;
                    left = land.YOffsets.Left;

                    break;

                case Static:
                case Multi:
                case Item { IsMulti: false, IsCorpse: false }:
                    ushort graphic = obj is Item item ? item.DisplayedGraphicOf(item.Graphic) : obj.Graphic;

                    if (!Client.Game.UO.FileManager.Arts.TrySize(graphic + 0x4000, out w, out h))
                    {
                        return false;
                    }

                    // vmw-cost: an item still fading in is kept (ViewMargin.ItemRingKind)
                    kind = obj is Item ? ViewMargin.ItemRingKind(obj.AlphaHue) : ViewMargin.RingKind.Art;
                    slack = obj is Static ? 0 : ViewMargin.MovingSlack;

                    break;
            }

            return ViewMargin.RingSkips(kind, ax, ay, obj.Z, stretched, top, right, bottom, left, w, h, slack,
                _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y);
        }

        private void FlushMarginWalk()
        {
#if CUO_DEVTOOLS
            if (MarginLog && _vmSprites)
            {
                LogMargins();
            }
#endif
            if (FrameStats.On && _vmRing)
            {
                FrameStats.Add(FrameCounter.VmRingTilesSkipped, _vmRingTilesSkipped);
                FrameStats.Add(FrameCounter.VmRingObjectsSkipped, _vmRingObjectsSkipped);
            }

#if CUO_DEVTOOLS
            if (ViewMargin.WalkCheck && _vmRing)
            {
                _vwFrames++;
                _vwTiles += _vmRingTilesSkipped;
                _vwObjects += _vmRingObjectsSkipped;

                if (Time.Ticks >= _vwSummaryAt)
                {
                    _vwSummaryAt = Time.Ticks + 5000;
                    Log.Info($"[view-margin-walk-check] view_margin_walk {(_vmWalk ? "on" : "off")}, {_vwFrames} frames: margin tiles " +
                             $"{(_vmWalk ? "skipped" : "that would be skipped")} {_vwTiles}, margin objects {(_vmWalk ? "left out" : "that would be left out")} {_vwObjects}; " +
                             $"checked {_vwChecked} margin objects of the full margin walk and {_vwFading} fading margin items, mismatches {_vwMismatch} " +
                             "(reaching the view or fading, left out)");
                }
            }
#endif
            _vmRingTilesSkipped = 0;
            _vmRingObjectsSkipped = 0;
        }

#if CUO_DEVTOOLS
        // CUO_VIEW_MARGIN_LOG=1 (vm-walk, the equivalent-viewport control): the margins the walk used, once per 5 s: the anchor
        // rectangle's (ViewMargin.ArtReach) and the walked chunks' overhang (mean and max of StaticOverLeft/Right/Up), so a
        // view_margin off run can be sized to the same walked area
        private static readonly bool MarginLog = System.Environment.GetEnvironmentVariable("CUO_VIEW_MARGIN_LOG") == "1";
        private long _mlChunks, _mlLeft, _mlRight, _mlUp, _mlFrames, _mlRingL, _mlRingR, _mlRingU;
        private int _mlMaxLeft, _mlMaxRight, _mlMaxUp;
        private uint _mlAt;

        private void NoteMarginChunk(Chunk chunk)
        {
            _mlChunks++;
            _mlLeft += chunk.StaticOverLeft;
            _mlRight += chunk.StaticOverRight;
            _mlUp += chunk.StaticOverUp;
            // the chunk's walked ring (ViewMargin.ChunkRect): left of the view by its right overhang, right of it by its left
            _mlRingL += System.Math.Max(chunk.StaticOverRight, ViewMargin.MobileSide);
            _mlRingR += System.Math.Max(chunk.StaticOverLeft, ViewMargin.MobileSide);
            _mlRingU += System.Math.Max(chunk.StaticOverUp, ViewMargin.MobileUp);
            _mlMaxLeft = System.Math.Max(_mlMaxLeft, chunk.StaticOverLeft);
            _mlMaxRight = System.Math.Max(_mlMaxRight, chunk.StaticOverRight);
            _mlMaxUp = System.Math.Max(_mlMaxUp, chunk.StaticOverUp);
        }

        private void LogMargins()
        {
            _mlFrames++;

            if (Time.Ticks < _mlAt || _mlChunks == 0)
            {
                return;
            }

            _mlAt = Time.Ticks + 5000;
            ViewMargin.ArtReach(Client.Game.UO.FileManager.Arts, out int side, out int up);
            Log.Info($"[view-margin] art reach side {side} up {up} (mobile {ViewMargin.MobileSide}/{ViewMargin.MobileUp}); view {_viewMinPixel.X},{_viewMinPixel.Y}..{_viewMaxPixel.X},{_viewMaxPixel.Y} " +
                     $"({Camera.Bounds.Width}x{Camera.Bounds.Height} zoom {Camera.Zoom}); walked chunks {_mlChunks / _mlFrames}/frame, overhang mean " +
                     $"left {_mlLeft / _mlChunks} right {_mlRight / _mlChunks} up {_mlUp / _mlChunks}, max left {_mlMaxLeft} right {_mlMaxRight} up {_mlMaxUp}; " +
                     $"chunk ring (max(overhang, mobile)) mean left {_mlRingL / _mlChunks} right {_mlRingR / _mlChunks} up {_mlRingU / _mlChunks}, " +
                     $"max side {System.Math.Max(System.Math.Max(_mlMaxLeft, _mlMaxRight), ViewMargin.MobileSide)} up {System.Math.Max(_mlMaxUp, ViewMargin.MobileUp)}");
            _mlChunks = _mlLeft = _mlRight = _mlUp = _mlFrames = _mlRingL = _mlRingR = _mlRingU = 0;
            _mlMaxLeft = _mlMaxRight = _mlMaxUp = 0;
        }

        private long _vwFrames, _vwTiles, _vwObjects, _vwChecked, _vwMismatch, _vwFading;
        private uint _vwSummaryAt;
        private int _vwLogged;

        // CUO_VIEW_MARGIN_WALK_CHECK (view_margin_walk): the reference is the full margin walk. Every object of the chunk's
        // walked tiles (the full mask) whose anchor is in the margin and which the new path leaves out (its tile skipped, or
        // the object by RingSkips) must not reach the visible world (no slack): the box is the chunk mesh quad when the object
        // has one (an independent source), else its art box; drawn by the walk's own tests (alpha, max Z, hidden roofs).
        // Positions come from X/Y/Z (a skipped tile's are stale). Runs in both switch states.
        internal void CheckMarginWalk(Chunk chunk, ulong inWalk, ulong walk)
        {
            Point vMin = Camera.ScreenToWorld(Point.Zero);
            Point vMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            int offX = _offset.X, offY = _offset.Y;

            for (ulong cells = inWalk; cells != 0; cells &= cells - 1)
            {
                int cell = System.Numerics.BitOperations.TrailingZeroCount(cells);
                bool tileSkipped = (walk & (1UL << cell)) == 0;

                for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
                {
                    CheckMarginWalkObject(chunk, o, tileSkipped, vMin, vMax, offX, offY);
                }

                if (chunk.Dynamic != null)
                {
                    foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
                    {
                        CheckMarginWalkObject(chunk, en.Obj, tileSkipped, vMin, vMax, offX, offY);
                    }
                }
            }
        }

        // vmw-cost: a margin item the new path leaves out (object skip; its tile is walked, items are in the dynamic cells)
        // must have settled its fade (AlphaHue 0xFF): the walk steps the alpha only of the objects it visits
        private void CheckMarginWalkFading(Item item, bool tileSkipped, int offX, int offY)
        {
            ViewMargin.Anchor(item.X, item.Y, item.Z, offX, offY, out int ax, out int ay);

            if (ax < _minPixel.X || ax > _maxPixel.X || ay < _minPixel.Y || ay > _maxPixel.Y ||
                ViewMargin.InView(ax, ay, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y))
            {
                return;
            }

            _vwFading++;

            if (!tileSkipped && !RingSkipsAt(item, ax, ay))
            {
                return;
            }

            _vwMismatch++;

            if (_vwLogged < 30)
            {
                _vwLogged++;
                Log.Warn($"[view-margin-walk-check] Item 0x{item.Graphic:X4} at {item.X},{item.Y},{item.Z} left out " +
                         $"({(tileSkipped ? "tile skipped" : "object skipped")}, view_margin_walk {(_vmWalk ? "on" : "off")}) while fading " +
                         $"(alpha {item.AlphaHue})");
            }
        }

        private void CheckMarginWalkObject(Chunk chunk, GameObject obj, bool tileSkipped, Point vMin, Point vMax, int offX, int offY)
        {
            if (obj is Item { IsMulti: false, IsCorpse: false, IsDestroyed: false } fading && fading.AlphaHue != 0xFF && fading.AllowedToDraw)
            {
                CheckMarginWalkFading(fading, tileSkipped, offX, offY);
            }

            if (obj == null || obj.IsDestroyed || !obj.AllowedToDraw || obj.AlphaHue == 0 || obj is Mobile || obj is GameEffect)
            {
                return;
            }

            ViewMargin.Anchor(obj.X, obj.Y, obj.Z, offX, offY, out int ax, out int ay);

            if (ax < _minPixel.X || ax > _maxPixel.X || ay < _minPixel.Y || ay > _maxPixel.Y ||
                ViewMargin.InView(ax, ay, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y))
            {
                // the stretched land the walk draws from above the view by its corners has its anchor outside the walk's
                // rectangle too: the full walk reaches it as the new path does (by the land's own adjusted test)
                if (!(obj is Land { IsStretched: true }) || ViewMargin.InView(ax, ay, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y))
                {
                    return;
                }
            }

            _vwChecked++;

            if (!tileSkipped && !RingSkipsAt(obj, ax, ay))
            {
                return;
            }

            int x0, y0, x1, y1;

            if (obj is Land land)
            {
                if (land.Z > _maxGroundZ && land.AlphaHue == 0)
                {
                    return;
                }

                if (land.InChunkMesh && land.MeshSpriteIndex >= 0)
                {
                    chunk.Mesh.Land.QuadBounds(land.MeshSpriteIndex, out float qx0, out float qy0, out float qx1, out float qy1);
                    x0 = (int)qx0 - offX;
                    y0 = (int)qy0 - offY;
                    x1 = (int)qx1 - offX;
                    y1 = (int)qy1 - offY;
                }
                else
                {
                    ViewMargin.LandBox(ax, ay, land.Z, land.IsStretched, land.YOffsets.Top, land.YOffsets.Right, land.YOffsets.Bottom,
                        land.YOffsets.Left, out x0, out y0, out x1, out y1);
                }
            }
            else
            {
                StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;

                if ((uint)obj.Graphic >= (uint)staticData.Length)
                {
                    return;
                }

                ref StaticTiles data = ref staticData[obj.Graphic];

                if (data.IsInternal || obj.Z >= _maxZ || _noDrawRoofs && data.IsRoof)
                {
                    return;
                }

                if (obj.InChunkMesh && obj.MeshSpriteIndex >= 0)
                {
                    chunk.Mesh.Statics.QuadBounds(obj.MeshSpriteIndex, out float qx0, out float qy0, out float qx1, out float qy1);
                    x0 = (int)qx0 - offX;
                    y0 = (int)qy0 - offY;
                    x1 = (int)qx1 - offX;
                    y1 = (int)qy1 - offY;
                }
                else
                {
                    ushort graphic = obj is Item item ? item.DisplayedGraphicOf(item.Graphic) : obj.Graphic;

                    if (obj is Item { IsCorpse: true } || obj is Item { IsMulti: true } ||
                        !Client.Game.UO.FileManager.Arts.TrySize(graphic + 0x4000, out int w, out int h))
                    {
                        // the new path keeps these whenever their tile is walked
                        if (!tileSkipped)
                        {
                            return;
                        }

                        x0 = ax - ViewMargin.MobileSide;
                        x1 = ax + 44 + ViewMargin.MobileSide;
                        y0 = ay - ViewMargin.MobileUp;
                        y1 = ay + 44;
                    }
                    else
                    {
                        ViewMargin.StaticBox(ax, ay, w, h, out x0, out y0, out x1, out y1);
                    }
                }
            }

            if (x1 < vMin.X || x0 > vMax.X || y1 < vMin.Y || y0 > vMax.Y)
            {
                return;
            }

            _vwMismatch++;

            if (_vwLogged < 30)
            {
                _vwLogged++;
                Log.Warn($"[view-margin-walk-check] {obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z} left out " +
                         $"({(tileSkipped ? "tile skipped" : "object skipped")}, view_margin_walk {(_vmWalk ? "on" : "off")}) but its box " +
                         $"{x0},{y0}..{x1},{y1} meets the view {vMin.X},{vMin.Y}..{vMax.X},{vMax.Y}");
            }
        }
#endif
    }
}
