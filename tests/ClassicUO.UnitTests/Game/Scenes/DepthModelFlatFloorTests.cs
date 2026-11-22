// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Renderer;
using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// render-order-1: flats (static flats and flat land) are lowered by FlatDrop but never behind their own tile's back edges
    /// (GeoDepth.FlatFloored; sprite.hlsl GEO_FLAT / GEO_LAND, IsometricWorld.fx class 4). Geometry of spot 2, Felucca Moonglow
    /// bank: parapet walls 0x0162 (Wall, height 20) at z 20 and the lower storey's walls at z 0 on 4462 1159-1166, the flat roof
    /// 0x057F-0x0585 (Background, Surface, height 0) at z 20 from 4463; and the planter east of it (flowers, Background height 0,
    /// at z 8 on items 0x0080, Wall, z -14 height 22, on the same tiles).
    /// </summary>
    public class DepthModelFlatFloorTests
    {
        private const int WX = 4462, RoofZ = 20;

        private static float Roof(int tx, int ty, float sx, float sy) => GeoDepth.Static(tx, ty, RoofZ, 0, false, true, false, sx, sy);

        private static float WallAt(int tx, int ty, int z, float sx) => GeoDepth.Static(tx, ty, z, 20, true, false, false, sx, sy: 0);

        // every pixel of the roof tile's diamond that a wall of the tile behind it (art 44 wide, centred on its tile) can cover
        [Theory]
        [InlineData(1161, 20)] // the parapet
        [InlineData(1161, 0)] // the lower storey's wall, its top at the roof
        [InlineData(1162, 20)]
        [InlineData(1159, 0)]
        public void RoofAheadCoversTheWallOfTheTileBehind(int wy, int wz)
        {
            int covered = 0;

            foreach (var (rx, ry) in new[] { (WX + 1, wy), (WX + 1, wy + 1), (WX + 1, wy - 1) })
            {
                var (nx, ny) = GeoDepth.NorthVertex(rx, ry, RoofZ);
                var (wnx, _) = GeoDepth.NorthVertex(WX, wy, wz);

                for (int dx = -22; dx <= 22; dx++)
                {
                    float sx = nx + dx;

                    if (Math.Abs(sx - wnx) > 22)
                    {
                        continue;
                    }

                    for (int dy = 0; dy <= 44; dy++)
                    {
                        float sy = ny + dy;

                        if (!GeoDepth.OnShape(rx, ry, RoofZ, 0, false, true, sx, sy))
                        {
                            continue;
                        }

                        float wall = WallAt(WX, wy, wz, sx);
                        float roof = Roof(rx, ry, sx, sy);

                        // the wall wins only on the exact corner it shares with the roof tile's back edges
                        if (Math.Abs(GeoDepth.Wall(WX, wy, sx) - GeoDepth.Wall(rx, ry, sx)) < 0.05f)
                        {
                            continue;
                        }

                        roof.Should().BeGreaterThan(wall, $"roof {rx},{ry} over wall {WX},{wy} z {wz} at ({dx}, {dy})");
                        covered++;
                    }
                }
            }

            covered.Should().BeGreaterThan(100);
        }

        // the floor of the wall's own tile stays under the wall, as before, wherever the plane drop did it (rows up to 33 px
        // under the back edges)
        [Fact]
        public void WallStillCoversTheFloorOfItsOwnTile()
        {
            const int tx = 4470, ty = 1170, z = 20;
            var (nx, ny) = GeoDepth.NorthVertex(tx, ty, z);

            for (int dx = -22; dx <= 22; dx++)
            {
                for (int dy = 0; dy <= 44; dy++)
                {
                    float sx = nx + dx, sy = ny + dy;

                    if (!GeoDepth.OnShape(tx, ty, z, 0, false, true, sx, sy) || dy - Math.Abs(dx) > 32)
                    {
                        continue;
                    }

                    GeoDepth.Static(tx, ty, z, 20, true, false, false, sx, sy)
                        .Should().BeGreaterThan(GeoDepth.Static(tx, ty, z, 0, false, true, false, sx, sy), $"({dx}, {dy})");
                }
            }
        }

        // the planter: a flower (Background, height 0: a flat whose upright art rises above its diamond) beats the planter's wall
        // item on its own tile (z -14, top at the flower's z 8), as the key draws it
        [Fact]
        public void FlowerBeatsThePlanterWallOnItsTile()
        {
            const int tx = 4462, ty = 1182;
            var (nx, ny) = GeoDepth.NorthVertex(tx, ty, 8);

            for (int dx = -20; dx <= 20; dx += 2)
            {
                for (int dy = -40; dy <= 20; dy += 2)
                {
                    float sx = nx + dx, sy = ny + dy;
                    float flower = GeoDepth.Static(tx, ty, 8, 0, false, true, false, sx, sy);
                    float wall = GeoDepth.Static(tx, ty, -14, 22, true, false, false, sx, sy);
                    flower.Should().BeGreaterThan(wall, $"({dx}, {dy})");
                }
            }
        }

        // the floor never puts a flat in front of its own surface, and below the floor's reach the plane drop is unchanged
        [Fact]
        public void FloorIsAtMostThePlaneAndOnlyBindsUnderTheBackEdges()
        {
            foreach (int z in new[] { -20, 0, 20 })
            {
                const int tx = 1000, ty = 1200;
                var (nx, ny) = GeoDepth.NorthVertex(tx, ty, z);

                for (int dx = -22; dx <= 22; dx++)
                {
                    for (int dy = 0; dy <= 44; dy++)
                    {
                        float sx = nx + dx, sy = ny + dy;

                        if (!GeoDepth.OnShape(tx, ty, z, 0, false, true, sx, sy))
                        {
                            continue;
                        }

                        float floored = GeoDepth.FlatFloored(tx, ty, z, sx, sy);
                        floored.Should().BeLessThanOrEqualTo(GeoDepth.Plane(sy, z) + 1e-4f);
                        floored.Should().BeGreaterThanOrEqualTo(GeoDepth.Flat(sy, z));

                        if (dy - Math.Abs(dx) >= 33)
                        {
                            floored.Should().Be(GeoDepth.Flat(sy, z), $"({dx}, {dy}) is 33 px or more under the back edges");
                        }
                    }
                }
            }
        }

        // feet hang over the floor ahead: capped at their own tile's front faces, which the floor ahead ties, they still win
        [Fact]
        public void FeetStillBeatTheFloorAhead()
        {
            const int px = 1453, py = 1668, z = 10;
            var (ax, ay) = GeoDepth.MobileAnchor(px, py, z);

            foreach (var (tx, ty) in new[] { (px, py), (px + 1, py), (px, py + 1), (px + 1, py + 1), (px + 1, py - 1), (px - 1, py + 1) })
            {
                for (int dx = -33; dx <= 33; dx++)
                {
                    for (int row = 0; row <= 20; row++)
                    {
                        float sx = ax + dx, sy = ay + row;

                        if (!GeoDepth.OnShape(tx, ty, z, 0, false, true, sx, sy))
                        {
                            continue;
                        }

                        GeoDepth.MobileBiased(ax, ay, z, sx, sy).Should().BeGreaterThan(GeoDepth.Land(tx, ty, z, sx, sy), $"land {tx},{ty} at ({dx}, +{row})");
                        GeoDepth.MobileBiased(ax, ay, z, sx, sy).Should().BeGreaterThan(GeoDepth.Static(tx, ty, z, 0, false, true, false, sx, sy) + 7 * GeoDepth.RankBias,
                            $"floor {tx},{ty} at ({dx}, +{row})");
                    }
                }
            }
        }

        // wet flats (wet_surface_depth) keep their plane less WetDrop, with no floor
        [Fact]
        public void WetFlatIsUnfloored()
        {
            const int tx = 1675, ty = 1706, z = -5;
            var (nx, ny) = GeoDepth.NorthVertex(tx, ty, z);
            GeoDepth.Static(tx, ty, z, 0, false, true, false, nx, ny + 1, wet: true).Should().Be(GeoDepth.Plane(ny + 1, z) - GeoDepth.WetDrop + z * GeoDepth.ZBias);
        }

        [Fact]
        public void LegacyFlatDropIsGeoDepths()
        {
            LegacyGeo.FlatDrop.Should().Be(GeoDepth.FlatDrop);
        }
    }
}
