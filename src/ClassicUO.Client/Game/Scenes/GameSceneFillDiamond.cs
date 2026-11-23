// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Scenes
{
    // perf-16 world_fill_diamond (FillDiamond): the fill's state for the diamond walk.
    internal partial class GameScene
    {
        private FillDiamond.Bands _diamondPrev;

        // At the fill's start, after GetViewPort and GpuPrepareBegin: this fill's bands. Tiles outside the walk keep stale
        // positions (they missed camera moves), so a fill whose bands differ from the last one's (the bands moved while the
        // offset held still) recomputes every walked position. perf-18: chunks with no tile in the hidden bands skip their
        // chunk-level work too (mesh build, visibility reset, Gpu prepare); they too enter the bands only when the bands move.
        private FillDiamond.Bands BeginFillDiamond()
        {
            bool gpu = _gpuWorld != null;
            FillDiamond.Bands b = FillDiamond.ComputeBands(_offset.X, _offset.Y, _minPixel, _maxPixel, gpu, _gpuView, gpu ? FillDiamond.MaxStaticArtHeight(_gpuArt) : 0);

            _zband = WalkChunkZ.Enabled;
            _zbandOcc = _zband && (LightOcclusion.Enabled || LightOcclusion.Check);
            _zbandCheck = WalkChunkZ.Check;
            _fillSeq++;

            // walk_chunk_z: the per-chunk S bands move with the raw edges, which can move inside a tile of the full band
            if (!b.Equals(_diamondPrev) || _zband && (!b.Walk.RawEquals(in _diamondPrev.Walk) || !b.Hidden.RawEquals(in _diamondPrev.Hidden)))
            {
                UpdateDrawPosition = true;
            }

            _diamondPrev = b;

            return b;
        }
    }
}
