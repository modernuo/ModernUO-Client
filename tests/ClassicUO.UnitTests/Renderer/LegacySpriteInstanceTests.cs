using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // The instance path must rebuild, bit for bit, the quad UltimaBatcher2D.BuildQuad writes; Expand is the CPU twin of the
    // vertex shader that does it on the GPU.
    public class LegacySpriteInstanceTests
    {
        private static (float x, float y, float w, float h) Uvs(int x, int y, int w, int h, int texW, int texH)
        {
            float invW = 1f / texW;
            float invH = 1f / texH;

            return (x * invW, y * invH, w * invW, h * invH);
        }

        public static IEnumerable<object[]> Cases()
        {
            var data = new List<object[]>();
            var dests = new[] { (0f, 0f, 1f, 1f), (-37.5f, 812.25f, 44f, 93f), (1919f, 1079f, 1f, 1f) };
            var sources = new[] { (0, 0, 1, 1, 2048, 2048), (123, 457, 44, 93, 2048, 2048), (4001, 3999, 7, 33, 4096, 4096), (5, 9, 1, 63, 4096, 2048) };
            var hues = new[] { (0f, 0f, 1f), (2999f, 1f, 1f), (5f, 2f, 0.5f), (0f, 7f, 1.5f), (0f, 10f, 0f) };

            foreach (var d in dests)
            {
                foreach (var s in sources)
                {
                    foreach (var h in hues)
                    {
                        for (byte e = 0; e < 4; e++)
                        {
                            float depth = e == 0 ? 0f : e == 1 ? 0.5f : 1234.5f;
                            data.Add(new object[] { d.Item1, d.Item2, d.Item3, d.Item4, s.Item1, s.Item2, s.Item3, s.Item4, s.Item5, s.Item6, e, depth, h.Item1, h.Item2, h.Item3 });
                        }
                    }
                }
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ExpandEqualsBuildQuad_Bitwise(float dx, float dy, float dw, float dh, int sx, int sy, int sw, int sh, int texW, int texH, byte effects, float depth, float hueX, float hueY, float hueZ)
        {
            var (ux, uy, uw, uh) = Uvs(sx, sy, sw, sh, texW, texH);
            var hue = new Vector3(hueX, hueY, hueZ);

            var expected = new UltimaBatcher2D.PositionNormalTextureColor4();
            UltimaBatcher2D.BuildQuad(ref expected, ux, uy, uw, uh, dx, dy, dw, dh, hue, 0, 0, 0f, 1f, depth, effects);

            LegacySpriteInstance.TryFromSprite(ux, uy, uw, uh, dx, dy, dw, dh, hue, 0, 0, 0f, 1f, depth, effects, out var instance).Should().BeTrue();
            var actual = new UltimaBatcher2D.PositionNormalTextureColor4();
            LegacySpriteInstance.Expand(in instance, ref actual);

            MemoryMarshal.AsBytes(new ReadOnlySpan<UltimaBatcher2D.PositionNormalTextureColor4>(in actual)).SequenceEqual(
                MemoryMarshal.AsBytes(new ReadOnlySpan<UltimaBatcher2D.PositionNormalTextureColor4>(in expected))).Should().BeTrue();
        }

        [Fact]
        public void RejectsRotationAndOrigin()
        {
            var hue = new Vector3(0, 0, 1);

            LegacySpriteInstance.TryFromSprite(0, 0, 0.1f, 0.1f, 1, 2, 3, 4, hue, 0, 0, 0.5f, 0.8660254f, 0, 0, out _).Should().BeFalse();
            LegacySpriteInstance.TryFromSprite(0, 0, 0.1f, 0.1f, 1, 2, 3, 4, hue, 0.5f, 0, 0, 1, 0, 0, out _).Should().BeFalse();
            LegacySpriteInstance.TryFromSprite(0, 0, 0.1f, 0.1f, 1, 2, 3, 4, hue, 0, 0.5f, 0, 1, 0, 0, out _).Should().BeFalse();
        }

        [Theory]
        [InlineData(-1f, 0f, 1f)]
        [InlineData(0.5f, 0f, 1f)]
        [InlineData(65536f, 0f, 1f)]
        [InlineData(0f, 256f, 1f)]
        [InlineData(0f, -1f, 1f)]
        [InlineData(0f, 1.5f, 1f)]
        [InlineData(float.NaN, 0f, 1f)]
        public void RejectsUnpackableHue(float x, float y, float z)
        {
            LegacySpriteInstance.TryFromSprite(0, 0, 0.1f, 0.1f, 1, 2, 3, 4, new Vector3(x, y, z), 0, 0, 0, 1, 0, 0, out _).Should().BeFalse();
        }

        [Theory]
        [InlineData(-0f, 2f, 3f, 4f)]
        [InlineData(1f, -0f, 3f, 4f)]
        [InlineData(1f, 2f, 0f, 4f)]
        [InlineData(1f, 2f, 3f, -4f)]
        [InlineData(float.NaN, 2f, 3f, 4f)]
        [InlineData(1f, 2f, float.PositiveInfinity, 4f)]
        public void RejectsDestinationsTheShaderCouldNotMatch(float dx, float dy, float dw, float dh)
        {
            LegacySpriteInstance.TryFromSprite(0, 0, 0.1f, 0.1f, dx, dy, dw, dh, new Vector3(0, 0, 1), 0, 0, 0, 1, 0, 0, out _).Should().BeFalse();
        }

        [Fact]
        public void SizeIsAtMost48()
        {
            LegacySpriteInstance.SIZE_IN_BYTES.Should().BeLessThanOrEqualTo(48);
            LegacySpriteInstance.SIZE_IN_BYTES.Should().Be(Marshal.SizeOf<LegacySpriteInstance>());
            LegacySpriteInstance.VertexDeclaration.VertexStride.Should().Be(LegacySpriteInstance.SIZE_IN_BYTES);
        }
    }
}
