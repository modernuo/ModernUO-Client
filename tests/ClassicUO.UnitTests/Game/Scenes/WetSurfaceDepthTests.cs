// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
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
    /// wet_surface_depth: a mobile under a water surface (Felucca 1675 1705: water statics Background|Wet, height 0, at z -5 over
    /// sea-bed land at z -15, the player at z -15). depth_model on: wet flats drop GeoDepth.WetDrop, so the waterline is near
    /// 4 px per z above the mobile's centre row (FlatDrop put it 7 px above it). depth_model off: the strips all take + 1.
    /// </summary>
    [Collection(nameof(DepthModelSwitchCollection))]
    public sealed class WetSurfaceDepthTests : IDisposable
    {
        private const int MX = 1675, MY = 1705, WaterZ = -5, FloorZ = -15;
        private const ushort Water = 0x1797, Rock = 2;

        private readonly StaticTiles[] _savedStaticData;

        public WetSurfaceDepthTests()
        {
            var data = new StaticTiles[0x10000];
            data[Water].Flags = TileFlag.Background | TileFlag.Impassable | TileFlag.Wet;
            data[Rock].Flags = TileFlag.Impassable;
            data[Rock].Height = 10;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            WetSurfaceDepth.OverrideForTests = null;
            DepthModel.OverrideForTests = null;
        }

        [Fact]
        public void WetBitOnlyOnWetFlats()
        {
            SpriteInstance.GeoShapeBits(false, true, false, true, 0).Should().Be(SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet);
            SpriteInstance.GeoShapeBits(false, true, false, false, 0).Should().Be(SpriteInstance.ZGeoFlat);
            SpriteInstance.GeoShapeBits(true, false, false, true, 20).Should().Be(SpriteInstance.ZGeoWall, "a wet wall keeps its shape");
            SpriteInstance.GeoShapeBits(false, false, false, true, 6).Should().Be(0u, "a wet box (fountain) keeps its shape");
            SpriteInstance.GeoShapeBits(false, false, true, true, 3).Should().Be(0u);
        }

        [Fact]
        public void GeoTileCarriesTheWetBit()
        {
            var inst = new SpriteInstance { Whf = SpriteInstance.PackWhf(44, 44, 0) };
            inst.GeoTile(MX, MY, WaterZ, 0, SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet).Should().BeTrue();
            inst.IsGeoTile.Should().BeTrue();
            ((inst.Corners >> 16) & SpriteInstance.ZGeoWet).Should().Be(SpriteInstance.ZGeoWet);
            ((inst.Corners >> 16) & 0x7F).Should().Be(SpriteInstance.ZGeoFlat);
            ((sbyte)inst.Corners).Should().Be(WaterZ);
        }

        [Fact]
        public void BakedWaterStaticsCarryTheWetBitWithTheShapeBits()
        {
            var data = new Uo.Pack.StaticTileData[2];
            data[1] = new Uo.Pack.StaticTileData { Flags = TileFlags.Background | TileFlags.Wet, Height = 0 };
            uint on = BakeJob.ZInfo(1, WaterZ, data, FloorZ, true);
            uint off = BakeJob.ZInfo(1, WaterZ, data, FloorZ, false);
            ((on >> 16) & (SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet)).Should().Be(SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet);
            ((on >> 16) & SpriteInstance.ZGeoBuried).Should().Be(0u);
            ((off >> 16) & (SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet | SpriteInstance.ZGeoTile)).Should().Be(0u, "depth_model off bakes the key model's bits only");
            (on & ~((SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet) << 16)).Should().Be(off);
        }

        // the first row (from the top) at the mobile's anchor column where the water flat beats the mobile's billboard
        private static int Waterline(bool wet, int mobileZ, out int centreRow)
        {
            var (ax, ay) = GeoDepth.MobileAnchor(MX, MY, mobileZ);
            centreRow = ay + 3;

            for (int sy = centreRow - 80; sy <= centreRow + 22; sy++)
            {
                float water = GeoDepth.Static(MX + 1, MY, WaterZ, 0, false, true, false, ax, sy, wet);

                if (water > GeoDepth.MobileBiased(ax, ay, mobileZ, ax, sy))
                {
                    return sy;
                }
            }

            return int.MaxValue;
        }

        [Fact]
        public void DepthModelWaterlineFollowsTheWaterHeight()
        {
            // the report: 10 z under the surface; FlatDrop's line is 7 px above the centre row, WetDrop's 38 (true: 40)
            Waterline(false, FloorZ, out int c).Should().Be(c - 7);
            Waterline(true, FloorZ, out c).Should().Be(c - 38);

            // 5 z under (wfd_DSm10): FlatDrop hid nothing above the feet; WetDrop cuts 17 px above the centre (true: 20)
            Waterline(false, -10, out c).Should().BeGreaterThan(c);
            Waterline(true, -10, out c).Should().Be(c - 17);
        }

        [Fact]
        public void MobileOnTheWaterStillWins()
        {
            // a mobile at the water's own z (swimming, a sea creature) beats the water on and below its centre row inside its
            // own tile's front faces, by MobileBias
            var (ax, ay) = GeoDepth.MobileAnchor(MX, MY, WaterZ);

            for (int dx = -11; dx <= 11; dx++)
            {
                for (int dy = -80; dy <= 5; dy++)
                {
                    float sx = ax + dx, sy = ay + dy;

                    for (int t = 0; t < 3; t++)
                    {
                        int tx = MX + (t == 1 ? 1 : 0), ty = MY + (t == 2 ? 1 : 0);
                        GeoDepth.MobileBiased(ax, ay, WaterZ, sx, sy)
                            .Should().BeGreaterThan(GeoDepth.Static(tx, ty, WaterZ, 0, false, true, false, sx, sy, true), $"tile {tx},{ty} at {dx},{dy}");
                    }
                }
            }
        }

        // ---- Legacy (LegacyGeoDepth / LegacyGeo, as LegacyDepthModelTests decodes them) ----

        private const int OffX = -1800, OffY = 66700;
        private static readonly int Cam = LegacyGeo.CamSum(OffY + 300);

        private static Vertex LegacyTile(int tx, int ty, int z, uint bits)
        {
            var v = new Vertex();
            float key = tx + ty + (127 + z) * 0.01f + 0.5f;
            v.Position0 = new Microsoft.Xna.Framework.Vector3(0, 0, key);
            v.Position1 = new Microsoft.Xna.Framework.Vector3(44, 0, key);
            v.Position2 = new Microsoft.Xna.Framework.Vector3(0, 44, key);
            v.Position3 = new Microsoft.Xna.Framework.Vector3(44, 44, key);
            v.Hue0 = v.Hue1 = v.Hue2 = v.Hue3 = new Microsoft.Xna.Framework.Vector3(0, ShaderHueTranslator.SHADER_HUED, 1);
            LegacyGeoDepth.Tile(tx, ty, z, 0, bits, false, OffX, OffY, out int cls, out float col, out float row, out double b);
            LegacyGeo.Apply(ref v, cls, col, row, b);

            return v;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LegacyWetFlatDecodesToGeoDepth(bool switchOn)
        {
            uint bits = (SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet) & LegacyGeoDepth.WetMask(switchOn);
            Vertex v = LegacyTile(MX + 1, MY, WaterZ, bits);
            var (nx, ny) = GeoDepth.NorthVertex(MX + 1, MY, WaterZ);

            for (int dx = -22; dx <= 22; dx += 5)
            {
                for (int dy = 0; dy <= 44; dy += 4)
                {
                    float sx = nx + dx, sy = ny + dy;
                    float expected = GeoDepth.Static(MX + 1, MY, WaterZ, 0, false, true, false, sx, sy, switchOn) - Cam;
                    float got = LegacyGeo.PixelSum(v.TextureCoordinate0.Z, v.Position0.Z, v.Normal0.X, v.Normal0.Y, sx - OffX, sy - OffY, Cam);
                    got.Should().BeApproximately(expected, 1e-3f, $"at {dx},{dy}");
                }
            }
        }

        [Fact]
        public void WetMaskClearsOnlyTheWetBit()
        {
            uint all = SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoWet;
            (all & LegacyGeoDepth.WetMask(false)).Should().Be(SpriteInstance.ZGeoFlat);
            (all & LegacyGeoDepth.WetMask(true)).Should().Be(all);
        }

        // ---- key mode: the tile scan and the strips ----

        private static (Mobile M, Chunk Chunk) Place(World world, sbyte mobileZ, ushort graphic, sbyte staticZ)
        {
            Chunk chunk = Chunk.Create(world, MX >> 3, MY >> 3);
            var st = new Static(world) { Graphic = graphic, Z = staticZ, X = MX, Y = MY };
            chunk.AddGameObject(st, MX & 7, MY & 7);
            Mobile m = world.GetOrCreateMobile(0x0010_0001);
            m.X = MX;
            m.Y = MY;
            m.Z = mobileZ;
            chunk.AddGameObject(m, MX & 7, MY & 7);

            return (m, chunk);
        }

        [Theory]
        [InlineData(false, false, -15, false)] // switch off: no rule
        [InlineData(true, false, -15, true)] // the report: 10 z under
        [InlineData(true, false, -10, true)] // 5 z under
        [InlineData(true, false, -5, false)] // on the water
        [InlineData(true, false, 0, false)] // above it
        [InlineData(true, true, -15, false)] // depth_model on: the geometric rule instead
        public void KeySubmergedBySwitchAndHeight(bool wetOn, bool depthOn, int mobileZ, bool expected)
        {
            var world = new World();
            (Mobile m, _) = Place(world, (sbyte)mobileZ, Water, WaterZ);
            WetSurfaceDepth.OverrideForTests = wetOn;
            DepthModel.OverrideForTests = depthOn;
            WetSurfaceDepth.KeySubmerged(m, new MobileFrameCache()).Should().Be(expected);
        }

        [Fact]
        public void DryStaticAboveIsNotWater()
        {
            var world = new World();
            (Mobile m, _) = Place(world, FloorZ, Rock, WaterZ);
            WetSurfaceDepth.OverrideForTests = true;
            DepthModel.OverrideForTests = false;
            WetSurfaceDepth.KeySubmerged(m, new MobileFrameCache()).Should().BeFalse();
        }

        [Fact]
        public void ScanFollowsTheMobilesHeight()
        {
            var world = new World();
            (Mobile m, _) = Place(world, FloorZ, Water, WaterZ);
            WetSurfaceDepth.OverrideForTests = true;
            DepthModel.OverrideForTests = false;
            var cache = new MobileFrameCache();
            WetSurfaceDepth.KeySubmerged(m, cache).Should().BeTrue();
            cache.WetSurfaceZ.Should().Be(WaterZ);
            m.Z = WaterZ;
            WetSurfaceDepth.KeySubmerged(m, cache).Should().BeFalse("the cached scan is redone for the new z");
            WetSurfaceDepth.OverrideForTests = false;
            WetSurfaceDepth.KeySubmerged(m, cache).Should().BeFalse();
        }

        private static uint[] Depths(string name, bool submerged, int seed = 5)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = DynamicSpriteTests.AnimFixture(seed);
            var mirror = new WorldMirror();
            EcsEntity e = DynamicSpriteTests.MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);
            DepthModel.OverrideForTests = false;
            extract.Begin(in tables, 37, -12);
            extract.Emit(e, 600, 400, 812.25f, new MobileFrameCache { Submerged = submerged });

            return extract.Instances.ToArray().Select(i => i.Depth).ToArray();
        }

        // the fixture's random frames have a second (feet) strip only when their centre lies above the frame's bottom (and the
        // trimmed art reaches it); shadows keep the base depth
        [Theory]
        [InlineData("plain")]
        [InlineData("mounted")]
        [InlineData("equipped")]
        public void SubmergedStripsAllTakePlusOne(string name)
        {
            // MobileSpriteExtract.DepthOf and the key of the fixture mobile (tile x 1000, RANK_MOBILE 7)
            uint Key(float d) => BakeJob.DepthKeyD((int)MathF.Round(d * 100f) - 50, 1000, 7);
            uint baseKey = Key(812.25f), plusOne = Key(812.25f + 1f);
            int changed = 0;

            for (int seed = 1; seed <= 40; seed++)
            {
                uint[] off = Depths(name, false, seed);
                uint[] on = Depths(name, true, seed);
                on.Length.Should().Be(off.Length);
                on.Should().OnlyContain(d => d == baseKey || d == plusOne, $"seed {seed}: off {string.Join(",", off)} on {string.Join(",", on)}");

                if (off.Any(d => d != baseKey && d != plusOne))
                {
                    changed++;
                }
            }

            changed.Should().BeGreaterThan(0, "some frames have a feet strip");
        }

        [Fact]
        public void SeatedMobileIsUnchanged()
        {
            for (int seed = 1; seed <= 10; seed++)
            {
                Depths("sitting", true, seed).Should().Equal(Depths("sitting", false, seed));
            }
        }

        [Fact]
        public void SubmergedIsPartOfTheSelectKey()
        {
            var a = new MobileFrameCache.SelectKey { X = MX, Y = MY, Z = FloorZ };
            var b = a;
            b.Submerged = true;
            a.Equals(b).Should().BeFalse("a flip of the rule re-records the strips");
        }
    }
}
