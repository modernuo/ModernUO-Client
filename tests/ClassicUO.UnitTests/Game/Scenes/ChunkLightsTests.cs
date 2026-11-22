using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>chunk_lights (light-edges round 6c): view_margin's light part as its own switch.</summary>
    public sealed class ChunkLightsTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            ChunkLights.OverrideForTests = on;

            try
            {
                Assert.Equal(on, ChunkLights.Enabled);
            }
            finally
            {
                ChunkLights.OverrideForTests = null;
            }
        }

        // owner 2026-11-22 (bench-001039): under the gradient circle of transparency the walk's meshed-static branch sets the
        // alpha, queues the static (PushToRenderQueue marks it drawn) and continues before its light add, so with the switch
        // off the Moonglow bank's lit windows got no light. With chunk_lights the light pass takes every drawn emitter from the
        // chunk lists, whichever walk branch drew it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Gradient_circle_keeps_the_bank_window_lights_with_chunk_lights(bool on)
        {
            const uint frame = 42;
            // what the gradient branch leaves: drawn this frame, no walk-side light
            uint drawnFrame = frame;
            bool walkAdded = false;

            bool chunkAdded = on && ChunkLights.LightsDrawn(drawnFrame, frame, false);
            // with the switch on the walk adds none either (no double halo: the torch flash)
            Assert.False(on && ViewMargin.WalkLightsStatic(true, on));

            Assert.Equal(on, walkAdded || chunkAdded);
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
