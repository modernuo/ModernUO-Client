using System;
using System.Runtime.CompilerServices;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework.Graphics;
using Xunit;
using PathKind = ClassicUO.Renderer.UltimaBatcher2D.PathKind;

namespace ClassicUO.UnitTests.Renderer
{
    // The instanced sprite path's queue decisions, without a device: when a path change must flush (submission order), how
    // an instance run splits on texture changes (the quad path's draw calls), and which calls may take the instance path.
    public class BatcherInstanceQueueTests
    {
        private static Texture2D FakeTexture() => (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));

        [Fact]
        public void PathSwitchFlushes()
        {
            UltimaBatcher2D.NeedsFlushForPath(PathKind.Quad, PathKind.Instance).Should().BeTrue();
            UltimaBatcher2D.NeedsFlushForPath(PathKind.Instance, PathKind.Quad).Should().BeTrue();
            UltimaBatcher2D.NeedsFlushForPath(PathKind.Quad, PathKind.Quad).Should().BeFalse();
            UltimaBatcher2D.NeedsFlushForPath(PathKind.Instance, PathKind.Instance).Should().BeFalse();
            UltimaBatcher2D.NeedsFlushForPath(PathKind.None, PathKind.Quad).Should().BeFalse();
            UltimaBatcher2D.NeedsFlushForPath(PathKind.None, PathKind.Instance).Should().BeFalse();
        }

        [Fact]
        public void PendingPathFollowsTheQueues()
        {
            UltimaBatcher2D.PendingPath(0, 0).Should().Be(PathKind.None);
            UltimaBatcher2D.PendingPath(3, 0).Should().Be(PathKind.Quad);
            UltimaBatcher2D.PendingPath(0, 2).Should().Be(PathKind.Instance);
        }

        [Fact]
        public void RunsSplitOnTextureChange()
        {
            Texture2D a = FakeTexture();
            Texture2D b = FakeTexture();

            UltimaBatcher2D.CountRuns(new[] { a, a, b, a }).Should().Be(3);
            UltimaBatcher2D.CountRuns(new[] { a }).Should().Be(1);
            UltimaBatcher2D.CountRuns(ReadOnlySpan<Texture2D>.Empty).Should().Be(0);
            UltimaBatcher2D.CountRuns(new[] { a, a, a }).Should().Be(1);
            UltimaBatcher2D.CountRuns(new[] { a, b, a, b }).Should().Be(4);
        }

        [Fact]
        public void RunEndsAtTheNextTexture()
        {
            Texture2D a = FakeTexture();
            Texture2D b = FakeTexture();
            Texture2D[] textures = { a, a, b, a };

            UltimaBatcher2D.RunEnd(textures, 0).Should().Be(2);
            UltimaBatcher2D.RunEnd(textures, 2).Should().Be(3);
            UltimaBatcher2D.RunEnd(textures, 3).Should().Be(4);
        }

        [Fact]
        public void NoInstancingMeansQuadOnly()
        {
            for (int bits = 0; bits < 64; bits++)
            {
                UltimaBatcher2D.ChoosePath
                (
                    requested: (bits & 1) != 0,
                    supported: false,
                    geo: (bits & 2) != 0,
                    customEffect: (bits & 4) != 0,
                    recording: (bits & 8) != 0,
                    suppressed: (bits & 16) != 0,
                    capture: (bits & 32) != 0
                ).Should().Be(PathKind.Quad);
            }
        }

        // fxc folds the shader's (x, y, depth, 1) into depth * (0, 0, 1, 0) + (0, 0, 0, 1): -0 and non-finite depths would not
        // reach the transform as the quad path's vertex does
        [Theory]
        [InlineData(-0f, false)]
        [InlineData(float.NaN, false)]
        [InlineData(float.PositiveInfinity, false)]
        [InlineData(float.NegativeInfinity, false)]
        [InlineData(0f, true)]
        [InlineData(-3.5f, true)]
        public void DepthTheShaderCannotCarryStaysQuad(float depth, bool accepted)
        {
            LegacySpriteInstance.TryFromSprite(0f, 0f, 1f, 1f, 10f, 20f, 44f, 44f, new Microsoft.Xna.Framework.Vector3(0f, 0f, 1f), 0f, 0f, 0f, 1f, depth, 0, out _)
                .Should().Be(accepted);
            LegacySpriteInstance.TryFromShadow(new Microsoft.Xna.Framework.Vector2(10f, 20f), new Microsoft.Xna.Framework.Rectangle(0, 0, 44, 44), 64, 64, false, depth, out _)
                .Should().Be(accepted);
        }

        [Fact]
        public void InstancePathOnlyWhenRequestedSupportedAndPlain()
        {
            UltimaBatcher2D.ChoosePath(true, true, false, false, false, false, false).Should().Be(PathKind.Instance);
            UltimaBatcher2D.ChoosePath(false, true, false, false, false, false, false).Should().Be(PathKind.Quad);
            UltimaBatcher2D.ChoosePath(true, true, true, false, false, false, false).Should().Be(PathKind.Quad);
            UltimaBatcher2D.ChoosePath(true, true, false, true, false, false, false).Should().Be(PathKind.Quad);
            UltimaBatcher2D.ChoosePath(true, true, false, false, true, false, false).Should().Be(PathKind.Quad);
            UltimaBatcher2D.ChoosePath(true, true, false, false, false, true, false).Should().Be(PathKind.Quad);
            UltimaBatcher2D.ChoosePath(true, true, false, false, false, false, true).Should().Be(PathKind.Quad);
        }
    }
}
