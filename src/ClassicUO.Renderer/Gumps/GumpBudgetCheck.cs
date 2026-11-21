#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Renderer.Gumps
{
    /// <summary>
    /// CLEANUP(release) CUO_GUMP_BUDGET_CHECK=1: watches gump worker decodes once per frame (Gump.DrainReady, main thread).
    /// Switch gump_decode_budget on: the pooled bytes the decoded pixels hold must not exceed what
    /// <see cref="DecodeBudget.Gumps"/> admitted (a mismatch is logged with both values); waits for room are counted.
    /// Off: the bytes the budget would have counted and the frames they were over its cap (the waits it would have
    /// caused). A summary every 10 s names the switch state and the counts.
    /// </summary>
    internal static class GumpBudgetCheck
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_GUMP_BUDGET_CHECK") == "1";

        private static long _lastSummary;
        private static int _mismatches, _frames, _framesWaiting, _framesOverCap;
        private static long _peakHeld, _peakAdmitted;

        public static void Observe(FirstSightDecoder decoder)
        {
            Check(decoder.HeldBytes, decoder.CountedIn, out string mismatch);

            if (mismatch != null && ++_mismatches <= 50)
            {
                Log.Warn($"[gump-budget-check] {mismatch}");
            }

            long now = Stopwatch.GetTimestamp();

            if (_lastSummary == 0)
            {
                _lastSummary = now;
            }
            else if (now - _lastSummary >= Stopwatch.Frequency * 10)
            {
                _lastSummary = now;
                Log.Info($"[gump-budget-check] switch={(decoder.CountedIn != null ? "on" : "off")} frames={_frames} mismatches={_mismatches} " +
                         $"peakHeldMB={_peakHeld / 1048576.0:0.0} peakAdmittedMB={_peakAdmitted / 1048576.0:0.0} framesWaiting={_framesWaiting} " +
                         $"framesOverCap={_framesOverCap} capMB={DecodeBudget.GumpCapBytes >> 20}");
            }
        }

        /// <summary>One observation; <paramref name="mismatch"/> is the log text when held bytes exceed admitted ones.</summary>
        internal static void Check(long held, DecodeBudget budget, out string mismatch)
        {
            mismatch = null;
            _frames++;
            _peakHeld = Math.Max(_peakHeld, held);

            if (budget == null)
            {
                if (held > DecodeBudget.GumpCapBytes)
                {
                    _framesOverCap++;
                }

                return;
            }

            long admitted = budget.InFlightBytes;
            _peakAdmitted = Math.Max(_peakAdmitted, admitted);

            if (budget.Waiting > 0)
            {
                _framesWaiting++;
            }

            if (held > admitted)
            {
                mismatch = $"held {held} B > admitted {admitted} B (waiting {budget.Waiting})";
            }
        }

        internal static int Mismatches => _mismatches;

        internal static void ResetForTests()
        {
            _mismatches = _frames = _framesWaiting = _framesOverCap = 0;
            _peakHeld = _peakAdmitted = 0;
            _lastSummary = 0;
        }
    }
}
#endif
