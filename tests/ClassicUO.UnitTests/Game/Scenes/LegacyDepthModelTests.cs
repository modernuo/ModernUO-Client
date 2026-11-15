// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Uo.Render;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Vertex = ClassicUO.Renderer.UltimaBatcher2D.PositionNormalTextureColor4;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// clip-4: the Legacy port of the depth model (IsometricWorld.fx HueGeoTechnique, LegacyGeo, LegacyGeoDepth). The vertex
    /// encoding, decoded as the shader does (LegacyGeo.PixelSum), gives GeoDepth's values, so both renderers share the
    /// expectations of DepthModelTests (acceptance #1's geometry: Felucca 1453 1668 z 10, walls z 10 height 20).
    /// </summary>
    [Collection(nameof(DepthModelSwitchCollection))]
    public class LegacyDepthModelTests
    {
        private const int PX = 1453, PY = 1668, Z = 10;

        // a frame's draw offset and camera sum near the player (screen = world - offset)
        private const int OffX = -1800, OffY = 66700;
        private static readonly int Cam = LegacyGeo.CamSum(OffY + 300);

        private static Vertex Quad(float key, float x0 = 0, float y0 = 0, float x1 = 44, float y1 = 44)
        {
            var v = new Vertex();
            v.Position0 = new Microsoft.Xna.Framework.Vector3(x0, y0, key);
            v.Position1 = new Microsoft.Xna.Framework.Vector3(x1, y0, key);
            v.Position2 = new Microsoft.Xna.Framework.Vector3(x0, y1, key);
            v.Position3 = new Microsoft.Xna.Framework.Vector3(x1, y1, key);
            v.Normal0 = v.Normal1 = v.Normal2 = v.Normal3 = new Microsoft.Xna.Framework.Vector3(0, 0, 1);
            v.Hue0 = v.Hue1 = v.Hue2 = v.Hue3 = new Microsoft.Xna.Framework.Vector3(0, ShaderHueTranslator.SHADER_HUED, 1);

            return v;
        }

        // the shader's sum at world pixel (sx, sy) of a quad drawn with the frame offset (constant classes: vertex 0's values)
        private static float Decode(in Vertex v, float sx, float sy) =>
            LegacyGeo.PixelSum(v.TextureCoordinate0.Z, v.Position0.Z, v.Normal0.X, v.Normal0.Y, sx - OffX, sy - OffY, Cam);

        private static float StaticKey(int tx, int ty, int z) => tx + ty + (127 + z) * 0.01f + 0.5f;

        private static Vertex Tile(int tx, int ty, int z, int height, uint bits, bool buried)
        {
            Vertex v = Quad(StaticKey(tx, ty, z));
            LegacyGeoDepth.Tile(tx, ty, z, height, bits, buried, OffX, OffY, out int cls, out float col, out float row, out double b);
            LegacyGeo.Apply(ref v, cls, col, row, b);

            return v;
        }

        private static Vertex MobileQuad(int tx, int ty, int z, float key)
        {
            var (ax, ay) = GeoDepth.MobileAnchor(tx, ty, z);
            Vertex v = Quad(key);
            LegacyGeoDepth.Mobile(ax - OffX, ay - OffY, z, OffY, out float col, out float row, out double b);
            LegacyGeo.Apply(ref v, LegacyGeo.Mobile, col, row, b);

            return v;
        }

        [Theory]
        [InlineData(SpriteInstance.ZGeoWall, 20, false)]
        [InlineData(SpriteInstance.ZGeoWall, 20, true)]
        [InlineData(SpriteInstance.ZGeoFlat, 0, false)]
        [InlineData(0u, 6, false)]
        [InlineData(0u, 12, true)]
        [InlineData(0u, 0, false)]
        public void StaticShapesDecodeToGeoDepth(uint bits, int height, bool buried)
        {
            foreach (int z in new[] { -11, 0, 10, 40 })
            {
                Vertex v = Tile(PX, PY + 1, z, height, bits, buried);
                var (nx, ny) = GeoDepth.NorthVertex(PX, PY + 1, z);
                bool wall = bits == SpriteInstance.ZGeoWall, flat = bits == SpriteInstance.ZGeoFlat;

                for (int dx = -22; dx <= 22; dx += 3)
                {
                    for (int dy = -4 * height - 30; dy <= 50; dy += 7)
                    {
                        float sx = nx + dx, sy = ny + dy;
                        float expected = GeoDepth.Static(PX, PY + 1, z, height, wall, flat, buried, sx, sy) - Cam;
                        Decode(in v, sx, sy).Should().BeApproximately(expected, 1e-3f, $"z {z} at {dx},{dy}");
                    }
                }
            }
        }

        [Theory]
        [InlineData(0, 3.27f)] // the strip keys: depth + 1 + 2i
        [InlineData(10, 9.27f)]
        [InlineData(-5, 1.27f)]
        public void MobileDecodesToGeoDepth(int z, float keyOffset)
        {
            Vertex v = MobileQuad(PX, PY, z, PX + PY + keyOffset);
            var (ax, ay) = GeoDepth.MobileAnchor(PX, PY, z);

            for (int dx = -33; dx <= 33; dx += 3)
            {
                for (int dy = -90; dy <= 12; dy += 3)
                {
                    float expected = GeoDepth.MobileBiased(ax, ay, z, ax + dx, ay + dy) - Cam;
                    Decode(in v, ax + dx, ay + dy).Should().BeApproximately(expected, 1e-3f, $"at {dx},{dy}");
                }
            }
        }

        [Fact]
        public void LandDecodesToGeoDepth()
        {
            foreach (int z in new[] { -20, 0, 10 })
            {
                var v = Quad(StaticKey(PX, PY, z));
                LegacyGeoDepth.Land(PX, PY, z, OffX, OffY, out float col, out float row, out double b);
                LegacyGeo.Apply(ref v, LegacyGeo.Flat, col, row, b);
                var (nx, ny) = GeoDepth.NorthVertex(PX, PY, z);

                for (int dy = 0; dy <= 44; dy += 4)
                {
                    Decode(in v, nx, ny + dy).Should().BeApproximately(GeoDepth.Land(ny + dy, z) - Cam, 1e-3f);
                }
            }
        }

        // sprite.hlsl FLAG_STRETCH: each corner is a world point with an exact tile sum (top, right, left, bottom: + 0, 1, 1, 2)
        [Fact]
        public void StretchedLandCornersAndCentre()
        {
            const int z = 7;
            Vertex v = Quad(StaticKey(PX, PY, z));
            Microsoft.Xna.Framework.Vector3 normal = new(0.3f, 0.5f, 0.8f);
            v.Normal0 = v.Normal1 = v.Normal2 = v.Normal3 = normal;
            LegacyGeoDepth.ApplyLand(ref v, true, PX, PY, z, true);
            float key = v.Position0.Z;
            float top = PX + PY - GeoDepth.FlatDrop + (z - 2) * GeoDepth.ZBias - Cam;

            float Sum(float t) => LegacyGeo.PixelSum(t, key, 0, 0, 0, 0, Cam);

            Sum(v.TextureCoordinate0.Z).Should().BeApproximately(top, 1e-4f);
            Sum(v.TextureCoordinate1.Z).Should().BeApproximately(top + 1, 1e-4f);
            Sum(v.TextureCoordinate2.Z).Should().BeApproximately(top + 1, 1e-4f);
            Sum(v.TextureCoordinate3.Z).Should().BeApproximately(top + 2, 1e-4f);
            Sum((v.TextureCoordinate0.Z + v.TextureCoordinate3.Z) / 2).Should().BeApproximately(top + 1, 1e-4f, "interpolated, one class");
            v.Normal0.Should().Be(normal, "stretched land keeps its lighting normals");
        }

        // DepthModelTests.Acceptance1_FeetBehindTheWalls through the Legacy encoding: the depth test sees the same order
        [Fact]
        public void Acceptance1_FeetBehindTheWalls()
        {
            Vertex mobile = MobileQuad(PX, PY, Z, PX + PY + 1.37f + 1f);
            Vertex south = Tile(1453, 1669, Z, 20, SpriteInstance.ZGeoWall, false);
            Vertex east = Tile(1454, 1668, Z, 20, SpriteInstance.ZGeoWall, false);
            var (ax, ay) = GeoDepth.MobileAnchor(PX, PY, Z);
            int checkedPixels = 0;

            for (int dx = -22; dx <= 22; dx++)
            {
                for (int dy = 0; dy <= 4; dy++)
                {
                    float m = LegacyGeo.WindowDepth(Decode(in mobile, ax + dx, ay + dy));

                    if (dx <= 0)
                    {
                        LegacyGeo.WindowDepth(Decode(in south, ax + dx, ay + dy)).Should().BeLessThan(m, $"south wall at {dx},{dy}");
                        checkedPixels++;
                    }

                    if (dx >= 0)
                    {
                        LegacyGeo.WindowDepth(Decode(in east, ax + dx, ay + dy)).Should().BeLessThan(m, $"east wall at {dx},{dy}");
                        checkedPixels++;
                    }
                }
            }

            checkedPixels.Should().BeGreaterThan(200);
        }

        // DepthModelTests.FeetBeatTheFloorTheyStandOnAndTheFloorAhead, and clip-3's same-tile box
        [Fact]
        public void FeetBeatTheFloorsAndASameTileBox()
        {
            Vertex mobile = MobileQuad(PX, PY, Z, PX + PY + 2.37f);
            var (ax, ay) = GeoDepth.MobileAnchor(PX, PY, Z);
            Vertex box = Tile(PX, PY, Z, 12, 0u, false);

            foreach (var (tx, ty) in new[] { (PX, PY), (PX + 1, PY), (PX, PY + 1), (PX + 1, PY + 1) })
            {
                var land = Quad(StaticKey(tx, ty, Z));
                LegacyGeoDepth.Land(tx, ty, Z, OffX, OffY, out float col, out float row, out double b);
                LegacyGeo.Apply(ref land, LegacyGeo.Flat, col, row, b);

                for (int dx = -10; dx <= 10; dx += 2)
                {
                    for (int dy = 0; dy <= 10; dy += 2)
                    {
                        Decode(in mobile, ax + dx, ay + dy).Should().BeGreaterThan(Decode(in land, ax + dx, ay + dy), $"land {tx},{ty} at {dx},{dy}");
                    }
                }
            }

            for (int dx = -10; dx <= 10; dx += 2)
            {
                for (int dy = -40; dy <= 6; dy += 2)
                {
                    Decode(in mobile, ax + dx, ay + dy).Should().BeGreaterThan(Decode(in box, ax + dx, ay + dy), $"box at {dx},{dy}");
                }
            }
        }

        // the key fallback (TexCoord.z 0: effects, shadows, seated mobiles) is the Gpu's key mapping: (D - Dcam) / 100 + 0.5
        [Fact]
        public void KeyFallbackMatchesTheGpuMapping()
        {
            foreach (float key in new[] { PX + PY + 1.37f, PX + PY + 7.27f, PX + PY - 40.5f })
            {
                int d = (int)MathF.Round(key * 100), dCam = Cam * 100 + 127;
                LegacyGeo.PixelSum(0f, key, 123, 456, 7, 8, Cam).Should().BeApproximately((d - dCam) / 100f + 0.5f, 1e-3f);
            }
        }

        [Fact]
        public void ShadowsAndUntouchedQuadsKeepTheKey()
        {
            Vertex shadow = Quad(3000.5f);
            shadow.Hue0.Y = ShaderHueTranslator.SHADER_SHADOW;
            Vertex before = shadow;
            LegacyGeo.Apply(ref shadow, LegacyGeo.Box, 10, 20, 3001.0);
            shadow.Should().Be(before);
            shadow.TextureCoordinate0.Z.Should().Be(0f);
        }

        // ZBias (2e-3 sums), the finest tie the model breaks, stays several Depth24 steps apart across the camera's range
        [Fact]
        public void ZBiasResolvesInDepth24()
        {
            const float step = 1f / ((1 << 24) - 1);

            for (float rel = -1200f; rel <= 1200f; rel += 37.5f)
            {
                (LegacyGeo.WindowDepth(rel) - LegacyGeo.WindowDepth(rel + GeoDepth.ZBias)).Should().BeGreaterThan(8 * step, $"at {rel}");
            }
        }

        // the switch: off, the mesh quads are the key model's bit for bit; on, they carry the shapes
        [Fact]
        public void MeshQuadsOnlyChangeWithTheSwitchOn()
        {
            try
            {
                foreach (bool on in new[] { false, true })
                {
                    DepthModel.OverrideForTests = on;
                    Vertex st = Quad(StaticKey(PX, PY, Z), 100, 200, 144, 290);
                    Vertex land = Quad(StaticKey(PX, PY, Z));
                    Vertex stretched = Quad(StaticKey(PX, PY, Z));
                    Vertex st0 = st, land0 = land, stretched0 = stretched;
                    LegacyGeoDepth.ApplyTile(ref st, DepthModel.On, PX, PY, Z, 20, SpriteInstance.ZGeoWall, false);
                    LegacyGeoDepth.ApplyLand(ref land, DepthModel.On, PX, PY, Z, false);
                    LegacyGeoDepth.ApplyLand(ref stretched, DepthModel.On, PX, PY, Z, true);

                    if (!on)
                    {
                        st.Should().Be(st0);
                        land.Should().Be(land0);
                        stretched.Should().Be(stretched0);

                        continue;
                    }

                    MathF.Floor(st.TextureCoordinate0.Z / LegacyGeo.ClassStride).Should().Be(LegacyGeo.Wall);
                    MathF.Floor(land.TextureCoordinate3.Z / LegacyGeo.ClassStride).Should().Be(LegacyGeo.Flat);
                    MathF.Floor(stretched.TextureCoordinate1.Z / LegacyGeo.ClassStride).Should().Be(LegacyGeo.Linear);
                    st.Position0.Should().Be(st0.Position0, "the key and the rectangle stay");
                }
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }

        // LegacySpriteSink's anchor is the one the extract gives every layer's record (DynamicSprite.GeoAx/GeoAy)
        [Theory]
        [MemberData(nameof(DynamicSpriteTests.MobileCases), MemberType = typeof(DynamicSpriteTests))]
        public void SinkAnchorMatchesTheRecords(string name)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = DynamicSpriteTests.AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = DynamicSpriteTests.MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);

            try
            {
                DepthModel.OverrideForTests = true;
                extract.Begin(in tables, 37, -12);
                extract.Select(e, 600, 400, 812.25f);
                EcsRow data = EcsRead.Row(e);
                MobileSpriteExtract.GeoAnchor(in data.Get<AnimationState>(), in data.Get<MobileRenderState>(), 600, 400, out int ax, out int ay);
                int geo = 0;

                foreach (DynamicSprite s in extract.Selected)
                {
                    if (s.Geo)
                    {
                        (s.X + s.GeoAx, s.Y + s.GeoAy).Should().Be((ax, ay), $"body {s.Body}");
                        geo++;
                    }
                }

                geo.Should().BeGreaterThan(0);
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }
    }
}
