using System;
using System.Diagnostics;
using System.Globalization;
using ClassicUO.Assets;

namespace ClassicUO.Renderer.Animations
{
    public sealed partial class Animations
    {
        /// <summary>
        /// anim_async_turn (settings.json, default off; the client sets it every Update): a direction or action of a body
        /// already drawn decodes on a worker when its pack chunks are not cached, as a first sight does, instead of on the
        /// main thread (a turn or new action of a visible mobile was a main-thread decode: Legacy p90 ~17 ms, bursts of 16+
        /// ms). The draw keeps the mobile's previous look until the new direction is uploaded (bounded hold).
        /// </summary>
        public static bool AsyncTurn;

        /// <summary>Whether a direction load goes to a worker: never for a sync caller (picking) or with chunks already
        /// cached (no decompress); a body's first sight and an evicted direction always; any other direction only with
        /// <paramref name="asyncTurn"/>.</summary>
        public static bool DecodesOnWorker(bool sync, bool bodyDrawn, bool evicted, bool asyncTurn, bool resident) =>
            AsyncFirstSight && !sync && (!bodyDrawn || evicted || asyncTurn) && !resident;

        /// <summary>
        /// anim_async_turn's early request (a direction a mobile will likely draw next): a direction never loaded is
        /// decoded on a worker even when its chunks are cached, so a prefetch never decodes on the main thread. A loaded,
        /// pending or evicted direction is left alone (the draw reloads an evicted one, with the LRU's accounting).
        /// </summary>
        public void PrefetchDirection(ushort id, byte action, byte dir, bool isEquip)
        {
            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS || id >= _animationLoader.BodyCount)
            {
                return;
            }

            if (_queue.HasReady)
            {
                DrainReady();
            }

            id = _animationLoader.ResolveBody(id, false, out _);
            var type = (AnimationGroupsType)_animationLoader.GetBody(id).Type;

            if (isEquip && type != AnimationGroupsType.Equipment && type != AnimationGroupsType.Human)
            {
                return;
            }

            ref var actions = ref _cache[id];
            actions ??= new SpriteInfo[AnimationsLoader.MAX_ACTIONS][][];
            ref var dirs = ref actions[action];
            dirs ??= new SpriteInfo[AnimationsLoader.MAX_DIRECTIONS][];

            if (dirs[dir] == null && (_evictedAt.Count == 0 || !_evictedAt.ContainsKey(DirKey(id, action, dir))))
            {
                dirs[dir] = LoadDirection(id, action, dir, sync: false, prefetch: true);
#if CUO_DEVTOOLS
                if (AsyncTurnCheck)
                {
                    AsyncTurnPrefetched++;
                }
#endif
            }
        }

#if CUO_DEVTOOLS
        /// <summary>
        /// CUO_ANIM_ASYNC_TURN_CHECK=1 (CLEANUP(release)), both switch states: the main-thread direction decodes left (sync
        /// callers such as picking; chunks cached; chunks not cached, the stall the switch removes: 0 with it on), their
        /// durations, the turn directions sent to a worker and their delay to upload (p50/p95/p99/max; over
        /// <see cref="TurnCapMs"/>, the hold cap, counted), and every uploaded turn direction compared with what the sync load
        /// writes (frame count, size, centre, a texture for each non-empty frame).
        /// </summary>
        public static bool AsyncTurnCheck = Environment.GetEnvironmentVariable("CUO_ANIM_ASYNC_TURN_CHECK") == "1";

        /// <summary>The client's hold cap (LookPrefetch.HoldMaxMs): a turn delay above it drew the new look partial.</summary>
        public static int TurnCapMs = 50;

        public static long AsyncTurnSubmitted, AsyncTurnUploaded, AsyncTurnMismatches, AsyncTurnCapHits, AsyncTurnPrefetched;
        private static long _syncPick, _syncResident, _syncStall, _syncStall16, _syncResident16;
        private static readonly long[] _turnDelay = new long[4096], _stallTicks = new long[4096];
        private static int _turnDelayCount, _stallCount;
        private static long _syncStallMax, _syncResidentMax, _syncPickMax;

        /// <summary>Tests: sees each mismatch line.</summary>
        public static Action<string> AsyncTurnMismatchObserver;

        private static void NoteSyncDecode(bool sync, bool resident, long ticks)
        {
            long ms16 = Stopwatch.Frequency * 16 / 1000;

            if (sync)
            {
                _syncPick++;
                _syncPickMax = Math.Max(_syncPickMax, ticks);
            }
            else if (resident)
            {
                _syncResident++;
                _syncResidentMax = Math.Max(_syncResidentMax, ticks);
                _syncResident16 += ticks >= ms16 ? 1 : 0;
            }
            else
            {
                _syncStall++;
                _syncStallMax = Math.Max(_syncStallMax, ticks);
                _syncStall16 += ticks >= ms16 ? 1 : 0;

                if (_stallCount < _stallTicks.Length)
                {
                    _stallTicks[_stallCount++] = ticks;
                }
            }
        }

        private void NoteTurnUploaded(DirectionJob job)
        {
            AsyncTurnUploaded++;
            long delay = Stopwatch.GetTimestamp() - job.TurnSubmitted;

            if (delay > Stopwatch.Frequency * TurnCapMs / 1000)
            {
                AsyncTurnCapHits++;
            }

            if (_turnDelayCount < _turnDelay.Length)
            {
                _turnDelay[_turnDelayCount++] = delay;
            }

            // the sync path's frames for the same entry: count, size and centre from the tables, a texture when non-empty
            int bad = -1;

            for (int i = 0; i < job.Sprites.Length && bad < 0; i++)
            {
                ref readonly var frame = ref _animationLoader.GetFrame(job.Entry.FirstFrame + (uint)i);
                ref readonly var got = ref job.Sprites[i];
                bool empty = frame.Sprite.OrigW == 0 || frame.Sprite.OrigH == 0;

                if (!empty && (got.Texture == null || got.UV.Width != frame.Sprite.OrigW || got.UV.Height != frame.Sprite.OrigH ||
                    got.Center.X != frame.CenterX || got.Center.Y != frame.CenterY))
                {
                    bad = i;
                }
            }

            if (bad >= 0 || job.Sprites.Length != job.Entry.FrameCount)
            {
                AsyncTurnMismatches++;
                string line = $"[anim-async-turn-check] body 0x{job.Body:X4} action {job.Action} dir {job.Dir}: worker frames differ from the sync load " +
                    $"(frames {job.Sprites.Length} of {job.Entry.FrameCount}, first bad {bad}), anim_async_turn={(AsyncTurn ? "on" : "off")}";
                AsyncTurnMismatchObserver?.Invoke(line);

                if (AsyncTurnMismatches <= 20)
                {
                    ClassicUO.Utility.Logging.Log.Warn(line);
                }
            }
        }

        /// <summary>The checker's summary since the last call; then empty.</summary>
        public static string AsyncTurnSummary()
        {
            double f = 1000.0 / Stopwatch.Frequency;
            var inv = CultureInfo.InvariantCulture;
            string r = string.Format(inv,
                "anim_async_turn={0} main-thread decodes: stall {1} (p50 {2:F1} p99 {3:F1} max {4:F1} ms, >=16ms {5}) cached {6} (max {7:F1} ms, >=16ms {8}) sync_callers {9} (max {10:F1} ms); " +
                "turns async {11} uploaded {12} delay_ms {13} cap_hits {14} mismatches {15}; prefetched directions {16}",
                AsyncTurn ? "on" : "off", _syncStall, P(_stallTicks, _stallCount, 50) * f, P(_stallTicks, _stallCount, 99) * f, _syncStallMax * f, _syncStall16,
                _syncResident, _syncResidentMax * f, _syncResident16, _syncPick, _syncPickMax * f,
                AsyncTurnSubmitted, AsyncTurnUploaded, Dist(_turnDelay, _turnDelayCount, f), AsyncTurnCapHits, AsyncTurnMismatches, AsyncTurnPrefetched);
            _syncPick = _syncResident = _syncStall = _syncStall16 = _syncResident16 = 0;
            _syncStallMax = _syncResidentMax = _syncPickMax = 0;
            AsyncTurnSubmitted = AsyncTurnUploaded = AsyncTurnCapHits = AsyncTurnPrefetched = 0;
            _turnDelayCount = _stallCount = 0;

            return r;
        }

        private static long P(long[] a, int n, int pct)
        {
            if (n == 0)
            {
                return 0;
            }

            Array.Sort(a, 0, n);

            return a[Math.Min(n - 1, n * pct / 100)];
        }

        private static string Dist(long[] a, int n, double f) => string.Format(CultureInfo.InvariantCulture, "n {0} p50 {1:F1} p95 {2:F1} p99 {3:F1} max {4:F1}",
            n, P(a, n, 50) * f, P(a, n, 95) * f, P(a, n, 99) * f, n == 0 ? 0 : a[n - 1] * f);
#endif
    }
}
