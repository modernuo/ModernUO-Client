using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Loop;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Loop
{
    // frame-pacing: the event loop's present on change (fps 0, display-rate cap) and fps cap paths driven by a synthetic clock
    // (microseconds) against a simulated panel, with the loop's own decisions (LoopTiming). A walking scene changes every
    // Update, so every present is "continuous". Each iteration: draw + present (D), the wait the loop plans plus the wake's
    // lateness, the Update before the draw decision (U). The panel scans out at its own rate; with vsync off a refresh shows
    // the latest frame presented before its middle line, with FIFO vsync the oldest queued one (queue of two, Present
    // blocks when full).
    public class FramePacingSimulationTests
    {
        private readonly ITestOutputHelper _output;

        public FramePacingSimulationTests(ITestOutputHelper output) => _output = output;

        internal enum Path
        {
            PresentOnChangeDrawStart, // before the fix: the rate limit runs from the draw start
            PresentOnChangeAnchored, // LoopTiming.PresentAnchor
            FpsCap // fps > 0: LoopTiming.DrawDue's accumulator
        }

        internal sealed class Result
        {
            public double MeanIntervalMs, FramesPerSec;
            public double[] IntervalPercentiles; // p1, p50, p99
            public int Refreshes, Repeats, Skips;
            public double AdvancePxMean, AdvancePxStd;
            public SortedDictionary<int, int> AdvancePx = new SortedDictionary<int, int>();

            public double RepeatPercent => 100.0 * Repeats / Refreshes;
            public double SkipPercent => 100.0 * Skips / Refreshes;
        }

        // mounted run: 22 px per axis over a 100 ms step, offset computed from integer ms (Time.Ticks), truncated to pixels
        private const double PxPerMs = 22.0 / 100;

        internal static Result Run(Path path, double detectedHz, double panelHz, bool vsync, double fpsCap, double updateUs,
            double drawUs, double lateMaxUs, int seed, double seconds = 60)
        {
            var rng = new Random(seed);
            long period = (long)Math.Round(1_000_000 / detectedHz);
            double periodMs = period / 1000.0;
            double capMs = fpsCap > 0 ? 1000.0 / fpsCap : 0;
            double panelUs = 1_000_000 / panelHz;
            double phase = rng.NextDouble() * panelUs;
            long end = (long)(seconds * 1_000_000);

            var presents = new List<(long done, long content)>();
            long anchor = 0, t = 0, lastUpdate = 0;
            double total = 0; // fps cap accumulator (ms)
            var queue = new Queue<(long done, long content)>(); // FIFO vsync
            long nextVblank = (long)phase;
            var shownFifo = new List<long>();

            double Jit(double mean) => mean * (0.6 + 0.8 * rng.NextDouble());

            while (t < end)
            {
                // Update: game time is its start, truncated to ms
                long updateStart = t;
                long content = updateStart / 1000;
                total += (updateStart - lastUpdate) / 1000.0;
                lastUpdate = updateStart;
                t += (long)Jit(updateUs);

                bool due;

                if (path == Path.FpsCap)
                {
                    due = LoopTiming.DrawDue(total, capMs, out double carried);

                    if (due)
                    {
                        total = carried;
                    }
                }
                else
                {
                    double since = anchor == 0 ? double.PositiveInfinity : (t - anchor) / 1000.0;
                    due = LoopTiming.PresentDue(true, since, periodMs, double.PositiveInfinity);
                }

                double waitMs;

                if (due)
                {
                    anchor = path == Path.PresentOnChangeAnchored ? LoopTiming.PresentAnchor(anchor, t, period) : t;
                    t += (long)Jit(drawUs);

                    if (vsync)
                    {
                        // FIFO: drain the queue to the vblanks that passed, block while it holds two frames
                        while (true)
                        {
                            while (nextVblank <= t)
                            {
                                shownFifo.Add(queue.Count > 0 ? queue.Dequeue().content : -1);
                                nextVblank = (long)(phase + (shownFifo.Count) * panelUs);
                            }

                            if (queue.Count < 2)
                            {
                                break;
                            }

                            t = nextVblank;
                        }

                        queue.Enqueue((t, content));
                    }

                    presents.Add((t, content));
                }

                double sinceUpdate = (t - updateStart) / 1000.0;
                waitMs = path == Path.FpsCap
                    ? LoopTiming.MsUntilDraw(false, capMs, total, sinceUpdate)
                    : LoopTiming.MsUntilPresent(true, anchor == 0 ? double.PositiveInfinity : (t - anchor) / 1000.0, periodMs, double.PositiveInfinity);
                double wait = LoopTiming.WaitMs(waitMs, double.PositiveInfinity, 16);

                if (wait > 0)
                {
                    t += (long)(wait * 1000 + rng.NextDouble() * lateMaxUs);
                }
            }

            var r = new Result();
            var intervals = new List<double>();

            for (int i = 1; i < presents.Count; i++)
            {
                intervals.Add((presents[i].done - presents[i - 1].done) / 1000.0);
            }

            intervals.Sort();
            r.MeanIntervalMs = intervals.Average();
            r.FramesPerSec = presents.Count / seconds;
            r.IntervalPercentiles = new[] { intervals[intervals.Count / 100], intervals[intervals.Count / 2], intervals[intervals.Count * 99 / 100] };

            // what each refresh shows
            var shown = new List<long>();

            if (vsync)
            {
                long last = -1;

                foreach (long c in shownFifo)
                {
                    last = c >= 0 ? c : last;
                    shown.Add(last);
                }
            }
            else
            {
                int p = 0;

                for (int k = 0; ; k++)
                {
                    double line = phase + k * panelUs + panelUs / 2;

                    if (line >= end)
                    {
                        break;
                    }

                    while (p + 1 < presents.Count && presents[p + 1].done <= line)
                    {
                        p++;
                    }

                    shown.Add(presents[p].done <= line ? presents[p].content : -1);
                }
            }

            var advances = new List<double>();

            for (int k = 1; k < shown.Count; k++)
            {
                if (shown[k] < 0 || shown[k - 1] < 0)
                {
                    continue;
                }

                long dMs = shown[k] - shown[k - 1];
                int px = (int)(shown[k] * PxPerMs) - (int)(shown[k - 1] * PxPerMs);
                r.Refreshes++;

                if (dMs == 0)
                {
                    r.Repeats++;
                }
                else if (dMs > panelUs / 1000 * 1.5)
                {
                    r.Skips++;
                }

                advances.Add(px);
                r.AdvancePx[px] = r.AdvancePx.TryGetValue(px, out int n) ? n + 1 : 1;
            }

            r.AdvancePxMean = advances.Average();
            r.AdvancePxStd = Math.Sqrt(advances.Select(a => (a - r.AdvancePxMean) * (a - r.AdvancePxMean)).Average());

            return r;
        }

        private static string Describe(string name, Result r)
            => FormattableString.Invariant(
                $"{name,-58} fps={r.FramesPerSec,6:F2} interval mean={r.MeanIntervalMs:F3} p1/p50/p99={r.IntervalPercentiles[0]:F2}/{r.IntervalPercentiles[1]:F2}/{r.IntervalPercentiles[2]:F2} ms  repeats={r.RepeatPercent,5:F1}% skips={r.SkipPercent,4:F1}%  px/refresh mean={r.AdvancePxMean:F2} sd={r.AdvancePxStd:F2} [{string.Join(" ", r.AdvancePx.Select(kv => $"{kv.Key}:{100.0 * kv.Value / r.Refreshes:F0}%"))}]");

        // The distributions for the report (a 72 Hz panel, the player's report); asserts only that every case ran.
        [Fact]
        public void Report_Distributions()
        {
            foreach (var (label, u, late) in new[] { ("fast machine (U 0.3 ms, late <= 0.3 ms)", 300.0, 300.0), ("busy machine (U 1.5 ms, late <= 1 ms)", 1500.0, 1000.0) })
            {
                _output.WriteLine($"--- {label}, draw+present 3 ms, mounted run (0.22 px/ms) ---");

                foreach (double panel in new[] { 72.0, 71.928 })
                {
                    foreach (bool vsync in new[] { false, true })
                    {
                        string v = vsync ? "vsync" : "no vsync";
                        _output.WriteLine(Describe($"panel {panel} {v}, fps 0, draw-start anchor (old)", Run(Path.PresentOnChangeDrawStart, 72, panel, vsync, 0, u, 3000, late, 1)));
                        _output.WriteLine(Describe($"panel {panel} {v}, fps 0, deadline anchor (new)", Run(Path.PresentOnChangeAnchored, 72, panel, vsync, 0, u, 3000, late, 1)));
                        _output.WriteLine(Describe($"panel {panel} {v}, fps 240", Run(Path.FpsCap, 72, panel, vsync, 240, u, 3000, late, 1)));
                    }
                }

                // vsync off at the display rate: how bad it is depends on where the present phase sits against the scanout
                foreach (double panel in new[] { 72.0, 71.928 })
                {
                    foreach (Path p in new[] { Path.PresentOnChangeDrawStart, Path.PresentOnChangeAnchored })
                    {
                        var bad = Enumerable.Range(100, 40).Select(seed => Run(p, 72, panel, false, 0, u, 3000, late, seed, 30))
                            .Select(r => r.RepeatPercent + r.SkipPercent).OrderBy(x => x).ToArray();
                        _output.WriteLine(FormattableString.Invariant(
                            $"phase sweep panel {panel} no vsync {p}: repeats+skips % over 40 phases min/p50/p90/max = {bad[0]:F1}/{bad[20]:F1}/{bad[36]:F1}/{bad[39]:F1}"));
                    }

                    foreach (Path p in new[] { Path.PresentOnChangeDrawStart, Path.PresentOnChangeAnchored, Path.FpsCap })
                    {
                        var sd = Enumerable.Range(100, 40).Select(seed => Run(p, 72, panel, false, p == Path.FpsCap ? 240 : 0, u, 3000, late, seed, 30).AdvancePxStd).OrderBy(x => x).ToArray();
                        _output.WriteLine(FormattableString.Invariant($"phase sweep panel {panel} no vsync {p}: px sd p50/max = {sd[20]:F2}/{sd[39]:F2}"));
                    }
                }
            }
        }

        [Theory]
        [InlineData(300, 300)]
        [InlineData(1500, 1000)]
        [InlineData(3000, 1000)]
        public void Anchored_Present_On_Change_Holds_The_Display_Rate(double updateUs, double lateUs)
        {
            var old = Run(Path.PresentOnChangeDrawStart, 72, 72, false, 0, updateUs, 3000, lateUs, 7);
            var fix = Run(Path.PresentOnChangeAnchored, 72, 72, false, 0, updateUs, 3000, lateUs, 7);

            fix.FramesPerSec.Should().BeApproximately(72, 0.2);
            old.FramesPerSec.Should().BeLessThan(fix.FramesPerSec - 1, "every interval carried the wake lateness and the Update");
            fix.RepeatPercent.Should().BeLessThan(old.RepeatPercent);
        }

        [Fact]
        public void Anchored_Present_On_Change_Repeats_Few_Refreshes_On_A_Busy_Machine()
        {
            var fix = Run(Path.PresentOnChangeAnchored, 72, 72, false, 0, 1500, 3000, 1000, 3);
            var old = Run(Path.PresentOnChangeDrawStart, 72, 72, false, 0, 1500, 3000, 1000, 3);

            old.RepeatPercent.Should().BeGreaterThan(5, "~66 frames/s on a 72 Hz panel shows one frame twice every ~12 refreshes");
            fix.RepeatPercent.Should().BeLessThan(old.RepeatPercent / 2);
        }

        [Fact]
        public void PresentAnchor_Carries_Small_Lateness_And_Restarts_After_A_Gap()
        {
            const long P = 13_889;
            LoopTiming.PresentAnchor(0, 5_000, P).Should().Be(5_000, "the first present");
            LoopTiming.PresentAnchor(100_000, 200_000, 0).Should().Be(200_000, "no display rate: the draw start, as before");
            LoopTiming.PresentAnchor(100_000, 100_000 + P + 1_500, P).Should().Be(100_000 + P, "late by 1.5 ms: the deadline");
            LoopTiming.PresentAnchor(100_000, 100_000 + P - 400, P).Should().Be(100_000 + P, "drawn within the slack before it");
            LoopTiming.PresentAnchor(100_000, 100_000 + P + P / 2, P).Should().Be(100_000 + P + P / 2, "half an interval late: a new phase");
            LoopTiming.PresentAnchor(100_000, 900_000, P).Should().Be(900_000, "after an idle screen");
        }

        [Fact]
        public void An_Idle_Change_Never_Presents_Within_Half_An_Interval_Of_The_Next()
        {
            const long P = 13_889;
            long anchor = 100_000;

            // a change arrives just under half an interval late: kept on the grid, so the next present may come half an interval on
            for (long late = 0; late < P * 3; late += 97)
            {
                long now = anchor + P + late;
                long next = LoopTiming.PresentAnchor(anchor, now, P) + P;
                (next - now).Should().BeGreaterThan(P / 2);
            }
        }
    }
}
