// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using TileMap = ClassicUO.Game.Map.Map;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// mobile_wall_order: the art studio's south-east corner (Felucca 1453 1668 z 10, clip-1 design note section 2): the player
    /// stands inside, the south wall 0x0034 on 1453 1669 (a line along x: left-half art), the east wall 0x0035 on 1454 1668 (a
    /// line along y: right-half art) and the corner 0x0033 on 1454 1669, all z 10 height 20. The feet strip (depth + 3, key
    /// 312488) beat all three (312338, 312338, 312438); with the rule a strip over the corner only takes 312437, a wider one
    /// over the south or east wall too 312337. Sprite trims as in the pack (classic-1.6).
    /// </summary>
    [Collection(nameof(DepthModelSwitchCollection))]
    public sealed class MobileWallOrderTests : IDisposable
    {
        private const int MX = 1453, MY = 1668, Z = 10;
        private const ushort South = 0x0034, East = 0x0035, Corner = 0x0033, Floor = 0x0500;

        private readonly StaticTiles[] _savedStaticData;

        public MobileWallOrderTests()
        {
            var data = new StaticTiles[0x10000];

            foreach (ushort g in new[] { South, East, Corner })
            {
                data[g].Flags = TileFlag.Wall | TileFlag.Impassable;
                data[g].Height = 20;
            }

            data[Floor].Flags = TileFlag.Surface | TileFlag.Background;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;

            // the walls' art (pack trims): the south wall's on the left half, the east wall's on the right half, the corner on both
            var sprites = new SpriteEntry[PackConst.StaticSpriteBase + 0x1000];
            sprites[PackConst.StaticSpriteBase + South] = Art(0, 27);
            sprites[PackConst.StaticSpriteBase + East] = Art(18, 26);
            sprites[PackConst.StaticSpriteBase + Corner] = Art(0, 44);
            MobileWallOrder.SpritesForTests = sprites;
        }

        private static SpriteEntry Art(short trimX, ushort w) => new SpriteEntry { Page = 0, TrimX = trimX, W = w, H = 90, OrigW = 44, OrigH = 90 };

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            MobileWallOrder.OverrideForTests = null;
            MobileWallOrder.SpritesForTests = null;
            DepthModel.OverrideForTests = null;
        }

        private static void Add(TileMap map, ushort graphic, int x, int y, int z)
        {
            Chunk chunk = map.GetChunk(x, y, false) ?? throw new InvalidOperationException("no chunk");
            var st = new Static(chunk.Owner) { Graphic = graphic, Z = (sbyte)z, X = (ushort)x, Y = (ushort)y };
            chunk.AddGameObject(st, x & 7, y & 7);
        }

        private static TileMap Studio(bool south = true, bool east = true, bool corner = true)
        {
            var world = new World();
            var map = new TileMap(world, 0, 200, 220);

            for (int cx = (MX - 1) >> 3; cx <= (MX + 3) >> 3; cx++)
            {
                for (int cy = (MY - 1) >> 3; cy <= (MY + 3) >> 3; cy++)
                {
                    map.LoadEmptyChunkForTest(cx, cy);
                }
            }

            if (south)
            {
                Add(map, South, MX, MY + 1, Z);
            }

            if (east)
            {
                Add(map, East, MX + 1, MY, Z);
            }

            if (corner)
            {
                Add(map, Corner, MX + 1, MY + 1, Z);
            }

            return map;
        }

        private static MobileWallOrder.Scan ScanAt(TileMap map, int x, int y, int z)
        {
            MobileWallOrder.Scan s = default;
            s.Active = true;
            MobileWallOrder.ScanAt(map, x, y, z, ref s);

            return s;
        }

        // the mobile's depth (View.CalculateDepthZ, PriorityZ z + 1) and the strip keys (MobileSpriteExtract.DepthOf)
        private static float MobileDepth(int x, int y, int z) => (x + y) + (127 + z + 1) * 0.01f;

        private static int Key(float depth, float add) => (int)MathF.Round((depth + 1f + add) * 100f) - 50;

        [Fact]
        public void StudioWallsAreFoundWithTheirFrontHalves()
        {
            MobileWallOrder.Scan s = ScanAt(Studio(), MX, MY, Z);
            s.Count.Should().Be(3);
            var walls = Enumerable.Range(0, s.Count).Select(i => s.Walls[i]).ToArray();
            walls.Should().ContainSingle(w => w.Graphic == East && w.D == 312338 && w.Lo == 22 + MobileWallOrder.VertexMargin && w.Hi == 44);
            walls.Should().ContainSingle(w => w.Graphic == South && w.D == 312338 && w.Lo == -44 && w.Hi == -22 - MobileWallOrder.VertexMargin);
            walls.Should().ContainSingle(w => w.Graphic == Corner && w.D == 312438 && w.Lo == -22 && w.Hi == 22);
        }

        [Fact]
        public void FeetStripTakesTheKeyJustBehindTheWall()
        {
            MobileWallOrder.Scan s = ScanAt(Studio(), MX, MY, Z);
            float depth = MobileDepth(MX, MY, Z);
            Key(depth, 0).Should().Be(312288, "strip 0 (design note)");
            Key(depth, 2).Should().Be(312488, "strip 1 before the rule");

            // feet (22 px wide) overlap only the corner's art
            float add = MobileWallOrder.CapAdd(2, in s, -11, 11, depth, out int wall);
            Key(depth, add).Should().Be(312437);
            s.Walls[wall].Graphic.Should().Be(Corner);
            Key(depth, add).Should().BeGreaterThan(Key(depth, 0), "never behind the upper strip");

            // a mount's hooves (66 px wide) reach the south and east walls' art too
            add = MobileWallOrder.CapAdd(2, in s, -33, 33, depth, out wall);
            Key(depth, add).Should().Be(312337);
            s.Walls[wall].D.Should().Be(312338);

            // a third strip (+ 4) is capped the same way
            Key(depth, MobileWallOrder.CapAdd(4, in s, -33, 33, depth, out _)).Should().Be(312337);
        }

        [Fact]
        public void KeyMathHoldsAcrossTheMap()
        {
            // the float round trip gives D - 1 exactly for large tile sums (Felucca's far corner, z extremes)
            foreach ((int x, int y, int z) in new[] { (0, 0, 0), (1453, 1668, 10), (5100, 4090, -120), (6143, 4095, 120), (7000, 4000, 60) })
            {
                MobileWallOrder.Scan s = default;
                s.Active = true;
                s.Count = 1;
                s.Walls[0] = new MobileWallOrder.Wall { D = 100 * (x + y + 1) + 127 + z + 1, Lo = -100, Hi = 100 };
                float depth = MobileDepth(x, y, z);
                Key(depth, MobileWallOrder.CapAdd(2, in s, -5, 5, depth, out _)).Should().Be(s.Walls[0].D - 1, $"{x},{y},{z}");
            }
        }

        [Fact]
        public void CapNeverRaisesNorDropsBelowTheUpperStrip()
        {
            float depth = MobileDepth(MX, MY, Z);
            MobileWallOrder.Scan s = default;
            s.Count = 1;

            // a wall key far ahead: + 2 stays
            s.Walls[0] = new MobileWallOrder.Wall { D = 312338 + 1000, Lo = -100, Hi = 100 };
            MobileWallOrder.CapAdd(2, in s, -5, 5, depth, out _).Should().Be(2);

            // a wall key under the upper strip's: the strip keeps the upper strip's + 1
            s.Walls[0].D = 312200;
            MobileWallOrder.CapAdd(2, in s, -5, 5, depth, out _).Should().Be(0);

            // no column overlap: unchanged
            s.Walls[0] = new MobileWallOrder.Wall { D = 312338, Lo = -20, Hi = 0 };
            MobileWallOrder.CapAdd(2, in s, 0, 5, depth, out int wall).Should().Be(2);
            s.Walls[0].Lo = 6;
            s.Walls[0].Hi = 20;
            MobileWallOrder.CapAdd(2, in s, -5, 6, depth, out wall).Should().Be(2);
            wall.Should().Be(-1);
        }

        [Theory]
        [InlineData(10, 3)] // standing on the storey's floor, inside the walls
        [InlineData(29, 3)] // still inside
        [InlineData(30, 0)] // on the walls' top (the next storey): not inside
        [InlineData(9, 0)] // under their base
        public void WallsHoldTheMobilesZ(int mobileZ, int expected)
        {
            ScanAt(Studio(), MX, MY, mobileZ).Count.Should().Be((byte)expected);
        }

        [Fact]
        public void FloorsAndWallsBehindOrBesideDoNotCount()
        {
            var map = Studio(false, false, false);
            Add(map, Floor, MX + 1, MY, Z); // a floor ahead
            Add(map, South, MX, MY, Z); // a wall on the mobile's own tile
            Add(map, East, MX, MY - 1, Z); // and one behind
            Add(map, South, MX + 1, MY - 1, Z); // beside (same diagonal)
            Add(map, East, MX + 3, MY, Z); // three diagonals ahead (+ 2i loses to it already)
            ScanAt(map, MX, MY, Z).Count.Should().Be(0);
        }

        [Fact]
        public void WallLineThroughTheMobilesTileIsNotInFront()
        {
            // the mobile in a doorway of a wall line along x (pieces 0x0034, left-half art, faces on Y = y + 1): the pieces on
            // (1, 0) and (2, 0) continue the line past its own tile's front edge (Felucca 1448 1653, bench-001056 door_on)
            var map = Studio(false, false, false);
            Add(map, South, MX + 1, MY, Z);
            Add(map, South, MX + 2, MY, Z);
            ScanAt(map, MX, MY, Z).Count.Should().Be(0);

            // the mirror: a wall line along y (0x0035, right-half art) through the mobile's tile, pieces on (0, 1) and (0, 2)
            map = Studio(false, false, false);
            Add(map, East, MX, MY + 1, Z);
            Add(map, East, MX, MY + 2, Z);
            ScanAt(map, MX, MY, Z).Count.Should().Be(0);

            // the same pieces one row further are in front: the south wall line one tile south of the mobile
            map = Studio(false, false, false);
            Add(map, South, MX + 1, MY + 1, Z);
            ScanAt(map, MX, MY, Z).Count.Should().Be(1);
        }

        [Theory]
        [InlineData(0f, 0f, 0, 0)]
        [InlineData(1f, -1f, 0, 0)] // north
        [InlineData(1f, 0f, 1, 0)] // north-east
        [InlineData(1f, 1f, 1, 0)] // east
        [InlineData(0f, 1f, 1, 1)] // south-east
        [InlineData(-1f, 1f, 0, 1)] // south
        [InlineData(-1f, 0f, 0, 1)] // south-west
        [InlineData(-1f, -1f, 0, 0)] // west
        [InlineData(0f, -1f, 0, 0)] // north-west
        public void DepthTileFollowsCalculateDepthZ(float ox, float oy, int dx, int dy)
        {
            MobileWallOrder.DepthTile(MX, MY, new Vector3(ox, oy, 0), out int bx, out int by);
            bx.Should().Be(MX + dx);
            by.Should().Be(MY + dy);
        }

        [Theory]
        [InlineData(false, false, false)] // switch off
        [InlineData(true, false, true)]
        [InlineData(true, true, false)] // depth_model on: its own per-pixel walls
        public void KeyScanBySwitch(bool on, bool depthModel, bool expected)
        {
            // the rule's state only: the tile scan itself is ScanAt's (the test world has no map)
            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0010_0001);
            m.X = MX;
            m.Y = MY;
            m.Z = Z;
            MobileWallOrder.OverrideForTests = on;
            DepthModel.OverrideForTests = depthModel;
            MobileWallOrder.Scan s = MobileWallOrder.KeyScan(m);
            s.Active.Should().Be(expected);
            s.Count.Should().Be(0);
        }

        [Fact]
        public void ScanIsPartOfTheSelectKey()
        {
            var a = new MobileFrameCache.SelectKey { X = MX, Y = MY, Z = Z };
            var b = a;
            b.Wall = ScanAt(Studio(), MX, MY, Z);
            a.Equals(b).Should().BeFalse("a wall appearing re-records the strips");
            var c = b;
            c.Wall.Active = false;
            b.Equals(c).Should().BeFalse("a flip of the rule re-records the strips");
        }

        // ---- the extract: both switch states ----

        private static uint[] Depths(string name, MobileWallOrder.Scan scan, int seed, bool submerged = false)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = DynamicSpriteTests.AnimFixture(seed);
            var mirror = new WorldMirror();
            EcsEntity e = DynamicSpriteTests.MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);
            DepthModel.OverrideForTests = false;
            extract.Begin(in tables, 37, -12);
            extract.Emit(e, 600, 400, 812.25f, new MobileFrameCache { Wall = scan, Submerged = submerged });

            return extract.Instances.ToArray().Select(i => i.Depth).ToArray();
        }

        // a wall over every column, its key between the upper strip's and the feet strip's
        private static MobileWallOrder.Scan Everywhere(bool active)
        {
            MobileWallOrder.Scan s = default;
            s.Active = active;
            s.Count = 1;
            s.Walls[0] = new MobileWallOrder.Wall { D = 81375, Lo = -10000, Hi = 10000 };

            return s;
        }

        private static uint KeyOf(float depth) => BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, 1000, 7);

        [Theory]
        [InlineData("plain")]
        [InlineData("mounted")]
        [InlineData("equipped")]
        public void SwitchOffDrawsAsBefore(string name)
        {
            for (int seed = 1; seed <= 20; seed++)
            {
                Depths(name, Everywhere(false), seed).Should().Equal(Depths(name, default, seed), $"seed {seed}");
            }
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("mounted")]
        [InlineData("equipped")]
        public void SwitchOnCapsOnlyTheStripsBelowTheAnchor(string name)
        {
            uint shadow = KeyOf(812.25f), upper = KeyOf(812.25f + 1f);
            uint capped = BakeJob.DepthKeyD(81374, 1000, 7);
            int changed = 0;

            for (int seed = 1; seed <= 40; seed++)
            {
                uint[] off = Depths(name, default, seed);
                uint[] on = Depths(name, Everywhere(true), seed);
                on.Length.Should().Be(off.Length);

                for (int i = 0; i < on.Length; i++)
                {
                    if (off[i] == shadow || off[i] == upper)
                    {
                        on[i].Should().Be(off[i], $"seed {seed} instance {i}: the shadow and the upper strip keep their keys");
                    }
                    else
                    {
                        on[i].Should().Be(capped, $"seed {seed} instance {i}");
                        changed++;
                    }
                }
            }

            changed.Should().BeGreaterThan(0, "some frames have a feet strip");
        }

        [Fact]
        public void SubmergedAndSeatedMobilesAreUnchanged()
        {
            for (int seed = 1; seed <= 10; seed++)
            {
                Depths("plain", Everywhere(true), seed, true).Should().Equal(Depths("plain", default, seed, true));
                Depths("sitting", Everywhere(true), seed).Should().Equal(Depths("sitting", default, seed));
            }
        }
    }
}
