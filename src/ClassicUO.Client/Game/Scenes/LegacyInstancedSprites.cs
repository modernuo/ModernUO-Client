// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// legacy_instanced_sprites (default off). The Legacy renderer's world sprites (the render lists of
    /// <see cref="RenderLists"/>) go up as 44-byte <c>LegacySpriteInstance</c>s that the vertex shader expands into the quad the
    /// old path builds on the CPU, instead of 192-byte quads; the world pass includes its own text and gumps, and the UI pass stays on quads. Calls the
    /// instance cannot reproduce exactly (rotation, origin, geo mode, a capture) keep the quad path. No device instancing: quads.
    /// CUO_LEGACY_INSTANCED_CHECK=1: every call routed to the instance path also builds the old quad, expands the instance and
    /// compares the bytes (at most 20 mismatch lines logged); a summary every 5 s. CUO_LEGACY_INSTANCED=1|0 overrides the setting
    /// in developer builds.
    /// </summary>
    internal static class LegacyInstancedSprites
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LEGACY_INSTANCED") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LEGACY_INSTANCED_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LegacyInstancedSprites ?? false);

        /// <summary>What the batcher is asked for: instances only inside the world lists, quads everywhere else.</summary>
        public static bool RequestFor(bool inWorldLists) => inWorldLists && Enabled;

        /// <summary>Per-frame average of a counter's growth over a window; 0 for an empty window.</summary>
        internal static double PerFrame(long delta, long frames) => frames > 0 ? (double)delta / frames : 0d;

#if CUO_DEVTOOLS
        private static long _summaryAt;

        // hooks the batcher's checker to the log on the class's first use (the first world draw)
        static LegacyInstancedSprites()
        {
            if (!Check)
            {
                return;
            }

            UltimaBatcher2D.InstanceCheck = true;
            UltimaBatcher2D.InstanceMismatchLog = Log.Warn;
        }

        private static long _frames, _lastFrames, _lastInst, _lastQuad, _lastSwitches;

        /// <summary>Once per world frame with the checker on: the 5 s summary, with per-frame averages over the window taken from
        /// the checker's own cumulative counters (they do not depend on FrameStats).</summary>
        public static void Summary()
        {
            if (!Check)
            {
                return;
            }

            _frames++;

            if (Time.Ticks < _summaryAt)
            {
                return;
            }

            _summaryAt = Time.Ticks + 5000;
            long frames = _frames - _lastFrames;
            long inst = UltimaBatcher2D.InstanceChecked, quad = UltimaBatcher2D.CheckQuadSprites, sw = UltimaBatcher2D.CheckPathSwitches;
            Log.Info($"[legacy-instanced-check] legacy_instanced_sprites {(Enabled ? "on" : "off")}, {UltimaBatcher2D.InstanceChecked} checked, " +
                     $"{UltimaBatcher2D.InstanceMismatches} mismatches, per frame over {frames} frames: " +
                     $"{PerFrame(inst - _lastInst, frames):F1} instanced / {PerFrame(quad - _lastQuad, frames):F1} quad sprites, " +
                     $"{PerFrame(sw - _lastSwitches, frames):F2} path switches");
            _lastFrames = _frames;
            _lastInst = inst;
            _lastQuad = quad;
            _lastSwitches = sw;
        }
#endif
    }
}
