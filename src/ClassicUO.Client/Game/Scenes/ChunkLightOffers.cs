// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    internal interface IChunkLightSink
    {
        void Offer(GameObject obj, ChunkLightCache cache, int row, bool ground);
    }

    internal struct ChunkLightVisits
    {
        internal int Cells, Objects, Rebuilds, Overflows, VisitedBytes;
    }

    internal static class ChunkLightOffers
    {
        // Both renderers and the checker share only enumeration; admission remains the live scene's adapter.
        internal static ChunkLightVisits Collect<T>(Chunk chunk, StaticTiles[] data, bool cached, bool stats, ref T sink)
            where T : struct, IChunkLightSink
        {
            ChunkLightVisits visits = default;
            if (cached)
            {
                ChunkLightCache cache = chunk.LightCache ??= new ChunkLightCache();
                int builds = cache.Rebuilds;
                bool ready = cache.Refresh(chunk, data);
                if (stats)
                {
                    visits.Rebuilds = cache.Rebuilds - builds;
                    visits.VisitedBytes = (int)cache.RetainedBytes;
                }
                if (ready)
                {
                    if (stats) visits.Objects = cache.Count;
                    for (int i = 0; i < cache.Count; i++)
                    {
                        GameObject obj = cache.Resolve(i);
                        if (obj != null) sink.Offer(obj, cache, i, cache.Kind[i] != 0);
                    }
                    return visits;
                }
                if (stats) visits.Overflows = 1;
            }

            if (stats) visits.Objects = chunk.LightSourceCount;
            for (int i = 0; i < chunk.LightSourceCount; i++) sink.Offer(chunk.LightSources[i], null, i, false);
            DynamicTileIndex dyn = chunk.Dynamic;
            if (dyn == null || dyn.Count == 0) return visits;
            for (ulong cells = dyn.Cells; cells != 0; cells &= cells - 1)
            {
                int cell = System.Numerics.BitOperations.TrailingZeroCount(cells);
                var entries = dyn.RawCell(cell, out int n);
                if (stats) { visits.Cells++; visits.Objects += n; }
                for (int i = 0; i < n; i++)
                    if (entries[i].Obj is Item item) sink.Offer(item, null, i, true);
            }
            return visits;
        }
    }
}
