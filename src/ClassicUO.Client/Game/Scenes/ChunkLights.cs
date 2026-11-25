// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// chunk_lights (light-edges round 6c; always on since quiet-1123): static and multi lights come from the chunks'
    /// emitter lists (Chunk.LightSources, LightEmitters), not from the world walk, so every drawn emitter is lit whatever
    /// branch the walk took (the gradient circle of transparency's branch added none: the bank windows went dark) and the
    /// emitters beside the view whose halo reaches into it are lit before their source enters it. The frame's lights go into
    /// a growable ranked list (FrameLights: in view first, then nearest the player, bound FrameLights.SafetyBound) instead of
    /// the classic first-100, and a light whose halo cannot reach the view takes no slot (ViewMargin.LightReaches).
    /// view_margin is only the sprite margin.
    /// </summary>
    internal static class ChunkLights
    {
        /// <summary>The chunk emitter pass lights an emitter the walk drew this frame (DrawnFrame, set by every walk branch
        /// that draws it, the gradient circle's PushToRenderQueue included) and that still exists.</summary>
        public static bool LightsDrawn(uint drawnFrame, uint frame, bool destroyed) => drawnFrame == frame && !destroyed;

        internal static bool EdgeVisible(int z, int priorityZ, bool internalTile, bool roof, bool noRoofs, bool allowed,
            int maxZ, int ceiling, int height = 0) =>
            !internalTile && z < maxZ && !(noRoofs && roof) && priorityZ + (allowed ? height : 0) <= ceiling;
    }
}
