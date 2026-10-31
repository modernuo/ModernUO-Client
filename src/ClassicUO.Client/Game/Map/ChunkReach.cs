// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// The chunks a max-Z decision looked up (Map.GetChunkForDecision), as a box in chunk coordinates, and how many of them
    /// were not loaded. Out-of-map lookups are not part of it (known empty).
    /// </summary>
    internal struct ChunkReach
    {
        public int MinX, MinY, MaxX, MaxY;
        public bool Any;

        /// <summary>Chunk lookups that found the chunk not loaded (the answer may change when it loads).</summary>
        public int Missing;

        public readonly bool Complete => Missing == 0;

        public void Add(int chunkX, int chunkY)
        {
            if (!Any)
            {
                MinX = MaxX = chunkX;
                MinY = MaxY = chunkY;
                Any = true;

                return;
            }

            if (chunkX < MinX) MinX = chunkX;
            if (chunkX > MaxX) MaxX = chunkX;
            if (chunkY < MinY) MinY = chunkY;
            if (chunkY > MaxY) MaxY = chunkY;
        }
    }
}
