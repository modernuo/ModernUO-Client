// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Text;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    /// <summary>Why a loop wait returned.</summary>
    internal enum WakeReason
    {
        /// <summary>The wait was skipped: something was already due.</summary>
        None,
        /// <summary>The deadline timer (or the wait's own timeout) fired.</summary>
        Timer,
        /// <summary>The socket became readable or closed.</summary>
        Net,
        /// <summary>Window or input messages arrived.</summary>
        Input,
        /// <summary>Another thread called Wake.</summary>
        Wake,
        /// <summary>The wait failed; the loop carries on without sleeping.</summary>
        Failed,
        Count
    }

    /// <summary>
    /// Main-loop counters for the event-loop work (CUO_LOOP_STATS=1, developer builds only): loop iterations (Update calls),
    /// draws, sleeps or waits and why each wait ended, plus latency histograms: network readiness to the packets handled,
    /// SDL input event to the Update that sees it, and the interval between presented frames (pacing). A [loop] line is logged
    /// every CUO_LOOP_STATS_SEC seconds (default 5) with the process CPU time. Nothing here allocates per frame; the report
    /// builds one string per period. FrameStats gets the per-frame iteration and wait counts (loop_ticks, loop_waits).
    /// </summary>
    internal static class LoopStats
    {
#if CUO_DEVTOOLS
        public static readonly bool On = Diagnostics.On || Environment.GetEnvironmentVariable("CUO_LOOP_STATS") == "1";
#else
        public const bool On = false;
#endif

        private static readonly double _periodSec =
            double.TryParse(Environment.GetEnvironmentVariable("CUO_LOOP_STATS_SEC"), out var s) && s > 0 ? s : 5;

        private static long _ticks, _draws, _presents, _drains, _sleeps, _waits, _lateWakes;
        private static readonly long[] _wakes = new long[(int)WakeReason.Count];
        private static long _periodStart;
        private static TimeSpan _cpuStart;
        private static Process _process;
        private static int _reports;

        // network: the last time the client knew the socket had nothing (an empty drain, or a wait that blocked and then woke
        // on the socket); 0 = data was seen since, so the next drain with data is not a new arrival
        private static long _netEmptySince;
        private static ulong _pendingInputNs;
        private static long _lastPresent;

        /// <summary>Readiness to the start of the drain that read it (wait or poll latency).</summary>
        public static readonly LatencyHistogram NetWait = new LatencyHistogram();

        /// <summary>Readiness to the end of the drain that read it (the packets handled).</summary>
        public static readonly LatencyHistogram NetHandled = new LatencyHistogram();

        /// <summary>SDL input event timestamp to the start of the Update after it was pumped.</summary>
        public static readonly LatencyHistogram Input = new LatencyHistogram();

        /// <summary>Interval between presented frames.</summary>
        public static readonly LatencyHistogram FramePacing = new LatencyHistogram();

        /// <summary>How late a timed wait returned after its deadline (event loop only).</summary>
        public static readonly LatencyHistogram WaitLateness = new LatencyHistogram();

        /// <summary>The loop mode the last report names (set by GameController).</summary>
        public static string Mode = "legacy";

        public static void NoteTick() => _ticks++;

        public static void NoteSleep() => _sleeps++;

        public static void NoteWait(WakeReason reason, long latenessTicks, bool timed, bool late)
        {
            _waits++;
            _wakes[(int)reason]++;

            if (timed && reason == WakeReason.Timer)
            {
                WaitLateness.Add(Math.Max(0, latenessTicks));
            }

            if (late)
            {
                _lateWakes++;
            }
        }

        public static void NoteDraw() => _draws++;

        public static void NotePresent()
        {
            _presents++;
            long now = Stopwatch.GetTimestamp();

            if (_lastPresent != 0)
            {
                FramePacing.Add(now - _lastPresent);
            }

            _lastPresent = now;
        }

        /// <summary>The event loop woke on the socket after it had blocked: readiness is now (better than the last empty drain).</summary>
        public static void NoteNetWake(long now)
        {
            if (_netEmptySince != 0)
            {
                _netEmptySince = now;
            }
        }

        /// <summary>One drain: <paramref name="bytes"/> raw bytes read between <paramref name="start"/> and <paramref name="end"/>.</summary>
        public static void NoteDrain(long start, long end, int bytes)
        {
            _drains++;
            if (bytes <= 0)
            {
                _netEmptySince = start;

                return;
            }

            if (_netEmptySince != 0)
            {
                NetWait.Add(start - _netEmptySince);
                NetHandled.Add(end - _netEmptySince);
                _netEmptySince = 0;
            }
        }

        /// <summary>From the SDL event filter (on the main thread, inside the event pump).</summary>
        public static void NoteInputEvent(uint type, ulong timestampNs)
        {
            // keyboard 0x300-0x3FF, mouse 0x400-0x4FF
            if (type < 0x300 || type >= 0x500 || timestampNs == 0)
            {
                return;
            }

            if (_pendingInputNs == 0 || timestampNs < _pendingInputNs)
            {
                _pendingInputNs = timestampNs;
            }
        }

        /// <summary>At the start of Update: the oldest input event pumped since the last Update is now being handled.</summary>
        public static void NoteUpdateStart(ulong nowNs)
        {
            if (_pendingInputNs != 0)
            {
                if (nowNs > _pendingInputNs)
                {
                    Input.AddNs(nowNs - _pendingInputNs);
                }

                _pendingInputNs = 0;
            }

            long now = Stopwatch.GetTimestamp();

            if (_periodStart == 0)
            {
                _periodStart = now;
                _process = Process.GetCurrentProcess();
                _cpuStart = _process.TotalProcessorTime;

                return;
            }

            if ((now - _periodStart) >= (long)(_periodSec * Stopwatch.Frequency))
            {
                Report(now);
            }
        }

        private static void Report(long now)
        {
            double sec = (double)(now - _periodStart) / Stopwatch.Frequency;
            _process.Refresh();
            TimeSpan cpu = _process.TotalProcessorTime;
            double cpuMsPerSec = (cpu - _cpuStart).TotalMilliseconds / sec;

            var sb = new StringBuilder(512);
            sb.Append("[loop] #").Append(++_reports).Append(" mode=").Append(Mode)
              .Append(" sec=").Append(sec.ToString("0.0"))
              .Append(" ticks/s=").Append((_ticks / sec).ToString("0.0"))
              .Append(" draws/s=").Append((_draws / sec).ToString("0.0"))
              .Append(" presents/s=").Append((_presents / sec).ToString("0.0"))
              .Append(" drains/s=").Append((_drains / sec).ToString("0.0"))
              .Append(" sleeps/s=").Append((_sleeps / sec).ToString("0.0"))
              .Append(" waits/s=").Append((_waits / sec).ToString("0.0"))
              .Append(" wakes(none/timer/net/input/wake/failed)=");

            for (int i = 0; i < (int)WakeReason.Count; i++)
            {
                sb.Append(i == 0 ? "" : "/").Append(_wakes[i]);
            }

            sb.Append(" late=").Append(_lateWakes)
              .Append(" cpu_ms/s=").Append(cpuMsPerSec.ToString("0.0"));
            NetWait.Append(sb, " net_wait");
            NetHandled.Append(sb, " net_handled");
            Input.Append(sb, " input");
            FramePacing.Append(sb, " frame");
            WaitLateness.Append(sb, " wait_late");
#if CUO_DEVTOOLS
            if (Diagnostics.On) Diagnostics.Loop(sb.ToString());
            else
#endif
            Log.Info(sb.ToString());

            _ticks = _draws = _presents = _drains = _sleeps = _waits = _lateWakes = 0;
            Array.Clear(_wakes);
            NetWait.Clear();
            NetHandled.Clear();
            Input.Clear();
            FramePacing.Clear();
            WaitLateness.Clear();
            _periodStart = now;
            _cpuStart = cpu;
        }
    }

    /// <summary>A fixed-bucket latency histogram in microseconds (no allocation after construction).</summary>
    internal sealed class LatencyHistogram
    {
        // bucket upper bounds in microseconds; the last bucket is open
        internal static readonly int[] BoundsUs =
        {
            50, 100, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 4000, 6000, 8000, 12000, 16000, 20000, 25000, 33000, 50000,
            75000, 100000, 150000, 250000, 500000, int.MaxValue
        };

        private readonly long[] _counts = new long[BoundsUs.Length];
        private long _n, _sumUs, _maxUs;

        public long Count => _n;

        public void Add(long stopwatchTicks) => AddUs(stopwatchTicks * 1_000_000 / Stopwatch.Frequency);

        public void AddNs(ulong ns) => AddUs((long)(ns / 1000));

        public void AddUs(long us)
        {
            if (us < 0)
            {
                us = 0;
            }

            int i = 0;

            while (us > BoundsUs[i])
            {
                i++;
            }

            _counts[i]++;
            _n++;
            _sumUs += us;

            if (us > _maxUs)
            {
                _maxUs = us;
            }
        }

        /// <summary>The upper bound (µs) of the bucket that holds the given fraction of samples; the max for the open bucket.</summary>
        public long PercentileUs(double p)
        {
            if (_n == 0)
            {
                return 0;
            }

            long target = Math.Max(1, (long)Math.Ceiling(_n * p));
            long seen = 0;

            for (int i = 0; i < _counts.Length; i++)
            {
                seen += _counts[i];

                if (seen >= target)
                {
                    return Math.Min(BoundsUs[i], _maxUs);
                }
            }

            return _maxUs;
        }

        public void Clear()
        {
            Array.Clear(_counts);
            _n = _sumUs = _maxUs = 0;
        }

        public void Append(StringBuilder sb, string name)
        {
            sb.Append(name).Append("(n=").Append(_n);

            if (_n > 0)
            {
                sb.Append(" mean=").Append(_sumUs / _n)
                  .Append(" p50<=").Append(PercentileUs(0.5))
                  .Append(" p90<=").Append(PercentileUs(0.9))
                  .Append(" p99<=").Append(PercentileUs(0.99))
                  .Append(" max=").Append(_maxUs);
            }

            sb.Append(" us)");
        }
    }
}
