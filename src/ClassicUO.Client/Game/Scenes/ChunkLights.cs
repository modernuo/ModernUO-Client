// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// chunk_lights (light-edges round 6c, default off): view_margin's light part as its own switch. Static and multi lights
    /// come from the chunks' emitter lists (Chunk.LightSources, LightEmitters), not from the world walk, so every drawn
    /// emitter is lit whatever branch the walk took (the gradient circle of transparency's branch added none: the bank windows
    /// went dark) and the emitters beside the view whose halo reaches into it are lit before their source enters it. The
    /// frame's lights go into a growable ranked list (FrameLights: in view first, then nearest the player, bound
    /// FrameLights.SafetyBound) instead of the classic first-100, and a light whose halo cannot reach the view takes no slot
    /// (ViewMargin.LightReaches). view_margin keeps only the sprite margin.
    /// CUO_CHUNK_LIGHTS_CHECK=1 compares the band lights with a walk of every tile list and dynamic of the same chunks, every
    /// frame, in both states; CUO_CHUNK_LIGHTS=1|0 overrides the setting in developer builds.
    /// </summary>
    internal static class ChunkLights
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_CHUNK_LIGHTS") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_CHUNK_LIGHTS_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.ChunkLights ?? false);

        /// <summary>The chunk emitter pass lights an emitter the walk drew this frame (DrawnFrame, set by every walk branch
        /// that draws it, the gradient circle's PushToRenderQueue included) and that still exists.</summary>
        public static bool LightsDrawn(uint drawnFrame, uint frame, bool destroyed) => drawnFrame == frame && !destroyed;
    }
}
