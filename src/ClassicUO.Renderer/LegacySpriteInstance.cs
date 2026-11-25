using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// One axis-aligned sprite as 44 bytes instead of the 192-byte quad. <see cref="Expand"/> rebuilds the quad
    /// <see cref="UltimaBatcher2D.BuildQuad"/> writes, bit for bit, for every call <see cref="TryFromSprite"/> accepts.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct LegacySpriteInstance : IVertexType
    {
        public float SourceX;
        public float SourceY;
        public float SourceW;
        public float SourceH;

        public float DestinationX;
        public float DestinationY;
        public float DestinationW;
        public float DestinationH;

        public float Depth;
        public float HueAlpha;

        // Byte4 on the GPU, unnormalised: hue index = byte 0 + byte 1 * 256, then the hue mode, then the flip bits.
        public byte HueIndexLow;
        public byte HueIndexHigh;
        public byte HueMode;
        public byte Effects;

        public const int SIZE_IN_BYTES = sizeof(float) * 10 + sizeof(byte) * 4;

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

        // Usage indices 2 and up: the shader's quad input already uses TEXCOORD0 and TEXCOORD1.
        public static readonly VertexDeclaration VertexDeclaration = new VertexDeclaration
        (
            SIZE_IN_BYTES,
            new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(32, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(40, VertexElementFormat.Byte4, VertexElementUsage.TextureCoordinate, 5)
        );

        /// <summary>
        /// False when the quad cannot be reproduced exactly; the caller keeps the quad path.
        /// </summary>
        public static bool TryFromSprite
        (
            float sourceX,
            float sourceY,
            float sourceW,
            float sourceH,
            float destinationX,
            float destinationY,
            float destinationW,
            float destinationH,
            Vector3 color,
            float originX,
            float originY,
            float rotationSin,
            float rotationCos,
            float depth,
            byte effects,
            out LegacySpriteInstance instance
        )
        {
            instance = default;

            if (rotationSin != 0f || rotationCos != 1f || originX != 0f || originY != 0f || effects > 3)
            {
                return false;
            }

            // BuildQuad computes a corner as ((-0 * y) + (1 * x)) + destination; Expand computes (offset * size) + destination.
            // They agree for finite destinations with a positive size, except where a signed zero differs: the left/top
            // corner would turn a destination of -0 into +0 (the right/bottom corners are unaffected).
            if (!float.IsFinite(destinationX) || !float.IsFinite(destinationY) || !(destinationW > 0f) || !(destinationH > 0f)
                || !float.IsFinite(destinationW) || !float.IsFinite(destinationH)
                || BitConverter.SingleToInt32Bits(destinationX) == int.MinValue
                || BitConverter.SingleToInt32Bits(destinationY) == int.MinValue)
            {
                return false;
            }

            if (!TryPack(color.X, ushort.MaxValue, out float hueIndex) || !TryPack(color.Y, byte.MaxValue, out float hueMode))
            {
                return false;
            }

            instance.SourceX = sourceX;
            instance.SourceY = sourceY;
            instance.SourceW = sourceW;
            instance.SourceH = sourceH;
            instance.DestinationX = destinationX;
            instance.DestinationY = destinationY;
            instance.DestinationW = destinationW;
            instance.DestinationH = destinationH;
            instance.Depth = depth;
            instance.HueAlpha = color.Z;
            instance.HueIndexLow = (byte)((int)hueIndex & 0xFF);
            instance.HueIndexHigh = (byte)((int)hueIndex >> 8);
            instance.HueMode = (byte)hueMode;
            instance.Effects = effects;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryPack(float value, int max, out float packed)
        {
            packed = value;

            return value >= 0f && value <= max && value == (float)(int)value && BitConverter.SingleToInt32Bits(value) != int.MinValue;
        }

        /// <summary>
        /// The CPU twin of the vertex shader (VertexShaderInstanced in IsometricWorld.fx, Task 3), which mirrors this
        /// line for line with float math. The corner offsets come from the per-vertex corner stream; a flip bit mirrors a UV offset as abs(offset - flip).
        /// </summary>
        public static void Expand(in LegacySpriteInstance instance, ref UltimaBatcher2D.PositionNormalTextureColor4 quad)
        {
            float hueIndex = instance.HueIndexLow + (instance.HueIndexHigh * 256f);
            var hue = new Vector3(hueIndex, instance.HueMode, instance.HueAlpha);

            float effects = instance.Effects;
            float flipY = MathF.Floor(effects / 2f);
            float flipX = effects - (flipY * 2f);

            quad.Position0 = new Vector3((0f * instance.DestinationW) + instance.DestinationX, (0f * instance.DestinationH) + instance.DestinationY, instance.Depth);
            quad.Position1 = new Vector3((1f * instance.DestinationW) + instance.DestinationX, (0f * instance.DestinationH) + instance.DestinationY, instance.Depth);
            quad.Position2 = new Vector3((0f * instance.DestinationW) + instance.DestinationX, (1f * instance.DestinationH) + instance.DestinationY, instance.Depth);
            quad.Position3 = new Vector3((1f * instance.DestinationW) + instance.DestinationX, (1f * instance.DestinationH) + instance.DestinationY, instance.Depth);

            quad.TextureCoordinate0 = Uv(in instance, 0f, 0f, flipX, flipY);
            quad.TextureCoordinate1 = Uv(in instance, 1f, 0f, flipX, flipY);
            quad.TextureCoordinate2 = Uv(in instance, 0f, 1f, flipX, flipY);
            quad.TextureCoordinate3 = Uv(in instance, 1f, 1f, flipX, flipY);

            quad.Hue0 = hue;
            quad.Hue1 = hue;
            quad.Hue2 = hue;
            quad.Hue3 = hue;

            quad.Normal0 = new Vector3(0, 0, 1);
            quad.Normal1 = new Vector3(0, 0, 1);
            quad.Normal2 = new Vector3(0, 0, 1);
            quad.Normal3 = new Vector3(0, 0, 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 Uv(in LegacySpriteInstance instance, float cornerX, float cornerY, float flipX, float flipY)
        {
            float offsetX = MathF.Abs(cornerX - flipX);
            float offsetY = MathF.Abs(cornerY - flipY);

            return new Vector3((offsetX * instance.SourceW) + instance.SourceX, (offsetY * instance.SourceH) + instance.SourceY, 0f);
        }
    }
}
