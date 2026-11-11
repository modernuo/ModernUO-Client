// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // mesh_vis_bits (Settings.MeshVisBits, read once per frame): Legacy chunk-mesh layers keep the frame's visibility as
        // bits (MeshLayer.ResetVisibility(packed, check))
        private bool _meshVisBits;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_MESH_VIS=1|0) and the checker (CUO_MESH_VIS_CHECK=1):
        // every layer's packed indices and runs are compared with the bool path's
        private static readonly bool? _meshVisEnv = Environment.GetEnvironmentVariable("CUO_MESH_VIS") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        private static readonly bool _meshVisCheck = Environment.GetEnvironmentVariable("CUO_MESH_VIS_CHECK") == "1";
        private long _meshVisReportAt;
#else
        private const bool _meshVisCheck = false;
#endif

        private void BeginMeshVisFrame()
        {
            bool on = Settings.GlobalSettings.MeshVisBits;
#if CUO_DEVTOOLS
            on = _meshVisEnv ?? on;
#endif
            _meshVisBits = on;
        }

#if CUO_DEVTOOLS
        private void ReportMeshVisCheck()
        {
            long now = Stopwatch.GetTimestamp();

            if (_meshVisReportAt == 0)
            {
                _meshVisReportAt = now;
            }
            else if (now - _meshVisReportAt >= Stopwatch.Frequency * 5)
            {
                // totals since start; the sample is the first mismatch of the period
                Log.Info($"[mesh-vis-check] mesh_vis_bits {_meshVisBits}: layers checked {MeshLayer.VisChecks}, mismatches {MeshLayer.VisMismatches}" +
                         (MeshLayer.VisSample != null ? $"; first: {MeshLayer.VisSample}" : ""));
                _meshVisReportAt = now;
                MeshLayer.VisSample = null;
            }
        }
#endif
    }
}
