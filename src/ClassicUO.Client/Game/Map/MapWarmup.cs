// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Globalization;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// Login warm-up: moves the allocation and the garbage collections of the first minute after login out of gameplay frames.
    /// Before this, the chunk pool and the map object recycler started empty, so the first teleports allocated about 150 chunks,
    /// their mesh arrays and about 6 MB of land and statics; that cost 2-3 gen1 collections in the first 40 s and, about 30 s
    /// later, a compacting gen1 that promoted the warm-up (18-19 MB, 7-7.5 ms) into whatever frame was playing (poc-log
    /// "Login warm-up collections").
    /// <list type="bullet">
    /// <item>When the first map is created (login, before the first world frame), <see cref="OnMapCreated"/> fills the chunk pool
    /// (<see cref="Chunk.Prefill"/>, statics mesh layers pre-sized) and the recycler (<see cref="MapObjectRecycler.Prefill"/>).</item>
    /// <item>Then one deliberate collection, while the world isn't visible: the last held frame of the login's settle hold (Gpu
    /// world), or the first world frame (Legacy, the login transition). It promotes everything login allocated into gen2 at
    /// once, so later gen0/gen1 collections copy only what is new.</item>
    /// </list>
    /// CUO_WARMUP=0 restores the old behaviour (no prefill, no collection); CUO_WARMUP_GC=0 keeps the prefill but skips the
    /// collection; CUO_WARMUP_GC=2 makes it a full compacting gen2 instead of two gen1s. CUO_WARMUP_CHUNKS sets the chunk count.
    /// </summary>
    internal static class MapWarmup
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_WARMUP") != "0";
        private static readonly string _gcMode = Environment.GetEnvironmentVariable("CUO_WARMUP_GC");
        public static readonly bool CollectEnabled = Enabled && _gcMode != "0";

        /// <summary>Chunks put in the pool at login (the pool keeps at most <see cref="Constants.PREDICTABLE_CHUNKS"/>).</summary>
        public static readonly int Chunks = int.TryParse(Environment.GetEnvironmentVariable("CUO_WARMUP_CHUNKS"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int c) ? Math.Clamp(c, 0, Constants.PREDICTABLE_CHUNKS) : DefaultChunks;

        // Sizes from a base session (travel, glide, ltravel, bwalk; Gpu world at 1920x1080): 225 chunks were allocated by the
        // end of the login and 289 in all (none after 40 s), with 19,584 lands and 16,387 statics (57 per chunk); after two
        // minutes the statics mesh capacities were 64: 15 chunks, 128: 128, 256: 143, 512: 3.
        public const int DefaultChunks = Constants.PREDICTABLE_CHUNKS;
        /// <summary>Statics mesh layer capacity of a prefilled chunk (sprites; 48 KB of vertices, below the large object heap):
        /// nearly every block fits, busier ones grow as before.</summary>
        public const int StaticsMeshCapacity = 256;
        /// <summary>Map statics prefilled per chunk.</summary>
        public const int StaticsPerChunk = 56;

        private static bool _prefilled;
        private static long _collectDeadline;

        /// <summary>A deliberate collection is waiting for a frame where the world isn't visible.</summary>
        public static bool CollectPending { get; private set; }

        /// <summary>World.MapIndex created a map. The first time: fill the pools and arm the collection.</summary>
        public static void OnMapCreated(World world)
        {
            if (!Enabled || _prefilled)
            {
                return;
            }

            _prefilled = true;
            long start = Stopwatch.GetTimestamp();
            long before = GC.GetAllocatedBytesForCurrentThread();
            int chunks = Chunk.Prefill(world, Chunks, StaticsMeshCapacity);
            world.MapRecycler.Prefill(world, Chunks * 64, Chunks * StaticsPerChunk, Time.Ticks);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Log.Info(string.Format(CultureInfo.InvariantCulture, "[warmup] pools filled in {0:0.0} ms: {1} chunks (statics mesh {2}), {3} lands, {4} statics, {5:0.0} MB",
                ms, chunks, StaticsMeshCapacity, world.MapRecycler.PooledLands, world.MapRecycler.PooledStatics,
                (GC.GetAllocatedBytesForCurrentThread() - before) / 1048576.0));

            CollectPending = CollectEnabled;
            _collectDeadline = 0;
        }

        /// <summary>
        /// GameScene, in a frame whose world isn't shown: <paramref name="held"/> = the settle gate holds this frame,
        /// <paramref name="ready"/> = the view became ready in it (the hold ends next frame). Collects in the last held frame of
        /// the login hold, or in a held frame 1.2 s in (before the hold's 1.5 s timeout), or at once when nothing holds (Legacy).
        /// </summary>
        public static void OnWorldFrame(bool gpu, bool held, bool ready)
        {
            if (!CollectPending)
            {
                return;
            }

            if (gpu)
            {
                if (!held)
                {
                    // a presented Gpu frame without a pending hold: the login hold is over (or never held); wait for the next hold
                    return;
                }

                long now = Stopwatch.GetTimestamp();

                if (_collectDeadline == 0)
                {
                    _collectDeadline = now + Stopwatch.Frequency * 12 / 10;
                }

                if (!ready && now < _collectDeadline)
                {
                    return;
                }
            }

            Collect(gpu ? (ready ? "login hold, last held frame" : "login hold, 1.2 s in") : "first Legacy world frame");
        }

        private static void Collect(string where)
        {
            CollectPending = false;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            TimeSpan pauseBefore = GC.GetTotalPauseDuration();
            long start = Stopwatch.GetTimestamp();

            if (_gcMode == "2")
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            else
            {
                // the first promotes gen1 into gen2 and gen0 into gen1, the second gen1 into gen2: everything login allocated
                // that is still alive ends up in gen2, without marking all of gen2
                GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            GCMemoryInfo gi = GC.GetGCMemoryInfo(GCKind.Any);
            ReadOnlySpan<GCGenerationInfo> gens = gi.GenerationInfo;

            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "[warmup] collected in {0} ({1}): {2:0.0} ms (pause {3:0.0} ms), collections gen0 {4} gen1 {5} gen2 {6}, gen0 {7:0.0} gen1 {8:0.0} gen2 {9:0.0} loh {10:0.0} MB",
                where, _gcMode == "2" ? "full compacting" : "two gen1", ms, (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
                gens.Length > 0 ? gens[0].SizeAfterBytes / 1048576.0 : 0, gens.Length > 1 ? gens[1].SizeAfterBytes / 1048576.0 : 0,
                gens.Length > 2 ? gens[2].SizeAfterBytes / 1048576.0 : 0, gens.Length > 3 ? gens[3].SizeAfterBytes / 1048576.0 : 0));
        }
    }
}
