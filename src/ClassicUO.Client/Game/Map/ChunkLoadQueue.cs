// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Map
{
    internal enum ChunkLoadResult
    {
        /// <summary>The chunk was loaded now: counts against the budget, the request is done.</summary>
        Loaded,
        /// <summary>Its map data is still being decoded: the request stays queued.</summary>
        NotReady,
        /// <summary>Nothing to do (already loaded, outside the map): the request is dropped.</summary>
        Done
    }

    /// <summary>
    /// The chunks waiting to be loaded, and the rule that loads them a few at a time (Map.UpdateLoading, once per frame):
    /// <list type="bullet">
    /// <item>nearest the player first (Chebyshev distance, then squared distance);</item>
    /// <item>chunks within <see cref="RingRadius"/> chunks of the player (Map uses 0: the player's own chunk) load whatever
    /// the budget, so the player's tile, its roof test and its z are there as soon as the map data is;</item>
    /// <item>the others load until the frame's time budget is spent (ring loads count against it), and at least one per
    /// frame so the queue always drains;</item>
    /// <item>a request not repeated for <see cref="StaleFrames"/> frames is dropped (the chunk left the screen), unless it
    /// is sticky: a chunk that objects are waiting for stays queued until it loads.</item>
    /// </list>
    /// Game logic that needs another chunk now does not wait for the queue: it loads it on demand (Map.GetChunk with
    /// load = true, Pathfinder within the view range). Main thread only. No allocation per frame once the queue has
    /// grown to its working size.
    /// </summary>
    internal sealed class ChunkLoadQueue
    {
        public const uint StaleFrames = 3;

        public ChunkLoadQueue(int ringRadius = 0)
        {
            RingRadius = ringRadius;
        }

        public int RingRadius { get; }

        private struct Entry
        {
            public uint LastFrame;
            public bool Sticky;
        }

        private struct Candidate : IComparable<Candidate>
        {
            public int Ring, Dist2, ChunkX, ChunkY;

            public int CompareTo(Candidate other)
            {
                int c = Ring.CompareTo(other.Ring);

                return c != 0 ? c : Dist2.CompareTo(other.Dist2);
            }
        }

        private readonly Dictionary<long, Entry> _requests = new Dictionary<long, Entry>();
        private readonly List<long> _remove = new List<long>();
        private Candidate[] _order = new Candidate[64];

        public int Count => _requests.Count;

        /// <summary>Loads done by the last Drain, how many of them were ring chunks, and the requests left waiting on map data.</summary>
        public int LastLoaded, LastRingLoaded, LastNotReady;

        private static long Key(int chunkX, int chunkY) => ((long)chunkX << 32) | (uint)chunkY;

        public bool Contains(int chunkX, int chunkY) => _requests.ContainsKey(Key(chunkX, chunkY));

        /// <summary>Asks for chunk (chunkX, chunkY) in frame <paramref name="frame"/>; repeated each frame it is wanted.</summary>
        public void Request(int chunkX, int chunkY, uint frame, bool sticky = false)
        {
            long key = Key(chunkX, chunkY);
            _requests.TryGetValue(key, out var r);
            r.LastFrame = frame;
            r.Sticky |= sticky;
            _requests[key] = r;
        }

        public void Remove(int chunkX, int chunkY) => _requests.Remove(Key(chunkX, chunkY));

        public void Clear() => _requests.Clear();

        /// <summary>
        /// Loads queued chunks nearest (centerX, centerY) first through <paramref name="load"/>: ring chunks always, the
        /// rest while <paramref name="clock"/> is before <paramref name="deadline"/> (and at least one).
        /// </summary>
        public void Drain(int centerX, int centerY, uint frame, long deadline, Func<long> clock, Func<int, int, ChunkLoadResult> load)
        {
            LastLoaded = LastRingLoaded = LastNotReady = 0;

            if (_requests.Count == 0)
            {
                return;
            }

            if (_order.Length < _requests.Count)
            {
                _order = new Candidate[Math.Max(_requests.Count, _order.Length * 2)];
            }

            int n = 0;
            _remove.Clear();

            foreach (var kv in _requests)
            {
                if (!kv.Value.Sticky && frame - kv.Value.LastFrame > StaleFrames)
                {
                    _remove.Add(kv.Key);

                    continue;
                }

                int cx = (int)(kv.Key >> 32), cy = (int)(uint)kv.Key;
                int dx = cx - centerX, dy = cy - centerY;
                _order[n++] = new Candidate { Ring = Math.Max(Math.Abs(dx), Math.Abs(dy)), Dist2 = dx * dx + dy * dy, ChunkX = cx, ChunkY = cy };
            }

            foreach (long key in _remove)
            {
                _requests.Remove(key);
            }

            Array.Sort(_order, 0, n);
            int budgeted = 0;

            for (int i = 0; i < n; i++)
            {
                ref var c = ref _order[i];
                bool ring = c.Ring <= RingRadius;

                if (!ring && budgeted > 0 && clock() >= deadline)
                {
                    break;
                }

                var result = load(c.ChunkX, c.ChunkY);

                if (result == ChunkLoadResult.NotReady)
                {
                    LastNotReady++;

                    continue;
                }

                _requests.Remove(Key(c.ChunkX, c.ChunkY));

                if (result == ChunkLoadResult.Loaded)
                {
                    LastLoaded++;

                    if (ring)
                    {
                        LastRingLoaded++;
                    }
                    else
                    {
                        budgeted++;
                    }
                }
            }
        }
    }
}
