// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Renderer;
using Uo.Buffers;
using Uo.Render;

namespace ClassicUO.Game
{
    /// <summary>
    /// The large buffer pool's free-buffer caps per use, derived from the client's structure instead of one flat budget.
    /// Outstanding buffers (resident regions, cached page chunks, decodes in flight) are bounded by their owners; these caps
    /// bound only what is kept free between uses.
    /// </summary>
    internal static class LargePoolCaps
    {
        /// <summary>Region caches: Uo.Render ChunkStore (GPU world) and the client's MapLoader.</summary>
        public const int RegionCaches = 2;

        /// <summary>Regions one teleport replaces in one cache. A cache's loaded area (GPU: the view plus its prefetch ring and
        /// art margins; client: Map.PrefetchRadius 104 tiles plus the one-block land stretch) is under one region
        /// (64 blocks = 512 tiles) on a side, so it spans at most 2 x 2 regions.</summary>
        public const int RegionsPerTeleport = 4;

        /// <summary>Largest region buffer: logical regions are 1.2-3.3 MB (3.5 MiB class); a Kind-1 region starts at its
        /// file size x 8 and grows by half, so a few intermediate rents reach the 4 MiB class.</summary>
        public const long RegionBufferBytesMax = 4L << 20;

        /// <summary>PackPages lanes (art, tex, anim). A chunk decode rents its buffer before its insert evicts the lane's
        /// oldest chunk, so a lane needs one free chunk per decode in flight; two let two threads decode in one lane.</summary>
        public const int PageLanes = 3, PageDecodesPerLane = 2;

        /// <summary>A scoped compressed input: a region file (classic packs: at most 0.53 MB) or a page chunk (a compressed
        /// 1 MiB chunk) stays under 1 MiB.</summary>
        public const long ScratchBytesPerThread = 1L << 20;

        public readonly record struct Caps(long GeneralBytes, int RegionCount, long RegionBytes, int PageCount, long PageBytes, int ScratchThreads);

        /// <summary>
        /// General: worker decode outputs in flight are bounded by DecodeBudget.Shared (plus DecodeBudget.Gumps with
        /// gump_decode_budget on); the next burst reuses what the last one returned, so keeping more free than the budget
        /// gains no hits. Plus scoped scratch, at most two large buffers per job (compressed in, decoded out): two on the
        /// main thread, one compressed region file per Uo.Render worker and per MapLoader region load (MaxPending
        /// prefetches plus one synchronous load).
        /// Region: one teleport's swap in both caches. Both load before they evict (MapLoader prefetches up to MaxPending
        /// regions ahead and evicts when it adopts one; ChunkStore evicts regions unused for 120 frames, checked every 64
        /// frames, and bakes in flight pin theirs), so the buffers one teleport's evictions return serve the next.
        /// Page: one chunk buffer per decode in flight per lane.
        /// </summary>
        public static Caps Derive(long decodeBudgetBytes, long gumpBudgetBytes, int renderWorkers, int regionPrefetches, int pageChunkBytes)
        {
            int scratchThreads = 2 + renderWorkers + regionPrefetches + 1;
            long general = decodeBudgetBytes + gumpBudgetBytes + scratchThreads * ScratchBytesPerThread;
            int regions = RegionCaches * RegionsPerTeleport;
            int pages = PageLanes * PageDecodesPerLane;

            return new Caps(general, regions, regions * RegionBufferBytesMax, pages, (long)pages * pageChunkBytes, scratchThreads);
        }

        /// <summary>Sets the caps from this client's structure; <paramref name="gumpBudget"/> is switch gump_decode_budget.</summary>
        public static Caps Apply(bool gumpBudget)
        {
            long shared = DecodeBudget.Shared.CapBytes > 0 ? DecodeBudget.Shared.CapBytes : DecodeBudget.DefaultCapBytes;
            Caps caps = Derive(shared, gumpBudget ? DecodeBudget.GumpCapBytes : 0, WorldOptions.DefaultWorkers, MapLoader.MaxPending,
                Uo.Pack.ChunkedFile.DefaultChunkBytes);

            LargeBufferPool.SetCaps(LargeUse.General, caps.GeneralBytes, int.MaxValue);
            LargeBufferPool.SetCaps(LargeUse.Region, caps.RegionBytes, caps.RegionCount);
            LargeBufferPool.SetCaps(LargeUse.Page, caps.PageBytes, caps.PageCount);

            return caps;
        }
    }
}
