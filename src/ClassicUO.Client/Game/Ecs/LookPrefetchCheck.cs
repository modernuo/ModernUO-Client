// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// CUO_LOOK_PREFETCH_CHECK=1 (CLEANUP(release)): asset_prefetch_on_packet's measure, both switch states. Every look a
    /// WorldWriter operation noted (LookPrefetch.Note) is tracked from packet arrival; at each draw start it is polled
    /// (LookPrefetch.LookComplete: Legacy direction uploaded or art decoded; Gpu anim pages resident). Reports per kind the
    /// arrival-to-first-complete-draw latency (p50/p95/max), looks complete at their first draw, frames that drew a tracked
    /// look incomplete, and looks never complete within 3 s. Every 5 s, with the switch state.
    /// </summary>
    internal static class LookPrefetchCheck
    {
        private const int Tracked = 256, Samples = 2048;
        private static readonly uint[] _serial = new uint[Tracked];
        private static readonly LookPrefetch.Kind[] _kind = new LookPrefetch.Kind[Tracked];
        private static readonly long[] _arrival = new long[Tracked];
        private static readonly bool[] _polled = new bool[Tracked];
        private static int _count;

        private static readonly long[][] _lat = { new long[Samples], new long[Samples], new long[Samples] };
        private static readonly int[] _latCount = new int[3];
        private static readonly long[] _firstComplete = new long[3], _arrived = new long[3];
        private static long _incompleteFrames, _never, _gone, _overflow, _start;
        private static bool _switch;

        public static void Arrived(uint serial, LookPrefetch.Kind kind, bool on)
        {
            if (serial == 0)
            {
                return;
            }

            _switch = on;
            _arrived[(int)kind]++;

            for (int i = 0; i < _count; i++)
            {
                if (_serial[i] == serial)
                {
                    return; // the earliest arrival of a look still pending counts
                }
            }

            if (_count == Tracked)
            {
                _overflow++;

                return;
            }

            _serial[_count] = serial;
            _kind[_count] = kind;
            _arrival[_count] = Stopwatch.GetTimestamp();
            _polled[_count] = false;
            _count++;
        }

        /// <summary>Draw start.</summary>
        public static void Draw(World world)
        {
            long now = Stopwatch.GetTimestamp();

            if (_start == 0)
            {
                _start = now;
            }

            bool incomplete = false;

            for (int i = 0; i < _count; i++)
            {
                Entity e = world?.Get(_serial[i]);

                if (e is Item item && _kind[i] == LookPrefetch.Kind.Equip && item.Container != 0 && world.Get(item.Container) is Mobile wearer)
                {
                    e = wearer;
                }

                bool drop;

                if (e == null || e.IsDestroyed || world.Player == null || e.Distance > world.ClientViewRange)
                {
                    _gone++;
                    drop = true;
                }
                else if (LookPrefetch.LookComplete(e))
                {
                    int k = (int)_kind[i];

                    if (!_polled[i])
                    {
                        _firstComplete[k]++;
                    }

                    if (_latCount[k] < Samples)
                    {
                        _lat[k][_latCount[k]++] = now - _arrival[i];
                    }

                    drop = true;
                }
                else if (now - _arrival[i] > Stopwatch.Frequency * 3)
                {
                    _never++;
                    drop = true;

                    if (_never <= 20)
                    {
                        Log.Warn($"[look-prefetch-check] 0x{_serial[i]:X8} ({_kind[i]}) not complete 3 s after its packet, switch={(_switch ? "on" : "off")}");
                    }
                }
                else
                {
                    _polled[i] = true;
                    incomplete = true;
                    drop = false;
                }

                if (drop)
                {
                    _count--;
                    _serial[i] = _serial[_count];
                    _kind[i] = _kind[_count];
                    _arrival[i] = _arrival[_count];
                    _polled[i] = _polled[_count];
                    i--;
                }
            }

            if (incomplete)
            {
                _incompleteFrames++;
            }

            if (now - _start >= Stopwatch.Frequency * 5)
            {
                Summary();
                _start = now;
            }
        }

        private static void Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[look-prefetch-check] switch={(_switch ? "on" : "off")}");

            for (int k = 0; k < 3; k++)
            {
                var span = _lat[k].AsSpan(0, _latCount[k]);
                span.Sort();
                double f = 1000.0 / Stopwatch.Frequency;
                double p50 = span.Length == 0 ? 0 : span[span.Length / 2] * f;
                double p95 = span.Length == 0 ? 0 : span[Math.Min(span.Length - 1, span.Length * 95 / 100)] * f;
                double max = span.Length == 0 ? 0 : span[^1] * f;
                sb.Append(FormattableString.Invariant($" {(LookPrefetch.Kind)k}: arrived {_arrived[k]} complete {span.Length} complete_at_first_draw {_firstComplete[k]} p50_ms {p50:F1} p95_ms {p95:F1} max_ms {max:F1};"));
                _latCount[k] = 0;
                _firstComplete[k] = _arrived[k] = 0;
            }

            sb.Append($" incomplete_frames {_incompleteFrames} never_complete {_never} gone {_gone} overflow {_overflow} tracked {_count}; prefetch resolved {LookPrefetch.Resolved} requested {LookPrefetch.Requested} dropped {LookPrefetch.Dropped}");
            Log.Info(sb.ToString());
            _incompleteFrames = _never = _gone = _overflow = 0;
        }
    }
}
#endif
