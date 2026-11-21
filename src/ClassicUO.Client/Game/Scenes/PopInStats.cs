// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System.Diagnostics;
using System.Text;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Dev-only pop-in counters (CLEANUP(release)): every Gpu world frame presented while walking (no settle episode) whose
    /// view was not ready, split by what was missing. The causes are not exclusive. A "run" is a stretch of consecutive
    /// unsettled walking frames; its length in ms is how long something was missing on screen. DevBench logs them with the
    /// settle counters at the end of each plan entry ([popin]). CUO_POPIN_SHOTS=dir[|pairs]: the first frame of a run with
    /// a cause that can show on screen and the first ready frame after it are saved as popNN_a_causes.png / popNN_b.png.
    /// </summary>
    internal static class PopInStats
    {
        internal enum Cause
        {
            /// <summary>A render chunk whose land footprint intersects the view is not baked (a gap).</summary>
            ChunkVisible,
            /// <summary>Only chunks in the art margins around the view are not baked (tall art may reach in).</summary>
            ChunkNear,
            /// <summary>A baked chunk's static art page is not resident (its statics draw as nothing).</summary>
            ChunkPages,
            /// <summary>A dynamic art sprite (ground item, effect, animated static) has no resident page.</summary>
            DynamicArt,
            /// <summary>A dynamic animation frame (mobile, equipment, corpse) has no resident Indexed8 page.</summary>
            DynamicAnim,
            /// <summary>The client walk asked for a chunk it has not loaded (art wait): its items and mobiles are missing.</summary>
            ClientQueued,
            Count
        }

        private static readonly string[] _names = { "chunk_visible", "chunk_near", "chunk_pages", "dyn_art", "dyn_anim", "client_queued" };

        private static readonly long[] _frames = new long[(int)Cause.Count];
        private static readonly long[] _runs = new long[(int)Cause.Count];
        private static readonly long[] _instances = new long[(int)Cause.Count];
        private static long _unsettled, _runCount, _pixels, _maxPixels;
        private static double _runMsTotal, _runMsMax;
        private static int _runMask;
        private static long _runStart = -1;
        private static int _runFrames, _runFramesMax;

        private static readonly string _shotDir;
        private static readonly int _shotPairs;
        private static int _shotIndex, _shotsLeft;
        private static bool _shotAfter;

        static PopInStats()
        {
            string v = System.Environment.GetEnvironmentVariable("CUO_POPIN_SHOTS");

            if (!string.IsNullOrEmpty(v))
            {
                string[] parts = v.Split('|');
                _shotDir = parts[0];
                _shotPairs = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 12;
                _shotsLeft = _shotPairs;
            }
        }

        /// <summary>A presented Gpu world frame outside a settle episode. <paramref name="ready"/> = the settle gate's readiness.</summary>
        public static void OnWalkingFrame(bool ready, WorldRenderer w, int clientQueued)
        {
            if (ready)
            {
                EndRun();

                if (_shotAfter)
                {
                    _shotAfter = false;
                    Client.Game.RequestWorldShot(System.IO.Path.Combine(_shotDir, $"pop{_shotIndex:00}_b.png"));
                }

                return;
            }

            _unsettled++;
            int mask = 0;
            int pageMisses = w.Pages.MissingVisible - w.DynamicMissingArt;

            if (w.Chunks.MissingVisible > 0)
            {
                mask |= 1 << (int)Cause.ChunkVisible;
                _instances[(int)Cause.ChunkVisible] += w.Chunks.MissingVisible;
            }
            else if (w.Chunks.MissingNear > 0)
            {
                mask |= 1 << (int)Cause.ChunkNear;
                _instances[(int)Cause.ChunkNear] += w.Chunks.MissingNear;
            }

            if (pageMisses > 0)
            {
                mask |= 1 << (int)Cause.ChunkPages;
                _instances[(int)Cause.ChunkPages] += pageMisses;
            }

            if (w.DynamicMissingArt > 0)
            {
                mask |= 1 << (int)Cause.DynamicArt;
                _instances[(int)Cause.DynamicArt] += w.DynamicMissingArt;
            }

            if (w.DynamicMissingAnim > 0)
            {
                mask |= 1 << (int)Cause.DynamicAnim;
                _instances[(int)Cause.DynamicAnim] += w.DynamicMissingAnim;
            }

            if (clientQueued > 0)
            {
                mask |= 1 << (int)Cause.ClientQueued;
                _instances[(int)Cause.ClientQueued] += clientQueued;
            }

            _pixels += w.DynamicMissingPixels;

            if (w.DynamicMissingPixels > _maxPixels)
            {
                _maxPixels = w.DynamicMissingPixels;
            }

            for (int i = 0; i < (int)Cause.Count; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    _frames[i]++;

                    if ((_runMask & (1 << i)) == 0)
                    {
                        _runs[i]++;
                    }
                }
            }

            if (_runStart < 0)
            {
                _runStart = Stopwatch.GetTimestamp();
                _runFrames = 0;
                _runCount++;

                // only causes that can show on screen (not ChunkNear alone)
                if (_shotsLeft > 0 && !_shotAfter && (mask & ~(1 << (int)Cause.ChunkNear)) != 0)
                {
                    _shotsLeft--;
                    _shotIndex++;
                    _shotAfter = true;
                    Client.Game.RequestWorldShot(System.IO.Path.Combine(_shotDir, $"pop{_shotIndex:00}_a_{CauseList(mask, '+')}.png"));
                }
            }

            _runFrames++;
            _runMask |= mask;
        }

        /// <summary>A held frame or a settle episode: a walking run ends there.</summary>
        public static void EndRun()
        {
            if (_runStart < 0)
            {
                return;
            }

            double ms = (Stopwatch.GetTimestamp() - _runStart) * 1000.0 / Stopwatch.Frequency;
            _runMsTotal += ms;

            if (ms > _runMsMax)
            {
                _runMsMax = ms;
            }

            if (_runFrames > _runFramesMax)
            {
                _runFramesMax = _runFrames;
            }

            _runStart = -1;
            _runMask = 0;
        }

        private static string CauseList(int mask, char sep)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < (int)Cause.Count; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(sep);
                    }

                    sb.Append(_names[i]);
                }
            }

            return sb.ToString();
        }

        /// <summary>The cumulative counters (DevBench, at the end of each plan entry).</summary>
        public static string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"unsettled walking frames {_unsettled}, runs {_runCount} (ms total {_runMsTotal:0.0}, max {_runMsMax:0.0}; max frames {_runFramesMax}); by cause frames/runs/instances:");

            for (int i = 0; i < (int)Cause.Count; i++)
            {
                sb.Append($" {_names[i]} {_frames[i]}/{_runs[i]}/{_instances[i]}");
            }

            sb.Append($"; dynamic missing pixels total {_pixels}, max per frame {_maxPixels}");

            return sb.ToString();
        }
    }
}
#endif
