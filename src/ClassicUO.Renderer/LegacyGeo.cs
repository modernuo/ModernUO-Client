// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// clip-4: the Legacy world's geometric depth (switch <c>depth_model</c>; IsometricWorld.fx HueGeoTechnique, the same maths
    /// as the Gpu's sprite.hlsl GeoDepth and Uo.Render.GeoDepth). A sprite's shape class and parameters ride in vertex fields
    /// the GEO technique reads: TexCoord.z = class · 64 + 32 + (base − key), with base the absolute tile sum of the class's
    /// reference point with its biases and key the vertex's depth (Position.z), and Normal.xy = the reference column and row in
    /// the vertex's own coordinates (world pixels for the chunk mesh, screen pixels for the batcher). The shader subtracts the
    /// frame's integer <see cref="CamSum"/> from the key before adding (base − key), so the sums stay small and exact in fp32
    /// (absolute sums near 10 000 resolve only ~1e-3; ZBias is 2e-3). TexCoord.z 0 is the depth key (every writer's default).
    /// Shadows keep the key (as on the Gpu).
    /// </summary>
    public static class LegacyGeo
    {
        /// <summary>Shape classes (IsometricWorld.fx GeoSum): per-vertex linear (land corners), box, wall, flat (its base carries
        /// <see cref="FlatDrop"/>; floored at its tile's back edges, Uo.Render.GeoDepth.FlatFloored), mobile composite, and a wet
        /// flat (wet_surface_depth: its base carries GeoDepth.WetDrop, no floor).</summary>
        public const int Linear = 1, Box = 2, Wall = 3, Flat = 4, Mobile = 5, FlatWet = 6;

        /// <summary>Uo.Render.GeoDepth.FlatDrop (IsometricWorld.fx GEO_FLAT_DROP): the drop in a <see cref="Flat"/>'s base, which its
        /// floor adds back.</summary>
        public const float FlatDrop = 1.5f;

        /// <summary>TexCoord.z = class · ClassStride + LocalOffset + local; |local| is clamped to LocalMax.</summary>
        public const float ClassStride = 64f, LocalOffset = 32f, LocalMax = 30f;

        /// <summary>The depth key's tile sum relative to its key (IsometricWorld.fx GEO_KEY): the Gpu's key fallback,
        /// (D − Dcam) / 100 + 0.5 with D = 100 · key and the camera's D at its tile sum + 1.27.</summary>
        public const float KeyToSum = -0.77f;

        /// <summary>Depth per tile sum (IsometricWorld.fx GEO_SCALE, Uo.Render.WorldRenderer.GeoScale).</summary>
        public const float Scale = 1f / 2500f;

        /// <summary>TexCoord.z of class <paramref name="cls"/> with base <paramref name="baseSum"/> on a vertex at key
        /// <paramref name="key"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Encode(int cls, double baseSum, float key)
        {
            float local = (float)(baseSum - key);

            return cls * ClassStride + LocalOffset + Math.Clamp(local, -LocalMax, LocalMax);
        }

        /// <summary>Gives the quad class <paramref name="cls"/> with reference (<paramref name="col"/>, <paramref name="row"/>)
        /// and base <paramref name="baseSum"/>; a shadow quad keeps the key.</summary>
        public static void Apply(ref PositionNormalTextureColor4 v, int cls, float col, float row, double baseSum)
        {
            if (v.Hue0.Y == ShaderHueTranslator.SHADER_SHADOW)
            {
                return;
            }

            v.TextureCoordinate0.Z = Encode(cls, baseSum, v.Position0.Z);
            v.TextureCoordinate1.Z = Encode(cls, baseSum, v.Position1.Z);
            v.TextureCoordinate2.Z = Encode(cls, baseSum, v.Position2.Z);
            v.TextureCoordinate3.Z = Encode(cls, baseSum, v.Position3.Z);
            v.Normal0.X = v.Normal1.X = v.Normal2.X = v.Normal3.X = col;
            v.Normal0.Y = v.Normal1.Y = v.Normal2.Y = v.Normal3.Y = row;
            v.Normal0.Z = v.Normal1.Z = v.Normal2.Z = v.Normal3.Z = 0f;
        }

        /// <summary>The linear class: each vertex's own base (land corners); the normals (stretched land's lighting) stay.</summary>
        public static void ApplyLinear(ref PositionNormalTextureColor4 v, double b0, double b1, double b2, double b3)
        {
            v.TextureCoordinate0.Z = Encode(Linear, b0, v.Position0.Z);
            v.TextureCoordinate1.Z = Encode(Linear, b1, v.Position1.Z);
            v.TextureCoordinate2.Z = Encode(Linear, b2, v.Position2.Z);
            v.TextureCoordinate3.Z = Encode(Linear, b3, v.Position3.Z);
        }

        /// <summary>The integer tile sum a frame's depths are relative to: the one at world pixel row
        /// <paramref name="worldRow"/> (the view's centre); ±1250 sums around it fit the depth range.</summary>
        public static int CamSum(int worldRow) => (int)MathF.Floor((worldRow + 22) / 22f);

        /// <summary>
        /// IsometricWorld.fx GeoSum (tests, checker): the tile sum relative to <paramref name="cam"/> of a pixel at
        /// (<paramref name="x"/>, <paramref name="y"/>) in a quad whose interpolated TexCoord.z is <paramref name="t"/>, key
        /// <paramref name="key"/> and reference (<paramref name="col"/>, <paramref name="row"/>).
        /// </summary>
        public static float PixelSum(float t, float key, float col, float row, float x, float y, int cam)
        {
            float k = key - cam;

            if (t < LocalOffset)
            {
                return k + KeyToSum;
            }

            float cls = MathF.Floor(t / ClassStride);
            float b = k + (t - cls * ClassStride - LocalOffset);
            float s = MathF.Abs(x - col) / 22f;
            float v = (y - row) / 22f;

            return (int)cls switch
            {
                Box => b + MathF.Max(s, MathF.Min(v, 1f)),
                Wall => b + s,
                Flat => b + MathF.Max(v, s + FlatDrop),
                FlatWet => b + v,
                Mobile => b + MathF.Max(0f, MathF.Min(v, MathF.Abs(1f - (s - 2f * MathF.Floor(s * 0.5f))))),
                _ => b
            };
        }

        /// <summary>The depth the GEO technique writes for a relative tile sum (bigger sum = nearer = smaller depth).</summary>
        public static float WindowDepth(float relSum) => Math.Clamp(0.5f - relSum * Scale, 0f, 1f);
    }
}
