// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // mesh_hue_on_change (Settings.MeshHueOnChange, read once per frame): Legacy meshed sprites recompute their hue only
        // when its inputs change (MeshHueEpoch, the object's hue, a mesh rebuild) instead of every frame
        private readonly MeshHueEpoch _meshHueEpoch = new MeshHueEpoch();
        private bool _meshHueOnChange;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_MESH_HUE=1|0) and the checker (CUO_MESH_HUE_CHECK=1):
        // every sprite that keeps its hue is compared with the hue the old per-frame path computes for it
        private static readonly bool? _meshHueEnv = Environment.GetEnvironmentVariable("CUO_MESH_HUE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        private static readonly bool _meshHueCheck = Environment.GetEnvironmentVariable("CUO_MESH_HUE_CHECK") == "1";
        private long _meshHueChecks, _meshHueMismatches, _meshHueReportAt;
        private int _meshHueLogged;
#endif

        private void BeginMeshHueFrame()
        {
            bool on = Settings.GlobalSettings.MeshHueOnChange;
#if CUO_DEVTOOLS
            on = _meshHueEnv ?? on;
#endif
            if (on != _meshHueOnChange)
            {
                _meshHueOnChange = on;
                _meshHueEpoch.Bump();
            }

            var profile = ProfileManager.CurrentProfile;
            _meshHueEpoch.Update(
                profile.NoColorObjectsOutOfRange,
                _world.RangeSize.X,
                _world.RangeSize.Y,
                _world.ClientViewRange,
                _world.Player.IsDead && profile.EnableBlackWhiteEffect
            );
        }

#if CUO_DEVTOOLS
        // out of line: the walk inlines ApplyMeshHue
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void CheckMeshHue(GameObject obj, MeshLayer layer, ushort objHue)
        {
            _meshHueChecks++;
            MeshHueOf(obj, objHue, out float hueX, out float hueY);
            layer.GetHue(obj.MeshSpriteIndex, out float keptX, out float keptY);

            if (hueX == keptX && hueY == keptY)
            {
                return;
            }

            _meshHueMismatches++;

            if (_meshHueLogged < 10)
            {
                _meshHueLogged++;
                Log.Warn(
                    $"[mesh-hue-check] {obj.GetType().Name} 0x{obj.Graphic:X4} hue 0x{objHue:X4} at {obj.X},{obj.Y},{obj.Z} " +
                    $"(sprite {obj.MeshSpriteIndex}): kept {keptX}/{keptY}, per-frame {hueX}/{hueY}; epoch {_meshHueEpoch.Epoch}, " +
                    $"distance {obj.Distance}, view range {_world.ClientViewRange}, dead {_world.Player.IsDead}"
                );
            }
        }

        private void ReportMeshHueCheck()
        {
            long now = Stopwatch.GetTimestamp();

            if (_meshHueReportAt == 0)
            {
                _meshHueReportAt = now;
            }
            else if (now - _meshHueReportAt >= Stopwatch.Frequency * 5)
            {
                // totals since start; the mismatch log restarts each period (at most 10 lines)
                Log.Info($"[mesh-hue-check] mesh_hue_on_change {_meshHueOnChange}: kept hues checked {_meshHueChecks}, mismatches {_meshHueMismatches}");
                _meshHueReportAt = now;
                _meshHueLogged = 0;
            }
        }
#endif
    }
}
