// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// clip-2: items (and captured multis/statics) carry the baked statics' shape encoding (SpriteInstance.GeoTile) only with
    /// depth_model on; off, their records and instances are those of the build before the switch.
    /// </summary>
    [Collection(nameof(DepthModelSwitchCollection))]
    public class DepthModelItemTests
    {
        private const int TX = 1500, TY = 1600;
        private const sbyte TZ = 10;
        private const int OFFX = 15, OFFY = -40;

        // ItemView's screen position of tile (TX, TY, TZ) less the draw offset: the untrimmed art is centred on
        // (tx - ty)·22 and ends at (tx + ty)·22 - 4z + 22 (SpriteInstance.Tile)
        private static (int X, int Y) Pos(int z = TZ) => ((TX - TY) * 22 - 22 - OFFX, (TX + TY) * 22 - 4 * z - 22 - OFFY);

        private static ItemSpriteInput Case(string name, byte height = 6, byte bits = 0)
        {
            ItemSpriteInput i = DynamicSpriteTests.ItemCase(name);
            i.X = TX;
            i.Y = TY;
            i.Z = TZ;
            i.TileHeight = height;
            i.TileGeoBits = bits;

            return i;
        }

        private static (SpriteInstance[] Emitted, DynamicSprite[] Selected) Run(bool on, in ItemSpriteInput input)
        {
            (ArtLoader arts, GpuSpriteTables tables) = DynamicSpriteTests.ArtFixture();
            var extract = new ItemSpriteExtract(arts, null);
            (int px, int py) = Pos();

            try
            {
                DepthModel.OverrideForTests = on;
                extract.Emit(in input, in tables, OFFX, OFFY, px, py, 455.5f).Should().BeTrue();
                SpriteInstance[] emitted = extract.Instances.ToArray();
                extract.Select(in input, false, px, py, 455.5f).Should().BeTrue();

                return (emitted, extract.Selected.ToArray());
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }

        [Theory]
        [MemberData(nameof(DynamicSpriteTests.ItemCases), MemberType = typeof(DynamicSpriteTests))]
        public void Shape_Is_Written_Only_With_The_Switch_On(string name)
        {
            ItemSpriteInput input = Case(name);
            (SpriteInstance[] off, DynamicSprite[] offSel) = Run(false, in input);
            off.Should().NotBeEmpty();

            foreach (SpriteInstance i in off)
            {
                i.IsGeoTile.Should().BeFalse();
                (i.Light >> 16).Should().Be(0u, "the default path leaves Light's upper bits as before");
                i.Corners.Should().Be(0u);
            }

            foreach (DynamicSprite s in offSel)
            {
                s.GeoTile.Should().BeFalse();
                s.Geo.Should().BeFalse();
            }

            (SpriteInstance[] on, DynamicSprite[] onSel) = Run(true, in input);
            on.Length.Should().Be(off.Length);

            for (int k = 0; k < on.Length; k++)
            {
                SpriteInstance i = on[k];
                i.IsGeoTile.Should().BeTrue();
                i.Tile(out int tx, out int ty);
                (tx, ty).Should().Be((TX, TY), "the shader finds the item's tile as a baked static's");
                ((sbyte)i.Corners).Should().Be(TZ);
                ((i.Corners >> 8) & 0xFF).Should().Be(6u);

                // everything else as off
                SpriteInstance o = off[k];
                (i.X, i.Y, i.Whf, i.Uv, i.PageHue, i.Depth, i.Light & 0xFFFF).Should().Be((o.X, o.Y, o.Whf, o.Uv, o.PageHue, o.Depth, o.Light & 0xFFFF));
            }

            onSel.Should().OnlyContain(s => s.GeoTile && s.GeoTx == TX && s.GeoTy == TY && s.GeoZ == TZ);
        }

        [Fact]
        public void Shape_Bits_Follow_The_Tiledata()
        {
            ItemSpriteInput input = Case("plain", 20, (byte)SpriteInstance.ZGeoWall);
            (SpriteInstance[] on, _) = Run(true, in input);
            ((on[0].Corners >> 16) & 0x7F).Should().Be(SpriteInstance.ZGeoWall);
            ((on[0].Corners >> 8) & 0xFF).Should().Be(20u);

            SpriteInstance.GeoShapeBits(true, false, false, 20).Should().Be(SpriteInstance.ZGeoWall);
            SpriteInstance.GeoShapeBits(false, true, false, 0).Should().Be(SpriteInstance.ZGeoFlat);
            SpriteInstance.GeoShapeBits(false, false, true, 0).Should().Be(SpriteInstance.ZGeoFlat);
            SpriteInstance.GeoShapeBits(false, false, true, 3).Should().Be(0u, "a surface with height is a box");
            SpriteInstance.GeoShapeBits(false, false, false, 0).Should().Be(0u, "small items (height 0, not floor) are boxes");
        }

        [Fact]
        public void GeoTile_Refuses_Instances_Whose_Upper_Bits_Are_In_Use()
        {
            var lit = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, SpriteInstance.FlagLit) };
            lit.GeoTile(1, 2, 3, 4, 0).Should().BeFalse();
            var baked = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, SpriteInstance.FlagZTest) };
            baked.GeoTile(1, 2, 3, 4, 0).Should().BeFalse();
            var indexed = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, SpriteInstance.FlagIndexed), Corners = 77 };
            indexed.GeoTile(1, 2, 3, 4, 0).Should().BeFalse();
            indexed.Corners.Should().Be(77u);
            var rotated = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, 0), Light = SpriteInstance.LightRotated | 0x12340000u };
            rotated.GeoTile(1, 2, 3, 4, 0).Should().BeFalse();
            var wobble = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, 0), Light = SpriteInstance.WobbleBits(2, 3) };
            wobble.GeoTile(1, 2, 3, 4, 0).Should().BeFalse();

            var plain = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, SpriteInstance.FlagHued), Light = SpriteInstance.LightCot | 40 };
            plain.GeoTile(1, 2, -3, 4, SpriteInstance.ZGeoFlat).Should().BeTrue();
            (plain.Light & 0xFFFF).Should().Be(SpriteInstance.LightCot | 40, "alpha and the circle of transparency stay");
            plain.IsGeoTile.Should().BeTrue();
            ((sbyte)plain.Corners).Should().Be(-3);

            // a mobile layer's anchor is not a tile shape
            var mob = new SpriteInstance { Whf = SpriteInstance.PackWhf(4, 4, 0) };
            mob.GeoMobile(-2048, 2047, 0).Should().BeTrue();
            mob.IsGeoTile.Should().BeFalse();
        }

        // what the geo VS gives an item: a box over its tile, as a static's (GeoDepth.Static); a coin pile on a floor beats it
        // and loses to the walls on its tile's front edges
        [Fact]
        public void Item_On_A_Floor_Beats_The_Floor_And_Loses_To_The_Wall_Ahead()
        {
            var (nx, ny) = GeoDepth.NorthVertex(TX, TY, TZ);

            for (int col = -20; col <= 20; col += 4)
            {
                float sx = nx + col, sy = ny + 22;
                float item = GeoDepth.Static(TX, TY, TZ, 0, false, false, false, sx, sy);
                float floor = GeoDepth.Static(TX, TY, TZ, 0, false, true, false, sx, sy);
                item.Should().BeGreaterThan(floor, $"column {col}");

                // the walls on the tile's front edges are the back edges of the tiles south and east
                float wall = Math.Max(GeoDepth.Static(TX + 1, TY, TZ, 20, true, false, false, sx, sy),
                    GeoDepth.Static(TX, TY + 1, TZ, 20, true, false, false, sx, sy));
                wall.Should().BeGreaterThan(item, $"column {col}");
            }
        }

        // the highest rank bias the geo VS can add to a box (inst.depth & 7)
        private const float MaxRank = 7 * GeoDepth.RankBias;

        // clip-3, bench6-000027: the player at Felucca 1451,1666 z 10 stood in item 0x0F6A (height 12) on its own tile and the
        // box's front faces covered it on Gpu. A box on the mobile's own tile whose base is at or under the mobile's z must
        // not beat any pixel of the mobile (body above the anchor, feet below it), as the depth key draws it.
        [Theory]
        [InlineData(0, 12)]
        [InlineData(0, 1)]
        [InlineData(0, 40)]
        [InlineData(-2, 12)]
        [InlineData(-6, 6)]
        [InlineData(-12, 12)]
        [InlineData(-4, 2)]
        public void Box_On_The_Mobiles_Own_Tile_At_Or_Under_Its_Z_Does_Not_Hide_It(int dz, int height)
        {
            int boxZ = TZ + dz;
            var (ax, ay) = GeoDepth.MobileAnchor(TX, TY, TZ);
            int sampled = 0;

            for (int dx = -22; dx <= 22; dx += 2)
            {
                for (int row = -80; row <= 6; row += 2)
                {
                    float sx = ax + dx, sy = ay + row;

                    if (!GeoDepth.OnShape(TX, TY, boxZ, height, false, false, sx, sy))
                    {
                        continue;
                    }

                    sampled++;
                    float box = GeoDepth.Static(TX, TY, boxZ, height, false, false, false, sx, sy) + MaxRank;
                    float mob = GeoDepth.MobileBiased(ax, ay, TZ, sx, sy);
                    mob.Should().BeGreaterThan(box, $"dx {dx} row {row}");
                }
            }

            sampled.Should().BeGreaterThan(0);
        }

        // the same box on a neighbouring tile ahead (south-east, south-west, south) still hides the mobile's lower body where
        // it covers it, and the walls on the mobile tile's front edges still beat the box on its own tile
        [Theory]
        [InlineData(1, 0)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        public void Box_On_A_Neighbouring_Tile_Ahead_Still_Hides_The_Lower_Body(int ddx, int ddy)
        {
            var (ax, ay) = GeoDepth.MobileAnchor(TX, TY, TZ);
            int sampled = 0;

            for (int dx = -16; dx <= 16; dx += 2)
            {
                for (int row = -20; row <= 4; row += 2)
                {
                    float sx = ax + dx, sy = ay + row;

                    if (!GeoDepth.OnShape(TX + ddx, TY + ddy, TZ, 12, false, false, sx, sy))
                    {
                        continue;
                    }

                    sampled++;
                    float box = GeoDepth.Static(TX + ddx, TY + ddy, TZ, 12, false, false, false, sx, sy);
                    float mob = GeoDepth.MobileBiased(ax, ay, TZ, sx, sy);
                    box.Should().BeGreaterThan(mob, $"dx {dx} row {row}");
                }
            }

            sampled.Should().BeGreaterThan(0);
        }

        [Fact]
        public void Walls_On_The_Own_Tiles_Front_Edges_Still_Beat_A_Box_On_It()
        {
            var (nx, ny) = GeoDepth.NorthVertex(TX, TY, TZ);

            for (int col = -22; col <= 22; col += 2)
            {
                for (int row = -48; row <= 44; row += 4)
                {
                    float sx = nx + col, sy = ny + row;

                    if (!GeoDepth.OnShape(TX, TY, TZ, 12, false, false, sx, sy))
                    {
                        continue;
                    }

                    float box = GeoDepth.Static(TX, TY, TZ, 12, false, false, false, sx, sy) + MaxRank;
                    float wall = col <= 0
                        ? GeoDepth.Static(TX, TY + 1, TZ, 20, true, false, false, sx, sy)
                        : GeoDepth.Static(TX + 1, TY, TZ, 20, true, false, false, sx, sy);
                    wall.Should().BeGreaterThan(box, $"column {col} row {row}");
                }
            }
        }

        // a box hanging well above the mobile on its own tile (base more than MobileBias / ZBias z higher) keeps its front
        // faces' place over the mobile's billboard: the rule only covers boxes the mobile stands on or in
        [Fact]
        public void Box_Well_Above_The_Mobile_On_Its_Own_Tile_Still_Wins()
        {
            var (ax, ay) = GeoDepth.MobileAnchor(TX, TY, TZ);
            int boxZ = TZ + 20;
            var (nx, ny) = GeoDepth.NorthVertex(TX, TY, boxZ);

            for (int col = -16; col <= 16; col += 4)
            {
                float sx = nx + col, sy = ny + 40 - Math.Abs(col);
                float box = GeoDepth.Static(TX, TY, boxZ, 10, false, false, false, sx, sy);
                float mob = GeoDepth.MobileBiased(ax, ay, TZ, sx, sy);
                box.Should().BeGreaterThan(mob, $"column {col}");
            }
        }
    }
}
