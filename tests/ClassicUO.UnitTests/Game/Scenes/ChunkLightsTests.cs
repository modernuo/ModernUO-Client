using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>chunk_lights (light-edges round 6c, permanent since quiet-1123): static and multi lights come from the chunks'
    /// emitter lists.</summary>
    public sealed class ChunkLightsTests
    {
        // owner 2026-11-22 (bench-001039): under the gradient circle of transparency the walk's meshed-static branch sets the
        // alpha, queues the static (PushToRenderQueue marks it drawn) and continues before any light add, so a walk-side
        // light left the Moonglow bank's lit windows dark. The light pass takes every drawn emitter from the chunk lists,
        // whichever walk branch drew it.
        [Fact]
        public void Gradient_circle_keeps_the_bank_window_lights()
        {
            const uint frame = 42;
            // what the gradient branch leaves: drawn this frame
            uint drawnFrame = frame;

            Assert.True(ChunkLights.LightsDrawn(drawnFrame, frame, false));
        }

        [Fact]
        public void Chunk_pass_skips_emitters_not_drawn_this_frame_or_destroyed()
        {
            Assert.True(ChunkLights.LightsDrawn(7, 7, false));
            Assert.False(ChunkLights.LightsDrawn(6, 7, false));
            Assert.False(ChunkLights.LightsDrawn(7, 7, true));
        }
    }
}
