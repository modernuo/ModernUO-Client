// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    // walk_chunk_z (WalkChunkZ): the fill's per-chunk S band from the chunk's recorded Z range.
    internal partial class GameScene
    {
        private bool _zband, _zbandOcc, _zbandCheck;
        private uint _fillSeq;
        // walk_chunk_z with light_occlusion: the chunks of the full bands, so the occluder search sees the chunks it saw
        // before (a chunk the tight band drops has nothing drawn, but its art may still reach a light's rectangle)
        private readonly List<Chunk> _occChunks = new();

        private List<Chunk> OccluderChunks => _zbandOcc ? _occChunks : _visibleChunks;

        /// <summary>
        /// The chunk's fill masks for its recorded Z range (Chunk.WalkZLo/Hi). zForce: tiles may join the walk without a
        /// camera move (the range grew, or the chunk was not banded at the last fill), so walked positions are checked against
        /// the camera generation. With CUO_WALK_CHUNK_Z_CHECK the full-band masks are computed too and compared; with the
        /// switch off the full masks are returned.
        /// </summary>
        private ulong ZBandChunkMasks(Chunk chunk, int chunkX, int chunkY, bool gpu, in FillDiamond.Bands diamond, out ulong inHidden, out bool zForce)
        {
            int zLo = chunk.WalkZLo, zHi = chunk.WalkZHi;
            ulong inWalk = FillDiamond.ChunkMasks(gpu, chunkX, chunkY, in diamond, zLo, zHi, out inHidden);
            ulong fullWalk = 0, fullHidden = 0;

            if (_zbandOcc || _zbandCheck)
            {
                fullWalk = FillDiamond.ChunkMasks(gpu, chunkX, chunkY, in diamond, out fullHidden);

                if (_zbandOcc && fullHidden != 0)
                {
                    _occChunks.Add(chunk);
                }

                if (_zbandCheck)
                {
                    CheckZBand(chunk, chunkX, chunkY, inWalk, inHidden, fullWalk, fullHidden, gpu, zLo, zHi);
                }
            }

            zForce = false;

            if (!_zband)
            {
                inHidden = fullHidden;

                return fullWalk;
            }

            if (inHidden != 0)
            {
                zForce = chunk.FillSeq != _fillSeq - 1 || zLo != chunk.FillZLo || zHi != chunk.FillZHi;
                chunk.FillSeq = _fillSeq;
                chunk.FillZLo = zLo;
                chunk.FillZHi = zHi;
            }

            return inWalk;
        }

        // CUO_WALK_CHUNK_Z_CHECK: objects on the tiles only the full band covers that the walk (anchor inside the culling
        // rectangle) or the Gpu hidden pass (GpuLandVisible / GpuStaticVisible) would act on; every 30th fill, every object of
        // a banded chunk against the chunk's recorded range
        private void CheckZBand(Chunk chunk, int chunkX, int chunkY, ulong inWalk, ulong inHidden, ulong fullWalk, ulong fullHidden, bool gpu, int zLo, int zHi)
        {
            WalkChunkZ.CheckChunks += fullHidden != 0 ? 1 : 0;
            WalkChunkZ.CheckTightChunks += inHidden != 0 ? 1 : 0;
            WalkChunkZ.CheckFullTiles += System.Numerics.BitOperations.PopCount(fullWalk);
            WalkChunkZ.CheckTightTiles += System.Numerics.BitOperations.PopCount(inWalk);

            ulong walkOnly = fullWalk & ~inWalk, hiddenOnly = gpu ? fullHidden & ~inHidden : 0;

            if ((walkOnly | hiddenOnly) != 0)
            {
                for (int cell = 0; cell < 64; cell++)
                {
                    ulong bit = 1UL << cell;

                    if (((walkOnly | hiddenOnly) & bit) == 0)
                    {
                        continue;
                    }

                    for (GameObject o = TileWalk.Begin(chunk, cell, out TileWalk walk); o != null; o = walk.Next())
                    {
                        if (o.IsDestroyed)
                        {
                            continue;
                        }

                        if ((walkOnly & bit) != 0 && WalkAnchorIn(o))
                        {
                            WalkChunkZ.ReportMissed(false, o, chunkX, chunkY, zLo, zHi);
                        }

                        if ((hiddenOnly & bit) != 0 && (o is Land l ? GpuLandVisible(l) : o is Static st && GpuStaticVisible(st)))
                        {
                            WalkChunkZ.ReportMissed(true, o, chunkX, chunkY, zLo, zHi);
                        }
                    }
                }
            }

            if (fullHidden != 0 && _fillSeq % 30 == 0)
            {
                WalkChunkZ.CheckScans++;

                for (int cell = 0; cell < 64; cell++)
                {
                    for (GameObject o = TileWalk.Begin(chunk, cell, out TileWalk walk); o != null; o = walk.Next())
                    {
                        Chunk.WalkZOf(o, out int lo, out int hi);

                        if (!o.IsDestroyed && (lo < chunk.WalkZLo || hi > chunk.WalkZHi))
                        {
                            WalkChunkZ.ReportOutOfRange(o, lo, hi, chunk.WalkZLo, chunk.WalkZHi);
                        }
                    }
                }
            }
        }

        // the walk's anchor cull (AddTileToRenderList) from the object's own position: x beside the culling rectangle, y
        // outside it (stretched land: its MinZ edge)
        private bool WalkAnchorIn(GameObject o)
        {
            int sx = (o.X - o.Y) * 22 - _offset.X - 22;
            int sy = (o.X + o.Y) * 22 - (o.Z << 2) - _offset.Y - 22;

            if (sx < _minPixel.X || sx > _maxPixel.X || sy > _maxPixel.Y)
            {
                return false;
            }

            int top = o is Land { IsStretched: true } land ? sy + (land.Z << 2) - (land.MinZ << 2) : sy;

            return top >= _minPixel.Y;
        }

        // after the chunk loop
        private void ZBandFrameEnd()
        {
            if (_zbandCheck)
            {
                WalkChunkZ.CheckFrames++;
                WalkChunkZ.Summary(_zband);
            }
        }
    }
}
