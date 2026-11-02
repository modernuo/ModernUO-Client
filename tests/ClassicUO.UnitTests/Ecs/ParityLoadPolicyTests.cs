// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game;
using Xunit;
using RendererAnimations = ClassicUO.Renderer.Animations.Animations;

namespace ClassicUO.UnitTests.Ecs
{
    // CUO_ECS_PARITY compares the view capture with the extract; a direction first seen in a scene Update (Mobile's frame
    // count, the advance system) before the first Gpu frame decoded on a worker and the view drew nothing for it while the
    // extract drew it (bench-000231: three 0xDC mobiles, capture=0 ecs=3). The synchronous load must be set at client load.
    public class ParityLoadPolicyTests
    {
        [Fact]
        public void ParityOn_LoadsAnimationsSynchronously()
        {
            bool saved = RendererAnimations.AsyncFirstSight;

            try
            {
                RendererAnimations.AsyncFirstSight = true;
                EcsParity.ConfigureLoaders("1");
                Assert.False(RendererAnimations.AsyncFirstSight);
            }
            finally
            {
                RendererAnimations.AsyncFirstSight = saved;
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("0")]
        public void ParityOff_KeepsAsyncFirstSight(string value)
        {
            bool saved = RendererAnimations.AsyncFirstSight;

            try
            {
                RendererAnimations.AsyncFirstSight = true;
                EcsParity.ConfigureLoaders(value);
                Assert.True(RendererAnimations.AsyncFirstSight);
            }
            finally
            {
                RendererAnimations.AsyncFirstSight = saved;
            }
        }
    }
}
