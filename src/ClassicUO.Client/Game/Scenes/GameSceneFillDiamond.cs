// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    // perf-16 world_fill_diamond (FillDiamond): the fill's state for the diamond walk and its checker.
    internal partial class GameScene
    {
        private bool _diamondWasOn;
        private FillDiamond.Bounds _diamondPrev;

        // At the fill's start, after GetViewPort and GpuPrepareBegin: this fill's bands. Tiles outside the walk keep stale
        // positions (they missed camera moves), so a fill whose bands differ from the last one's (the switch toggled, or the
        // bands moved while the offset held still) recomputes every walked position.
        private FillDiamond.Bounds BeginFillDiamond(bool on)
        {
            FillDiamond.Bounds b = default;

            if (on)
            {
                bool gpu = _gpuWorld != null;
                b = FillDiamond.Compute(_offset.X, _offset.Y, _minPixel, _maxPixel, gpu, _gpuView, gpu ? FillDiamond.MaxStaticArtHeight(_gpuArt) : 0);
            }

            if (on != _diamondWasOn || on && !b.Equals(_diamondPrev))
            {
                UpdateDrawPosition = true;
            }

            _diamondWasOn = on;
            _diamondPrev = b;

            return b;
        }

#if CUO_DEVTOOLS
        // CUO_FILL_DIAMOND_CHECK (CLEANUP(release)): per tile of the chunk outside the diamond, a checksum of its objects'
        // alphas and handle states before the walk
        private readonly ulong[] _diamondSide = new ulong[64];
        private object _diamondSelection;

        private static ulong DiamondSideSum(Chunk chunk, int cell, ulong mergeCells)
        {
            ulong h = 17;

            for (GameObject obj = TileWalk.Open(chunk, cell, mergeCells, out TileWalk tileWalk, out bool mergedWalk, out TileListObject list);
                 obj != null;
                 obj = mergedWalk ? tileWalk.Next() : list = list.TNext)
            {
                h = h * 31 + obj.AlphaHue;
                h = h * 31 + (ulong)obj.ObjectHandlesStatus;
            }

            return h;
        }

        private void DiamondCheckBefore(Chunk chunk, ulong outside, ulong mergeCells)
        {
            for (int b = 0; b < 64; b++)
            {
                if ((outside >> b & 1) != 0)
                {
                    _diamondSide[b] = DiamondSideSum(chunk, b, mergeCells);
                }
            }
        }

        // After the chunk's walk: nothing on a tile outside the diamond may have been drawn (stamped), and its pre-cull work
        // (fades, handles) is counted as dropped.
        private void DiamondCheckAfterWalk(Chunk chunk, ulong outside, ulong mergeCells, in FillDiamond.Bounds bounds)
        {
            for (int b = 0; b < 64; b++)
            {
                if ((outside >> b & 1) == 0)
                {
                    continue;
                }

                for (GameObject obj = TileWalk.Open(chunk, b, mergeCells, out TileWalk tileWalk, out bool mergedWalk, out TileListObject list);
                     obj != null;
                     obj = mergedWalk ? tileWalk.Next() : list = list.TNext)
                {
                    if (obj.DrawnFrame == _drawFrame)
                    {
                        FillDiamond.Drawn(obj, obj.X, obj.Y, obj.Z, in bounds);
                    }
                }

                if (DiamondSideSum(chunk, b, mergeCells) != _diamondSide[b])
                {
                    FillDiamond.SideEffect((chunk.X << 3) + (b >> 3), (chunk.Y << 3) + (b & 7));
                }
            }
        }

        private void DiamondCheckHidden(TileListObject head, bool skipBaked, in FillDiamond.Bounds bounds)
        {
            int before = GpuWorldRenderer.OverrideCalls;
            CollectGpuHidden(head, skipBaked);
            int calls = GpuWorldRenderer.OverrideCalls - before;

            if (calls != 0)
            {
                FillDiamond.Hidden(head.X, head.Y, calls, in bounds);
            }
        }

        private void DiamondCheckSelection(in FillDiamond.Bounds bounds)
        {
            if (SelectedObject.Object is GameObject g && !ReferenceEquals(g, _diamondSelection) && !bounds.Contains(g.X, g.Y))
            {
                FillDiamond.Selected(g, g.X, g.Y, g.Z, in bounds);
            }
        }
#endif
    }
}
