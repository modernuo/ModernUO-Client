// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// walk_chunk_z (zband, default off): the world fill's vertical (S) band per chunk from the chunk's recorded Z range
    /// (Chunk.WalkZLo/WalkZHi) instead of every sbyte Z. FillDiamond sized the S band for anchors moved by -508..+512 px, so at
    /// 1080p the walk visited about 1,000 px of tile rows beyond the view and culled their objects one by one by anchor y. The
    /// range is conservative: land (its own Z, MinZ and stretched corners), statics and multi components at load, grown by
    /// every object that joins a tile list or changes Z while on one (GameObject.Z), recomputed exactly at a mesh rebuild.
    /// CUO_WALK_CHUNK_Z=1|0 overrides the setting in developer builds. CUO_WALK_CHUNK_Z_CHECK=1 computes the full-band masks
    /// next to the tight ones every frame and logs each object the full band would have walked onto the render list (or hidden
    /// on the Gpu world) that the tight band skipped, and each object found outside its chunk's recorded range.
    /// </summary>
    internal static class WalkChunkZ
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_WALK_CHUNK_Z") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_WALK_CHUNK_Z_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.WalkChunkZ ?? false);

        // checker totals since the last summary
        internal static int CheckFrames, CheckChunks, CheckTightChunks, CheckWalkMissed, CheckHiddenMissed, CheckOutOfRange, CheckScans;
        internal static long CheckFullTiles, CheckTightTiles;
        private static long _missedTotal, _rangeTotal;
        private static uint _summaryAt;
        private static int _logged;

        internal static void ReportMissed(bool hidden, GameObject obj, int chunkX, int chunkY, int zLo, int zHi)
        {
            if (hidden)
            {
                CheckHiddenMissed++;
            }
            else
            {
                CheckWalkMissed++;
            }

            _missedTotal++;

            if (_logged++ < 200)
            {
                Log.Warn($"[walk-chunk-z-check] {(hidden ? "hidden" : "walk")} missed {obj.GetType().Name} 0x{obj.Graphic:X4} serial 0x{(obj as Entity)?.Serial ?? 0:X8} at {obj.X},{obj.Y},{obj.Z} chunk {chunkX},{chunkY} range {zLo}..{zHi}");
            }
        }

        internal static void ReportOutOfRange(GameObject obj, int lo, int hi, int zLo, int zHi)
        {
            CheckOutOfRange++;
            _rangeTotal++;

            if (_logged++ < 200)
            {
                Log.Warn($"[walk-chunk-z-check] out of range {obj.GetType().Name} 0x{obj.Graphic:X4} serial 0x{(obj as Entity)?.Serial ?? 0:X8} at {obj.X},{obj.Y} z {lo}..{hi}, chunk {obj.TileChunk?.X},{obj.TileChunk?.Y} range {zLo}..{zHi}");
            }
        }

        internal static void Summary(bool on)
        {
            if (Time.Ticks < _summaryAt)
            {
                return;
            }

            _summaryAt = Time.Ticks + 5000;
            Log.Info($"[walk-chunk-z-check] walk_chunk_z {(on ? "on" : "off")}: {CheckFrames} frames, chunks full {CheckChunks} tight {CheckTightChunks}, " +
                     $"band tiles full {CheckFullTiles} tight {CheckTightTiles}, range scans {CheckScans}; mismatches walk {CheckWalkMissed} hidden {CheckHiddenMissed} " +
                     $"out-of-range {CheckOutOfRange}; session mismatches {_missedTotal} out-of-range {_rangeTotal}");
            CheckFrames = CheckChunks = CheckTightChunks = CheckWalkMissed = CheckHiddenMissed = CheckOutOfRange = CheckScans = 0;
            CheckFullTiles = CheckTightTiles = 0;
        }
    }
}
