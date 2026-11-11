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
