// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Scenes;
using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// clip-2 depth model (GeoDepth, mirrored by sprite.hlsl PSMainGeo). Geometry of acceptance #1, Felucca 1453 1668 z 10: the
    /// art studio's upper storey has walls at z 10, height 20 on 1453 1669 (south wall, its NE edge), 1454 1668 (east wall, its
    /// NW edge) and the corner 1454 1669; the player stands inside at 1453 1668 z 10, its frame reaching 4 rows below the
    /// anchor (mounted: 11 rows, 66 px wide).
    /// </summary>
    [CollectionDefinition(nameof(DepthModelSwitchCollection), DisableParallelization = true)]
    public class DepthModelSwitchCollection
    {
    }

    // DepthModel.OverrideForTests is global: the extract reads it at Begin, so the tests that flip it run alone
    [Collection(nameof(DepthModelSwitchCollection))]
    public class DepthModelTests
    {
        private const int PX = 1453, PY = 1668, Z = 10;

        private static float MobileAt(int dx, int dyFromAnchor, int tx = PX, int ty = PY, int z = Z)
        {
            var (ax, ay) = GeoDepth.MobileAnchor(tx, ty, z);

            return GeoDepth.Mobile(ax, ay, z, ax + dx, ay + dyFromAnchor) + GeoDepth.MobileBias + z * GeoDepth.ZBias;
        }

        private static float WallAt(int tx, int ty, int dx, int z = Z)
        {
            var (ax, _) = GeoDepth.MobileAnchor(PX, PY, Z);

            return GeoDepth.Wall(tx, ty, ax + dx) + GeoDepth.WallBias + z * GeoDepth.ZBias;
        }

        // the walls whose art covers screen column dx (relative to the player's anchor): the south wall's NE edge spans
        // dx in [-22, 0], the east wall's NW edge [0, 22], the corner post sits at dx 0
        private static float NearestWallAt(int dx)
        {
            float d = float.MaxValue;

            if (dx >= -22 && dx <= 0)
            {
                d = System.Math.Min(d, WallAt(1453, 1669, dx));
            }

            if (dx >= 0 && dx <= 22)
            {
                d = System.Math.Min(d, WallAt(1454, 1668, dx));
            }

            if (dx >= -2 && dx <= 2)
            {
                d = System.Math.Min(d, WallAt(1454, 1669, dx));
            }

            return d;
        }

        [Fact]
        public void Acceptance1_FeetBehindTheWalls()
        {
            for (int dx = -12; dx <= 12; dx++)
            {
                for (int row = 0; row <= 4; row++)
                {
                    MobileAt(dx, row).Should().BeLessThan(NearestWallAt(dx), $"feet pixel ({dx}, +{row}) must stay behind the wall");
                }
            }
        }

        [Fact]
        public void Acceptance1_OldKeysPutTheFeetInFront()
        {
            // the key model this replaces: strip 1 at base + 3 against the walls' x + y + 0.5 + (127 + pz) / 100
            float baseDepth = PX + PY + (127 + Z + 1) / 100f;
            float strip1 = baseDepth + 3f;
            float southWall = 1453 + 1669 + 0.5f + (127 + Z + 1) / 100f;
            float corner = 1454 + 1669 + 0.5f + (127 + Z + 1) / 100f;

            strip1.Should().BeGreaterThan(southWall);
            strip1.Should().BeGreaterThan(corner);
        }

        [Fact]
        public void Acceptance1_MountedHoovesBehindTheWalls()
        {
            for (int dx = -22; dx <= 22; dx++)
            {
                for (int row = 0; row <= 11; row++)
                {
                    MobileAt(dx, row).Should().BeLessThan(NearestWallAt(dx), $"hoof pixel ({dx}, +{row})");
                }
            }
        }

        [Fact]
        public void BodyAboveTheWallTopStaysBehindWhereTheWallIs()
        {
            for (int dx = -11; dx <= 11; dx++)
            {
                MobileAt(dx, -40).Should().BeLessThan(NearestWallAt(dx));
            }
        }

        [Fact]
        public void FeetBeatTheFloorTheyStandOnAndTheFloorAhead()
        {
            var (ax, ay) = GeoDepth.MobileAnchor(PX, PY, Z);

            for (int dx = -30; dx <= 30; dx += 3)
            {
                for (int row = 0; row <= 20; row++)
                {
                    float floor = GeoDepth.Flat(ay + row, Z) + (Z - 2) * GeoDepth.ZBias;
                    MobileAt(dx, row).Should().BeGreaterThan(floor, $"feet pixel ({dx}, +{row}) over a floor at the mobile's z");
                    _ = ax;
                }
            }
        }

        [Fact]
        public void InFrontOfTheWallTheWholeMobileWins()
        {
            // standing on the south wall's own tile (outside, in front of the wall line Y = 1669)
            for (int dx = -18; dx <= 18; dx++)
            {
                for (int row = -60; row <= 10; row += 5)
                {
                    var (ax, ay) = GeoDepth.MobileAnchor(1453, 1669, Z);
                    float mob = GeoDepth.Mobile(ax, ay, Z, ax + dx, ay + row) + GeoDepth.MobileBias;
                    float wall = GeoDepth.Wall(1453, 1669, ax + dx) + GeoDepth.WallBias;
                    float westWall = GeoDepth.Wall(1452, 1669, ax + dx) + GeoDepth.WallBias;

                    if (dx >= 0)
                    {
                        mob.Should().BeGreaterThan(wall, $"({dx}, {row})");
                    }
                    else
                    {
                        mob.Should().BeGreaterThan(westWall, $"({dx}, {row})");
                    }
                }
            }
        }

        // bench6-000021 c1u_out_1453_1671_d0: the lower storey's walls (0x0034, z -11, height 20) on 1451-1454 1669 lie under
        // the land (z 10). Lowered like the land, the land ahead (1452 1670, z 10) covers every point of the face again.
        [Fact]
        public void BuriedWallStaysUnderTheLandAhead()
        {
            const int wz = -11, wh = 20, lz = 10;
            var (nx, ny) = GeoDepth.NorthVertex(1452, 1669, wz);
            int poked = 0;

            for (int col = -22; col <= 22; col++)
            {
                for (int zp = wz; zp <= wz + wh; zp++)
                {
                    // the face point on the back edges at height zp
                    float sx = nx + col, sy = ny + System.Math.Abs(col) - 4 * (zp - wz);
                    float land = GeoDepth.Flat(sy, lz) + (lz - 2) * GeoDepth.ZBias;
                    float wall = GeoDepth.Wall(1452, 1669, sx) + GeoDepth.WallBias + wz * GeoDepth.ZBias;
                    poked += wall > land ? 1 : 0;
                    (wall - GeoDepth.BuriedDrop).Should().BeLessThan(land, $"column {col}, z {zp}");
                }
            }

            poked.Should().BeGreaterThan(0, "without the drop the face's upper part beat the land (the shots' teeth)");
        }

        [Theory]
        [InlineData(-11, 20, 10, true)] // top 9 under land 10
        [InlineData(-10, 20, 10, true)] // top 10 at the land
        [InlineData(-9, 20, 10, false)] // top 11 above it
        [InlineData(10, 20, 10, false)]
        public void BuriedBitFromTheTilesLand(int z, int height, int landZ, bool buried)
        {
            var data = new Uo.Pack.StaticTileData[2];
            data[1] = new Uo.Pack.StaticTileData { Flags = Uo.Render.TileFlags.Wall, Height = (byte)height };
            uint bits = BakeJob.ZInfo(1, z, data, landZ, true) >> 16;
            ((bits & SpriteInstance.ZGeoBuried) != 0).Should().Be(buried);
            (bits & SpriteInstance.ZGeoWall).Should().Be(SpriteInstance.ZGeoWall);

            // flats are lowered anyway and never marked
            data[1] = new Uo.Pack.StaticTileData { Flags = Uo.Render.TileFlags.Surface, Height = 0 };
            ((BakeJob.ZInfo(1, z, data, landZ, true) >> 16) & SpriteInstance.ZGeoBuried).Should().Be(0u);
        }

        // switch off (PackSet.BakeGeo false): the baked Corners are the key model's alone, bit for bit (audit clip-12 P2)
        [Theory]
        [InlineData(Uo.Render.TileFlags.Wall, 20, -11)]
        [InlineData(Uo.Render.TileFlags.Wall | Uo.Render.TileFlags.Roof, 20, 30)]
        [InlineData(Uo.Render.TileFlags.Surface, 0, 4)]
        [InlineData(Uo.Render.TileFlags.Background, 0, -20)]
        [InlineData(Uo.Render.TileFlags.Foliage, 7, 0)]
        [InlineData(0UL, 3, -40)]
        public void ZInfoOffHasNoGeoBits(ulong flags, int height, int z)
        {
            var data = new Uo.Pack.StaticTileData[2];
            data[1] = new Uo.Pack.StaticTileData { Flags = flags, Height = (byte)height };
            const uint geoBits = (SpriteInstance.ZGeoWall | SpriteInstance.ZGeoFlat | SpriteInstance.ZGeoBuried | SpriteInstance.ZGeoTile) << 16;
            uint off = BakeJob.ZInfo(1, z, data, 10, false);
            (off & geoBits).Should().Be(0u);
            uint on = BakeJob.ZInfo(1, z, data, 10, true);
            (on & ~geoBits).Should().Be(off, "the switch adds the shape bits and changes nothing else");
        }

        // CUO_CLIP_ORDER_CHECK's view of the shader classes (sprite.hlsl VSMain GEO, GeoDepth)
        [Fact]
        public void StaticMirrorsTheShaderClasses()
        {
            const int tx = 100, ty = 100, z = 5;
            var (nx, ny) = GeoDepth.NorthVertex(tx, ty, z);
            float sx = nx + 7, sy = ny + 10;

            GeoDepth.Static(tx, ty, z, 20, true, false, false, sx, sy).Should().Be(GeoDepth.Wall(tx, ty, sx) + GeoDepth.WallBias + z * GeoDepth.ZBias);
            GeoDepth.Static(tx, ty, z, 0, false, true, false, sx, sy).Should().Be(GeoDepth.Plane(sy, z) - GeoDepth.FlatDrop + z * GeoDepth.ZBias);
            GeoDepth.Static(tx, ty, z, 6, false, false, false, sx, sy).Should().Be(GeoDepth.Box(tx, ty, z, 6, sx, sy) + z * GeoDepth.ZBias);
            GeoDepth.Static(tx, ty, z, 6, false, false, true, sx, sy)
                .Should().BeApproximately(GeoDepth.Static(tx, ty, z, 6, false, false, false, sx, sy) - GeoDepth.BuriedDrop, 1e-4f);
            GeoDepth.Land(sy, z).Should().Be(GeoDepth.Flat(sy, z) + (z - 2) * GeoDepth.ZBias);
            GeoDepth.MobileBiased(nx, ny + 19, z, sx, sy).Should().Be(GeoDepth.Mobile(nx, ny + 19, z, sx, sy) + GeoDepth.MobileBias + z * GeoDepth.ZBias);
        }

        [Fact]
        public void OnShapeCoversTheClassShapes()
        {
            var (nx, ny) = GeoDepth.NorthVertex(100, 100, 0);

            // wall, height 20: the faces on the back edges, 80 px tall
            GeoDepth.OnShape(100, 100, 0, 20, true, false, nx, ny).Should().BeTrue();
            GeoDepth.OnShape(100, 100, 0, 20, true, false, nx, ny - 80).Should().BeTrue();
            GeoDepth.OnShape(100, 100, 0, 20, true, false, nx, ny + 1).Should().BeFalse();
            GeoDepth.OnShape(100, 100, 0, 20, true, false, nx + 22, ny + 22).Should().BeTrue();
            GeoDepth.OnShape(100, 100, 0, 20, true, false, nx + 23, ny + 22).Should().BeFalse();

            // box, height 10: top diamond to the south vertex
            GeoDepth.OnShape(100, 100, 0, 10, false, false, nx, ny - 40).Should().BeTrue();
            GeoDepth.OnShape(100, 100, 0, 10, false, false, nx, ny + 44).Should().BeTrue();
            GeoDepth.OnShape(100, 100, 0, 10, false, false, nx, ny - 41).Should().BeFalse();

            // flat: the diamond
            GeoDepth.OnShape(100, 100, 0, 0, false, true, nx, ny + 22).Should().BeTrue();
            GeoDepth.OnShape(100, 100, 0, 0, false, true, nx + 20, ny + 1).Should().BeFalse();
        }

        [Fact]
        public void BoxOnTheTileAheadHidesTheMobilesLowerBody()
        {
            // a crate (height 10) on the tile south-east of the player
            var (ax, ay) = GeoDepth.MobileAnchor(PX, PY, Z);
            float box = GeoDepth.Box(PX + 1, PY + 1, Z, 10, ax, ay + 10);
            float mob = GeoDepth.Mobile(ax, ay, Z, ax, ay + 10);

            box.Should().BeGreaterThan(mob);
        }

        [Fact]
        public void MobileOnTopOfABoxIsDrawnOverIt()
        {
            // a table (height 6) under the mobile: the body is above its top, the feet on it
            var (ax, ay) = GeoDepth.MobileAnchor(PX, PY, Z + 6);

            for (int row = -50; row <= 4; row += 2)
            {
                float box = GeoDepth.Box(PX, PY, Z, 6, ax, ay + row);
                float mob = GeoDepth.Mobile(ax, ay, Z + 6, ax, ay + row) + GeoDepth.MobileBias;
                mob.Should().BeGreaterThan(box, $"row {row}");
            }
        }

        [Fact]
        public void WallDepthIsOrientationFree()
        {
            var (nx, _) = GeoDepth.NorthVertex(100, 100, 0);
            GeoDepth.Wall(100, 100, nx).Should().Be(200f);
            GeoDepth.Wall(100, 100, nx + 22).Should().Be(201f);
            GeoDepth.Wall(100, 100, nx - 22).Should().Be(201f);
        }

        [Fact]
        public void BoxTopPlaneMeetsItsBackEdges()
        {
            var (nx, ny) = GeoDepth.NorthVertex(100, 100, 0);
            int h = 20;

            // the top diamond's north vertex: back edges and top plane agree
            GeoDepth.Box(100, 100, 0, h, nx, ny - 4 * h).Should().BeApproximately(200f, 1e-3f);

            // centre of the top diamond: the top plane, x + y + 1
            GeoDepth.Box(100, 100, 0, h, nx, ny - 4 * h + 22).Should().BeApproximately(201f, 1e-3f);

            // the front face's lowest point (the south vertex at z): x + y + 2
            GeoDepth.Box(100, 100, 0, h, nx, ny + 44).Should().BeApproximately(202f, 1e-3f);

            // a crown pixel high above the box: a vertical plane on the back edges
            GeoDepth.Box(100, 100, 0, h, nx + 11, ny - 200).Should().BeApproximately(200.5f, 1e-3f);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(-37, 61, 10)]
        [InlineData(2047, -2048, -128)]
        [InlineData(-2048, 2047, 127)]
        [InlineData(12, -4, -5)]
        public void GeoMobileRoundTrips(int adx, int ady, int z)
        {
            var inst = new SpriteInstance
            {
                Whf = SpriteInstance.PackWhf(20, 30, SpriteInstance.FlagIndexed),
                Light = 0x1000 | 0x4000 | 0x37,
                Corners = 0xBEEF
            };

            inst.GeoMobile(adx, ady, z).Should().BeTrue();
            inst.TryGeoMobile(out int rx, out int ry, out int rz).Should().BeTrue();
            (rx, ry, rz).Should().Be((adx, ady, z));

            // the palette, alpha and the COT / additive bits survive
            (inst.Corners & 0xFFFF).Should().Be(0xBEEFu);
            (inst.Light & 0x5FFF).Should().Be(0x1000u | 0x4000u | 0x37u);
        }

        [Fact]
        public void GeoMobileRefusesOutOfRangeAndDeformed()
        {
            var inst = new SpriteInstance { Whf = SpriteInstance.PackWhf(20, 30, SpriteInstance.FlagIndexed) };
            inst.GeoMobile(4000, 0, 0).Should().BeFalse();
            inst.TryGeoMobile(out _, out _, out _).Should().BeFalse();

            var sit = new SpriteInstance { Whf = SpriteInstance.PackWhf(20, 30, SpriteInstance.FlagIndexed | SpriteInstance.FlagDeform) };
            sit.GeoMobile(1, 1, 1).Should().BeFalse();
        }

        [Fact]
        public void SwitchStates()
        {
            try
            {
                DepthModel.OverrideForTests = true;
                DepthModel.On.Should().BeTrue();
                DepthModel.OverrideForTests = false;
                DepthModel.On.Should().BeFalse();
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }
    }
}
