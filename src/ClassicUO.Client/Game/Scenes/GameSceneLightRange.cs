// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // CUO_LIGHT_RANGE_CHECK (LightRange): light passes checked, lights kept, lights past the range (dropped with the switch
        // on, counted as would-be-dropped with it off: AddLightCore), lights in the range fade, budget-bound passes, lights
        // refused past the budget and faded by rank, mismatches
        private long _lrFrames, _lrKept, _lrDropped, _lrRangeFaded, _lrBudgetFrames, _lrRefused, _lrBudgetFaded, _lrMismatches;
        private int _lrMaxKept;
        private uint _lrSummaryAt;

        // the frame's kept lights against the rule: inside the range, share = range share (x a budget share for the lowest
        // ranks when the budget binds), the budget kept the best ranks, the budget-faded lights are the lowest ranked
        private void LightRangeCheck(int count)
        {
            if (!LightRange.Check || !_vmLights)
            {
                return;
            }

            _lrFrames++;
            _lrKept += count;
            _lrMaxKept = System.Math.Max(_lrMaxKept, count);

            if (!_lightRange || count == 0)
            {
                return;
            }

            bool bound = _frameLights.Refused > 0;
            long maxKept = long.MinValue, maxUnfaded = long.MinValue, minFaded = long.MaxValue;
            int budgetFaded = 0;

            for (int i = 0; i < count; i++)
            {
                ref FrameLights.Entry l = ref _frameLights[i];
                float d = LightRange.TileDistance(l.DrawX, l.DrawY, _lvPx, _lvPy);
                float rs = LightRange.RangeShare(d, _lvRange);
                long p = _frameLights.PriorityAt(i);
                maxKept = System.Math.Max(maxKept, p);

                if (rs < 1f)
                {
                    _lrRangeFaded++;
                }

                if (!LightRange.InRange(d, _lvRange) || l.Share <= 0f || l.Share > rs * 1.0001f + 1e-6f)
                {
                    RangeMismatch(ref l, $"kept at {d:F2} tiles (range {_lvRange}) with share {l.Share:F4}, range share {rs:F4}");

                    continue;
                }

                if (l.Share < rs * 0.999f)
                {
                    budgetFaded++;
                    minFaded = System.Math.Min(minFaded, p);
                }
                else
                {
                    maxUnfaded = System.Math.Max(maxUnfaded, p);
                }
            }

            if (bound)
            {
                _lrBudgetFrames++;
                _lrRefused += _frameLights.Refused;
                _lrBudgetFaded += budgetFaded;
            }

            // the top of the fading ranks keeps factor 1, so ranks - 1 lights are dimmed when the budget binds
            int expected = bound ? System.Math.Max(System.Math.Min(LightRange.FadeRanks, count) - 1, 0) : 0;

            if (budgetFaded != expected || budgetFaded > 0 && minFaded < maxUnfaded || bound && maxKept > _frameLights.MinRefusedPriority)
            {
                _lrMismatches++;

                if (_lrMismatches <= 20)
                {
                    Log.Warn($"[light-range-check] budget {_frameLights.Limit}: {count} kept, {_frameLights.Refused} refused, {budgetFaded} faded by rank " +
                             $"(expected {expected}); lowest faded rank {minFaded}, highest unfaded {maxUnfaded}; worst kept {maxKept}, best refused {_frameLights.MinRefusedPriority}");
                }
            }
        }

        private void RangeMismatch(ref FrameLights.Entry l, string why)
        {
            _lrMismatches++;

            if (_lrMismatches <= 20)
            {
                Log.Warn($"[light-range-check] light {l.ID} from {l.SrcX},{l.SrcY},{l.SrcZ} at {l.DrawX},{l.DrawY}: {why}");
            }
        }

        // once per light pass with the checker on
        private void LightRangeSummary()
        {
            if (!LightRange.Check || Time.Ticks < _lrSummaryAt)
            {
                return;
            }

            _lrSummaryAt = Time.Ticks + 5000;
            double frames = System.Math.Max(_lrFrames, 1);
            int setting = Settings.GlobalSettings?.LightRangeTiles ?? 0;
            Log.Info($"[light-range-check] light_range {(_lightRange ? "on" : "off")} (chunk_lights {(_vmLights ? "on" : "off")}): range {_lvRange} tiles " +
                     $"({(setting > 0 ? "set" : "auto")}), budget {LightRange.Budget(Settings.GlobalSettings?.LightBudget ?? 0)}: {_lrFrames} light passes, " +
                     $"lights kept {_lrKept / frames:F1} per pass (max {_lrMaxKept}), {_lrDropped / frames:F1} per pass past the range " +
                     $"({(_lightRange ? "dropped" : "would be dropped")}), {_lrRangeFaded / frames:F1} in the range fade; budget bound in {_lrBudgetFrames} passes " +
                     $"({_lrRefused} refused, {_lrBudgetFaded} faded by rank); mismatches {_lrMismatches}");
        }
    }
}
#endif
