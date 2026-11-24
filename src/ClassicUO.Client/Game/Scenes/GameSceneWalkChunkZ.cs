// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    // The fill's per-chunk S band from the chunk's recorded Z range (Chunk.WalkZLo/Hi). FillDiamond used to size the S band
    // for anchors moved by -508..+512 px; the range is conservative: land (its own Z, MinZ and stretched corners), statics and
    // multi components at load, grown by every object that joins a tile list or changes Z while on one (GameObject.Z),
    // recomputed exactly at a mesh rebuild.
    internal partial class GameScene
    {
        private bool _zbandOcc;
        private uint _fillSeq;
        // with light_occlusion: the chunks of the full bands, so the occluder search sees the chunks it saw before the
        // per-chunk band (a chunk the tight band drops has nothing drawn, but its art may still reach a light's rectangle)
        private readonly List<Chunk> _occChunks = new();

        private List<Chunk> OccluderChunks => _zbandOcc ? _occChunks : _visibleChunks;

        /// <summary>
        /// The chunk's fill masks for its recorded Z range (Chunk.WalkZLo/Hi). zForce: tiles may join the walk without a
        /// camera move (the range grew, or the chunk was not banded at the last fill), so walked positions are checked against
        /// the camera generation.
        /// </summary>
        private ulong ZBandChunkMasks(Chunk chunk, int chunkX, int chunkY, bool gpu, in FillDiamond.Bands diamond, out ulong inHidden, out bool zForce)
        {
            int zLo = chunk.WalkZLo, zHi = chunk.WalkZHi;
            ulong inWalk = FillDiamond.ChunkMasks(gpu, chunkX, chunkY, in diamond, zLo, zHi, out inHidden);

            if (_zbandOcc)
            {
                FillDiamond.ChunkMasks(gpu, chunkX, chunkY, in diamond, out ulong fullHidden);

                if (fullHidden != 0)
                {
                    _occChunks.Add(chunk);
                }
            }

            zForce = false;

            if (inHidden != 0)
            {
                zForce = chunk.FillSeq != _fillSeq - 1 || zLo != chunk.FillZLo || zHi != chunk.FillZHi;
                chunk.FillSeq = _fillSeq;
                chunk.FillZLo = zLo;
                chunk.FillZHi = zHi;
            }

            return inWalk;
        }
    }
}
