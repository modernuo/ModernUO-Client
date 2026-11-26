// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using ClassicUO.Utility.Logging;
#endif

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
#if CUO_DEVTOOLS
        private bool _chunkLightShadow;
        private FrameLights _chunkLightReference;
        private long _clChecks, _clDifferent;
        private uint _clLogAt;
#endif

        private void CollectChunkLights()
        {
            bool cached = ChunkLightCache.Enabled;
#if CUO_DEVTOOLS
            if (ChunkLightCache.Check)
            {
                _chunkLightReference ??= new FrameLights(FrameLights.SafetyBound);
                _frameLights.CopyTo(_chunkLightReference);
                FrameLights live = _frameLights;
                _frameLights = _chunkLightReference;
                _chunkLightShadow = true;
                try
                {
                    AddChunkLights(!cached, false);
                    if (_lightRange && _frameLights.Refused > 0 && _frameLights.Count > 0)
                        _frameLights.FadeLowest(LightRange.FadeRanks);
                }
                finally
                {
                    _frameLights = live;
                    _chunkLightShadow = false;
                }
            }
#endif
            AddChunkLights(cached);
        }

#if CUO_DEVTOOLS
        private void CheckChunkLightCache()
        {
            if (!ChunkLightCache.Check) return;
            _clChecks++;
            int diff = _frameLights.FirstDifference(_chunkLightReference);
            if (diff != -1)
            {
                _clDifferent++;
                Log.Warn($"[chunk-light-cache-check] mismatch frame {_drawFrame} index {diff} switch {ChunkLightCache.Enabled} " +
                    $"count {_frameLights.Count}/{_chunkLightReference.Count} refused {_frameLights.Refused}/{_chunkLightReference.Refused}");
                if (diff >= 0)
                {
                    ref var a = ref _frameLights[diff];
                    ref var b = ref _chunkLightReference[diff];
                    Log.Warn($"[chunk-light-cache-check] source {a.Source}/{b.Source} life {a.SourceLife}/{b.SourceLife} " +
                        $"id {a.ID}/{b.ID} xy {a.DrawX},{a.DrawY}/{b.DrawX},{b.DrawY} xyz {a.SrcX},{a.SrcY},{a.SrcZ}/{b.SrcX},{b.SrcY},{b.SrcZ} " +
                        $"color {a.Color}/{b.Color} hue {a.IsHue}/{b.IsHue} shape {a.ShapeWidth}x{a.ShapeHeight}/{b.ShapeWidth}x{b.ShapeHeight} " +
                        $"priority {_frameLights.PriorityAt(diff)}/{_chunkLightReference.PriorityAt(diff)} share {a.Share}/{b.Share}");
                }
            }
            if (Time.Ticks >= _clLogAt)
            {
                _clLogAt = Time.Ticks + 5000;
                Log.Info($"[chunk-light-cache-check] switch {ChunkLightCache.Enabled} checked {_clChecks} mismatches {_clDifferent}");
            }
        }
#endif
    }
}
