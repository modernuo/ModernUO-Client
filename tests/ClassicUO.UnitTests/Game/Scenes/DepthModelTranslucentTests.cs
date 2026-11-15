// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
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
    /// clip-5: translucent sprites (hidden or fading mobiles and items, tiledata Translucent statics, the circle of
    /// transparency) keep their geometric shape with depth_model on, on both renderers; only shadows (and effects, which
    /// never get a shape) keep the depth key. The design note's "translucent sprites keep the key" belonged to the per-vertex
    /// option, whose split boxes would blend twice; the per-pixel depth draws each sprite once, and a translucent mobile on the
    /// key would show its feet on the walls again (acceptance #1). The shape does not depend on the alpha.
    /// </summary>
    [Collection(nameof(DepthModelSwitchCollection))]
    public class DepthModelTranslucentTests
    {
        private static List<SpriteInstance> EmitMobile(string name)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = DynamicSpriteTests.AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = DynamicSpriteTests.MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);

            try
            {
                DepthModel.OverrideForTests = true;
                extract.Begin(in tables, 37, -12);
                extract.Emit(e, 600, 400, 812.25f);

                return new List<SpriteInstance>(extract.Instances.ToArray());
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }

        private static bool IsShadow(in SpriteInstance i) => i.Mode == HueMode.Shadow;

        // Gpu: a translucent mobile's layers draw in the translucent pass with the composite anchor of the opaque mobile
        [Fact]
        public void Gpu_Translucent_Mobile_Keeps_The_Composite_Anchor()
        {
            List<SpriteInstance> plain = EmitMobile("plain").FindAll(i => !IsShadow(in i));
            List<SpriteInstance> trans = EmitMobile("translucent");

            foreach (SpriteInstance s in trans)
            {
                if (IsShadow(in s))
                {
                    s.TryGeoMobile(out _, out _, out _).Should().BeFalse("shadows keep the key");
                }
            }

            trans = trans.FindAll(i => !IsShadow(in i));
            trans.Count.Should().Be(plain.Count);
            int layers = 0;

            for (int k = 0; k < trans.Count; k++)
            {
                SpriteInstance t = trans[k], p = plain[k];
                t.Translucent.Should().BeTrue("the case is a translucent mobile");
                p.Translucent.Should().BeFalse();
                t.TryGeoMobile(out int tx, out int ty, out int tz).Should().BeTrue("translucent layers keep the shape");
                p.TryGeoMobile(out int px, out int py, out int pz).Should().BeTrue();
                (t.X + tx, t.Y + ty, tz).Should().Be((p.X + px, p.Y + py, pz), "the same anchor as the opaque mobile");
                layers++;
            }

            layers.Should().BeGreaterThan(0);
        }

        // Gpu: a fading item (ProcessAlpha) is a translucent instance with the static shape of the opaque item
        [Theory]
        [InlineData("fading")]
        public void Gpu_Translucent_Item_Keeps_The_Tile_Shape(string name)
        {
            (ArtLoader arts, GpuSpriteTables tables) = DynamicSpriteTests.ArtFixture();

            SpriteInstance[] Emit(string c)
            {
                ItemSpriteInput input = DynamicSpriteTests.ItemCase(c);
                input.X = 1500;
                input.Y = 1600;
                input.Z = 10;
                input.TileHeight = 6;
                input.TileGeoBits = 0;
                var extract = new ItemSpriteExtract(arts, null);

                try
                {
                    DepthModel.OverrideForTests = true;
                    extract.Emit(in input, in tables, 15, -40, (1500 - 1600) * 22 - 22 - 15, (1500 + 1600) * 22 - 40 - 22 + 40, 455.5f)
                        .Should().BeTrue();

                    return extract.Instances.ToArray();
                }
                finally
                {
                    DepthModel.OverrideForTests = null;
                }
            }

            SpriteInstance[] plain = Emit("plain");
            SpriteInstance[] trans = Emit(name);
            trans.Length.Should().Be(plain.Length);

            for (int k = 0; k < trans.Length; k++)
            {
                trans[k].Translucent.Should().BeTrue();
                trans[k].IsGeoTile.Should().BeTrue("translucent items keep the shape");
                trans[k].Corners.Should().Be(plain[k].Corners, "same z, height and shape bits");
                (trans[k].Light >> 16).Should().Be(plain[k].Light >> 16, "same tile");
            }
        }

        private static Vertex Quad(float key, float alpha, byte mode)
        {
            var v = new Vertex();
            v.Position0 = new Microsoft.Xna.Framework.Vector3(0, 0, key);
            v.Position1 = new Microsoft.Xna.Framework.Vector3(44, 0, key);
            v.Position2 = new Microsoft.Xna.Framework.Vector3(0, 80, key);
            v.Position3 = new Microsoft.Xna.Framework.Vector3(44, 80, key);
            v.Hue0 = v.Hue1 = v.Hue2 = v.Hue3 = new Microsoft.Xna.Framework.Vector3(0, mode, alpha);

            return v;
        }

        // Legacy: the batcher's patch (LegacyGeo.Apply through BeginGeo/EndGeo) writes the same shape whatever the alpha or
        // the spectral mode; only shadows keep the key, as on the Gpu
        [Theory]
        [InlineData(LegacyGeo.Wall)]
        [InlineData(LegacyGeo.Box)]
        [InlineData(LegacyGeo.Flat)]
        [InlineData(LegacyGeo.Mobile)]
        public void Legacy_Translucent_Quads_Take_The_Opaque_Shape(int cls)
        {
            Vertex opaque = Quad(3125.4f, 1f, ShaderHueTranslator.SHADER_HUED);
            Vertex faded = Quad(3125.4f, 0.47f, ShaderHueTranslator.SHADER_HUED);
            Vertex spectral = Quad(3125.4f, 1f, ShaderHueTranslator.SHADER_SPECTRAL);
            Vertex shadow = Quad(3125.4f, 0.5f, ShaderHueTranslator.SHADER_SHADOW);
            Vertex shadow0 = shadow;

            LegacyGeo.Apply(ref opaque, cls, 22f, 10f, 3124.02);
            LegacyGeo.Apply(ref faded, cls, 22f, 10f, 3124.02);
            LegacyGeo.Apply(ref spectral, cls, 22f, 10f, 3124.02);
            LegacyGeo.Apply(ref shadow, cls, 22f, 10f, 3124.02);

            MathF.Floor(opaque.TextureCoordinate0.Z / LegacyGeo.ClassStride).Should().Be(cls);

            foreach (Vertex v in new[] { faded, spectral })
            {
                (v.TextureCoordinate0.Z, v.TextureCoordinate1.Z, v.TextureCoordinate2.Z, v.TextureCoordinate3.Z)
                    .Should().Be((opaque.TextureCoordinate0.Z, opaque.TextureCoordinate1.Z, opaque.TextureCoordinate2.Z, opaque.TextureCoordinate3.Z));
                (v.Normal0, v.Normal3).Should().Be((opaque.Normal0, opaque.Normal3));
            }

            shadow.Should().Be(shadow0, "shadows keep the key");
        }

        // acceptance #1 with a translucent mobile (LegacyDepthModelTests.Acceptance1_FeetBehindTheWalls): through the Legacy
        // encoding its feet stay behind the studio's south and east walls, as the opaque mobile's do; the alpha does not enter
        // the depth
        [Fact]
        public void Translucent_Feet_Stay_Behind_The_Walls()
        {
            const int px = 1453, py = 1668, z = 10;
            int cam = LegacyGeo.CamSum((px + py) * 22);
            var (ax, ay) = GeoDepth.MobileAnchor(px, py, z);

            Vertex Wall(int tx, int ty)
            {
                Vertex v = Quad(tx + ty + (127 + z) * 0.01f + 0.5f, 1f, ShaderHueTranslator.SHADER_HUED);
                LegacyGeoDepth.Tile(tx, ty, z, 20, SpriteInstance.ZGeoWall, false, 0, 0, out int cls, out float col, out float row, out double b);
                LegacyGeo.Apply(ref v, cls, col, row, b);

                return v;
            }

            float Sum(in Vertex v, float x, float y) =>
                LegacyGeo.PixelSum(v.TextureCoordinate0.Z, v.Position0.Z, v.Normal0.X, v.Normal0.Y, x, y, cam);

            Vertex south = Wall(1453, 1669), east = Wall(1454, 1668);
            int checkedPixels = 0;

            foreach (float alpha in new[] { 1f, 0.47f })
            {
                Vertex mob = Quad(px + py + 1.37f + 1f, alpha, alpha < 1f ? ShaderHueTranslator.SHADER_SPECTRAL : ShaderHueTranslator.SHADER_HUED);
                LegacyGeoDepth.Mobile(ax, ay, z, 0, out float mcol, out float mrow, out double mb);
                LegacyGeo.Apply(ref mob, LegacyGeo.Mobile, mcol, mrow, mb);

                for (int dx = -22; dx <= 22; dx += 2)
                {
                    for (int dy = 0; dy <= 4; dy++)
                    {
                        float m = Sum(in mob, ax + dx, ay + dy);
                        m.Should().BeApproximately(GeoDepth.MobileBiased(ax, ay, z, ax + dx, ay + dy) - cam, 1e-3f);
                        Sum(dx <= 0 ? south : east, ax + dx, ay + dy).Should().BeGreaterThan(m, $"wall ahead at {dx},{dy}, alpha {alpha}");
                        checkedPixels++;
                    }
                }
            }

            checkedPixels.Should().BeGreaterThan(200);
        }
    }
}
