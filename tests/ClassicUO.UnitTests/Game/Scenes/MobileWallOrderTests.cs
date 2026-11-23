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
        private const ushort South = 0x0034, East = 0x0035, Corner = 0x0033, Floor = 0x0500, Table = 0x0B3E, Easel = 0x0F66, Stairs = 0x0736, Roof = 0x05A2;

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
            // tiledata as in the pack: a counter, an easel, stone stairs
            data[Table].Flags = TileFlag.Impassable | TileFlag.Surface;
            data[Table].Height = 6;
            data[Easel].Flags = TileFlag.Impassable;
            data[Easel].Height = 12;
            data[Stairs].Flags = TileFlag.Impassable | TileFlag.Surface | TileFlag.Bridge | TileFlag.StairRight;
            data[Stairs].Height = 5;
            data[Roof].Flags = TileFlag.Roof | TileFlag.Impassable;
            data[Roof].Height = 3;
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = data;

            // the walls' art (pack trims): the south wall's on the left half, the east wall's on the right half, the corner on both
            var sprites = new SpriteEntry[PackConst.StaticSpriteBase + 0x1000];
            sprites[PackConst.StaticSpriteBase + South] = Art(0, 27);
            sprites[PackConst.StaticSpriteBase + East] = Art(18, 26);
            sprites[PackConst.StaticSpriteBase + Corner] = Art(0, 44);
            sprites[PackConst.StaticSpriteBase + Table] = Art(0, 44);
            sprites[PackConst.StaticSpriteBase + Easel] = Art(8, 30);
            sprites[PackConst.StaticSpriteBase + Stairs] = Art(0, 44);
            sprites[PackConst.StaticSpriteBase + Roof] = Art(0, 44);
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
        public void ScanAndCapDoNotAllocate()
        {
            var map = Studio();
            float depth = MobileDepth(MX, MY, Z);
            MobileWallOrder.Scan s = default;
            float warm = 0;

            // the same calls once first (type initialisers, first-call stubs)
            for (int i = 0; i < 2; i++)
            {
                s = default;
                MobileWallOrder.ScanAt(map, MX, MY, Z, ref s);
                warm += MobileWallOrder.CapAdd(2, in s, -33, 33, depth, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            float sum = 0;

            for (int i = 0; i < 1000; i++)
            {
                s = default;
                MobileWallOrder.ScanAt(map, MX, MY, Z, ref s);
                sum += MobileWallOrder.CapAdd(2, in s, -33, 33, depth, out _);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
            sum.Should().BeGreaterThan(0);
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
        [InlineData(30, 0)] // on the walls' top (the next storey): above them
        [InlineData(9, 3)] // under their base: the walls ahead still cover it (owner rule, round 2)
        [InlineData(-20, 0)] // 30 z under them: their art ends above the rows of its lower strips (MinBottom)
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

        [Fact]
        public void BoxesCountOverTheirWholeDiamond()
        {
            // a counter on (1, 0): its west face (X = x + 1) is in front of the mobile's right half, so the whole diamond counts,
            // unlike a wall there (only the half with its face)
            var map = Studio(false, false, false);
            Add(map, Table, MX + 1, MY, Z);
            MobileWallOrder.Scan s = ScanAt(map, MX, MY, Z);
            s.Count.Should().Be(1);
            s.Walls[0].Lo.Should().Be(0);
            s.Walls[0].Hi.Should().Be(44);
            float depth = MobileDepth(MX, MY, Z);
            Key(depth, MobileWallOrder.CapAdd(2, in s, -11, 11, depth, out _)).Should().Be(s.Walls[0].D - 1);

            // an easel on (0, 1), cut to its art's columns ((44 >> 1) = 22 from the vertex: -22 + 8 - 22 .. + 30)
            map = Studio(false, false, false);
            Add(map, Easel, MX, MY + 1, Z);
            s = ScanAt(map, MX, MY, Z);
            s.Count.Should().Be(1);
            s.Walls[0].Lo.Should().Be(-36);
            s.Walls[0].Hi.Should().Be(-6);
        }

        [Theory]
        [InlineData(Z, 1)] // an NPC behind the counter, at its base
        [InlineData(Z + 5, 0)] // the counter top (z + 6) within its climb limit: it could step onto it
        [InlineData(Z + 6, 0)] // standing on it
        [InlineData(Z - 1, 1)] // under it: still covered
        public void BoxesHoldTheMobilesZ(int mobileZ, int expected)
        {
            var map = Studio(false, false, false);
            Add(map, Table, MX + 1, MY + 1, Z);
            ScanAt(map, MX, MY, mobileZ).Count.Should().Be((byte)expected);
        }

        [Fact]
        public void FlatsAndPiecesBelowTheMobileNeverCount()
        {
            // a rug ahead, and stairs going down ahead (tops at or under the mobile's z): the mobile is drawn over them
            var map = Studio(false, false, false);
            Add(map, Stairs, MX + 1, MY, Z - 5);
            Add(map, Stairs, MX, MY + 1, Z - 10);
            Add(map, Floor, MX + 1, MY + 1, Z);
            ScanAt(map, MX, MY, Z).Count.Should().Be(0);

            // the first step rising ahead is one the mobile can climb (the pathfinder's limit): a floor, its hooves over it
            Add(map, Stairs, MX + 1, MY + 1, Z);
            ScanAt(map, MX, MY, Z).Count.Should().Be(0);

            // a step further up, or a stair block standing from the ground well above the limit, covers like any piece
            Add(map, Stairs, MX + 2, MY, Z + Pathfinder.ClimbLimit + 1);
            ScanAt(map, MX, MY, Z).Count.Should().Be(1);
        }

        // the entries that cover the mobile (not the stairs under it, not the shadow-only pieces behind it)
        private static int Covers(in MobileWallOrder.Scan s)
        {
            int n = 0;

            for (int i = 0; i < s.Count; i++)
            {
                if (!s.Walls[i].Floor && !s.Walls[i].ShadowOnly)
                {
                    n++;
                }
            }

            return n;
        }

        [Fact]
        public void OnStairsTheRunAndWhatLiesOnItAreFloor()
        {
            // stone stairs rising east to west, one step (h 5, bridge) per tile; the mobile stands on the step at z 30 at its
            // half height (z 32, the pathfinder's walkable top): it stands up to 35 (StandTop)
            var map = Studio(false, false, false);

            for (int k = -2; k <= 2; k++)
            {
                Add(map, Stairs, MX + k, MY, 30 - 5 * k);
            }

            MobileWallOrder.StandTop(map, MX, MY, 32).Should().Be(35);
            MobileWallOrder.StandTop(map, MX, MY, 30).Should().Be(30, "not standing at a stair's walkable top");

            // facing down (east): the lower steps ahead are under it; a box on the next lower step (z 25, top 28) too
            Add(map, Table, MX + 1, MY + 1, 25);
            MobileWallOrder.Scan s = default;
            MobileWallOrder.ScanStep(map, MX, MY, 32, Vector3.Zero, MX, MY, ref s);
            Covers(in s).Should().Be(0);

            // a staircase rising ahead (south-east), one step per tile: the run is floor, two tiles out too
            map = Studio(false, false, false);
            Add(map, Stairs, MX, MY, 30);
            Add(map, Stairs, MX + 1, MY, 35);
            Add(map, Stairs, MX + 2, MY, 40);
            Add(map, Stairs, MX + 1, MY + 1, 40);
            s = default;
            MobileWallOrder.ScanStep(map, MX, MY, 32, Vector3.Zero, MX, MY, ref s);
            Covers(in s).Should().Be(0);

            // a block standing far above the run still covers
            Add(map, Easel, MX, MY + 1, 45);
            s = default;
            MobileWallOrder.ScanStep(map, MX, MY, 32, Vector3.Zero, MX, MY, ref s);
            Covers(in s).Should().Be(1);
        }

        [Fact]
        public void OnStairsTheStepBesideIsFloorItIsDrawnOver()
        {
            // round 3 (owner, Felucca 1376 1625 42, stairs facing east): the same step on the tile south of the mobile (one
            // diagonal ahead) out-keyed its upper strip (a mount's legs); now the strips rise one key over it
            var map = Studio(false, false, false);
            Add(map, Stairs, MX, MY, 40);
            Add(map, Stairs, MX, MY + 1, 40);
            MobileWallOrder.Scan s = default;
            s.Active = true;
            MobileWallOrder.ScanStep(map, MX, MY, 42, Vector3.Zero, MX, MY, ref s);
            s.Count.Should().Be(1);
            s.Walls[0].Floor.Should().BeTrue();

            float depth = MobileDepth(MX, MY, 42);
            Key(depth, 0).Should().BeLessThan(s.Walls[0].D, "before: the step beside beat the upper strip");
            MobileWallOrder.FloorAdd(in s, -11, 11, depth, out float add).Should().BeTrue();
            Key(depth, add).Should().Be(s.Walls[0].D + 1);
            MobileWallOrder.CapAdd(add, in s, -11, 11, depth, true, out _).Should().Be(add, "a floor piece never caps");

            // not standing on the staircase (on the land beside it): the step is not floor and covers as before
            map = Studio(false, false, false);
            Add(map, Stairs, MX, MY + 1, 40);
            s = default;
            MobileWallOrder.ScanStep(map, MX, MY, 30, Vector3.Zero, MX, MY, ref s);
            for (int i = 0; i < s.Count; i++)
            {
                s.Walls[i].Floor.Should().BeFalse();
            }
        }

        [Fact]
        public void ShadowLosesToTheWallBehind()
        {
            // round 3 (owner, Felucca 1453 1660 10): a wall north of the mobile (behind it) covers its shadow, not its strips
            var map = Studio(false, false, false);
            Add(map, South, MX, MY - 1, Z);
            MobileWallOrder.Scan s = default;
            s.Active = true;
            MobileWallOrder.ScanBehind(map, MX, MY, Z, 0, 0, ref s);
            s.Count.Should().Be(1);
            s.Walls[0].ShadowOnly.Should().BeTrue();
            float depth = MobileDepth(MX, MY, Z);
            MobileWallOrder.ShadowAdd(in s, -30, 30, depth, out float add, out _).Should().BeTrue();
            Key(depth, add).Should().Be(s.Walls[0].D - 1);
            MobileWallOrder.CapAdd(2, in s, -30, 30, depth, out _).Should().Be(2, "the strips ignore it");

            // a flat behind stays under the shadow
            map = Studio(false, false, false);
            Add(map, Floor, MX, MY - 1, Z);
            s = default;
            MobileWallOrder.ScanBehind(map, MX, MY, Z, 0, 0, ref s);
            s.Count.Should().Be(0);
        }

        [Fact]
        public void OverhangOverAPieceInFrontOfTheSideTileIsCapped()
        {
            // round 3 item C (owner, Felucca 1466 1670 0, a mount facing west): its head and chest hang over (x - 1, y); the table
            // on (x - 1, y + 1), the mobile's own diagonal, is in front of that tile and covers the overhang
            var map = Studio(false, false, false);
            Add(map, Table, MX - 1, MY + 1, Z);
            MobileWallOrder.Scan s = default;
            s.Active = true;
            MobileWallOrder.ScanStep(map, MX, MY, Z, Vector3.Zero, MX, MY, ref s);
            s.Count.Should().Be(1);
            s.Walls[0].Side.Should().BeTrue();
            s.Walls[0].Hi.Should().Be(-22, "only the columns past the anchor tile's half-width");
            float depth = MobileDepth(MX, MY, Z);

            // a wide mount's upper strip reaching left over it: capped one key behind the table, above its own floor
            float add = MobileWallOrder.CapAdd(0, in s, -33, 33, depth, true, out int wall);
            wall.Should().Be(0);
            Key(depth, add).Should().Be(s.Walls[0].D - 1);
            Key(depth, add).Should().BeGreaterThan(100 * (MX + MY) + 127 + Z - 1, "over its own tile's floor");

            // a narrow body (within the tile) is not touched
            MobileWallOrder.CapAdd(0, in s, -15, 15, depth, true, out _).Should().Be(0);

            // the table behind the mobile's other side (x + 1, y - 1) only counts for the right overhang
            map = Studio(false, false, false);
            Add(map, Table, MX + 1, MY - 1, Z);
            s = default;
            MobileWallOrder.ScanStep(map, MX, MY, Z, Vector3.Zero, MX, MY, ref s);
            s.Count.Should().Be(1);
            s.Walls[0].Lo.Should().Be(22);

            // a rug there, or a table under the mobile's z, never
            map = Studio(false, false, false);
            Add(map, Floor, MX - 1, MY + 1, Z);
            Add(map, Table, MX + 1, MY - 1, Z - 10);
            s = default;
            MobileWallOrder.ScanStep(map, MX, MY, Z, Vector3.Zero, MX, MY, ref s);
            s.Count.Should().Be(0);
        }

        [Theory]
        [InlineData(TileFlag.Surface | TileFlag.Impassable, 2, 0, true)] // a low platform: walkable top within the limit
        [InlineData(TileFlag.Surface | TileFlag.Impassable, 6, 0, false)] // a counter: top 6 above
        [InlineData(TileFlag.Surface | TileFlag.Bridge, 10, 0, true)] // the teleporter altar block (bridge base at the mobile's z)
        [InlineData(TileFlag.Surface | TileFlag.Bridge, 10, 3, false)] // a bridge whose base is above the limit
        [InlineData(TileFlag.StairRight, 5, 2, true)]
        [InlineData(TileFlag.Impassable, 12, -10, false)] // an easel standing lower: not walkable
        public void ClimbableFollowsThePathfinder(TileFlag flags, int height, int dz, bool expected)
        {
            var td = new StaticTiles { Flags = flags, Height = (byte)height };
            MobileWallOrder.Climbable(in td, Z + dz, Z).Should().Be(expected);
        }

        // the owner's shadow spot (Felucca 1453 1665): the studio's east wall line (right-half pieces, a line along y at x + 1)
        private static TileMap EastLine()
        {
            var map = Studio(false, false, false);

            for (int k = -3; k <= 2; k++)
            {
                Add(map, East, MX + 1, MY + k, Z);
            }

            return map;
        }

        [Fact]
        public void WallSegmentContinuesPastTheScannedTiles()
        {
            MobileWallOrder.Scan s = ScanAt(EastLine(), MX, MY, Z);
            var walls = Enumerable.Range(0, s.Count).Select(i => s.Walls[i]).ToArray();

            // (1, 0) and (1, 1) are scanned; (1, -1), (1, -2), (1, -3) and (1, 2) follow as one segment, with their face half
            walls.Select(w => w.Y - MY).Should().BeEquivalentTo(new[] { 0, 1, -1, -2, -3, 2 });
            walls.Should().ContainSingle(w => w.Y == MY - 1 && w.Lo == 50 && w.Hi == 66);
            walls.Should().ContainSingle(w => w.Y == MY - 2 && w.Lo == 72 && w.Hi == 88);
            walls.Should().OnlyContain(w => !w.Overhead);

            // a line stops where its pieces stop
            var map = Studio(false, false, false);
            Add(map, East, MX + 1, MY, Z);
            Add(map, East, MX + 1, MY - 2, Z);
            ScanAt(map, MX, MY, Z).Count.Should().Be(1);
        }

        [Fact]
        public void StandingStripsKeepTheirKeysAgainstTheSegmentBehindTheirDiagonal()
        {
            // the segment's pieces on the mobile's diagonal or behind it have lower keys; standing (MinAdd 0) a strip stays at
            // the upper strip's + 1 at most, which still beats them, as before
            MobileWallOrder.Scan s = ScanAt(EastLine(), MX, MY, Z);
            float depth = MobileDepth(MX, MY, Z);
            MobileWallOrder.CapAdd(0, in s, 50, 90, depth, true, out _).Should().Be(0);
            MobileWallOrder.CapAdd(2, in s, 50, 90, depth, out _).Should().Be(0);
        }

        [Fact]
        public void ShadowLosesToTheWallPiecesItOverlaps()
        {
            MobileWallOrder.Scan s = ScanAt(EastLine(), MX, MY, Z);
            float depth = MobileDepth(MX, MY, Z);

            // a mount's shadow cast north-east reaches columns 40..90: the lowest piece it overlaps is (1, -2)
            MobileWallOrder.ShadowAdd(in s, 40, 90, depth, out float add, out int wall).Should().BeTrue();
            s.Walls[wall].Y.Should().Be((ushort)(MY - 2));
            Key(depth, add).Should().Be(s.Walls[wall].D - 1);
            add.Should().BeLessThan(-1f, "below the shadow's base depth");

            // a shadow clear of the line keeps its base depth (kind 0)
            MobileWallOrder.ShadowAdd(in s, -30, 20, depth, out add, out _).Should().BeFalse();
            add.Should().Be(-1f);
        }

        [Theory]
        [InlineData(Z + 16, true)] // the roof's bottom at the mobile's top
        [InlineData(Z + 23, true)] // the owner's roof (z 23 over a mobile at z 0)
        [InlineData(Z + 15, false)] // lower: not above the mobile
        public void PieceOverheadCapsTheUpperStrip(int roofZ, bool capped)
        {
            var map = Studio(false, false, false);
            Add(map, Roof, MX, MY, roofZ);
            Add(map, Floor, MX, MY, Z); // the floor the mobile stands on
            Add(map, Table, MX + 1, MY - 1, Z); // furniture beside it on its diagonal
            MobileWallOrder.Scan s = default;
            s.Active = true;
            MobileWallOrder.ScanOverhead(map, MX, MY, Z, 0, 0, ref s);
            s.Count.Should().Be(capped ? (byte)1 : (byte)0);

            if (!capped)
            {
                return;
            }

            s.Walls[0].Overhead.Should().BeTrue();
            float depth = MobileDepth(MX, MY, Z);
            MobileWallOrder.OverheadSplit(in s, -11, 11, depth, 0, out int splitRow, out float add, out int wall).Should().BeTrue();
            wall.Should().Be(0);

            // the rows above the roof art's bottom (90 px art on the south vertex, 4 px per z above the mobile) take the cap
            splitRow.Should().Be(22 - 4 * (roofZ - Z));
            Key(depth, add).Should().Be(Math.Max(s.Walls[0].D - 1, Key(depth, MobileWallOrder.OverheadMinAdd)));
            Key(depth, add).Should().BeGreaterThan(100 * (MX + MY) + 127 + Z + 1, "still over the floor and the pieces at its z");

            // the strip rules ignore pieces overhead (the upper strip's other rows, the strips below the anchor)
            MobileWallOrder.CapAdd(0, in s, -11, 11, depth, true, out _).Should().Be(0);
            MobileWallOrder.CapAdd(2, in s, -11, 11, depth, out _).Should().Be(2);
        }

        [Fact]
        public void OverheadOnTheSameDiagonalCounts()
        {
            var map = Studio(false, false, false);
            Add(map, Roof, MX + 1, MY - 1, Z + 23);
            Add(map, Roof, MX + 1, MY, Z + 23); // the next diagonal's roof wins by key already: not scanned
            MobileWallOrder.Scan s = default;
            MobileWallOrder.ScanOverhead(map, MX, MY, Z, 0, 0, ref s);
            s.Count.Should().Be(1);
            s.Walls[0].X.Should().Be((ushort)(MX + 1));
            s.Walls[0].Lo.Should().Be(22);
            s.Walls[0].Hi.Should().Be(66);
        }

        [Theory]
        [InlineData(1f, -1f, 0, -1)] // north
        [InlineData(1f, 0f, 1, -1)] // north-east
        [InlineData(1f, 1f, 1, 0)] // east
        [InlineData(0f, 1f, 1, 1)] // south-east
        [InlineData(-1f, 1f, 0, 1)] // south
        [InlineData(-1f, 0f, -1, 1)] // south-west
        [InlineData(-1f, -1f, -1, 0)] // west
        [InlineData(0f, -1f, -1, -1)] // north-west
        [InlineData(0f, 0f, 0, 0)] // standing
        public void StepTileFollowsTheOffset(float ox, float oy, int dx, int dy)
        {
            MobileWallOrder.StepTile(MX, MY, new Vector3(ox, oy, 0), out int nx, out int ny);
            nx.Should().Be(MX + dx);
            ny.Should().Be(MY + dy);
        }

        // a mounted mobile stepping east from (MX, MY) along the studio's south wall line (0x0034 at y + 1): the key sorts it on
        // (MX + 1, MY) from the step's first pixel, one diagonal past the wall piece south of the tile it leaves, so its body
        // (strip 0) beat that piece and the hind legs showed on it until the step ended
        [Fact]
        public void SteppingEastKeepsTheBodyBehindTheWallAheadOfTheTileItLeaves()
        {
            var map = Studio(false, false, false);
            Add(map, South, MX, MY + 1, Z);
            var offset = new Vector3(4, 4, 0);
            MobileWallOrder.DepthTile(MX, MY, offset, out int bx, out int by);
            (bx, by).Should().Be((MX + 1, MY));
            MobileWallOrder.Scan s = default;
            s.Active = true;
            MobileWallOrder.ScanStep(map, MX, MY, Z, offset, bx, by, ref s);
            s.Count.Should().Be(1);
            s.MinAdd.Should().Be(-1);
            s.Walls[0].Lo.Should().Be(-66, "the piece's in-front half moved to the depth tile's columns");

            float depth = MobileDepth(MX + 1, MY, Z);
            int wallD = s.Walls[0].D;
            Key(depth, 0).Should().BeGreaterThan(wallD, "before: the body beat the piece");

            // hind legs over the piece: the body strip drops just behind it, no lower than standing on the tile it leaves
            float add = MobileWallOrder.CapAdd(0, in s, -60, -10, depth, out _);
            Key(depth, add).Should().Be(wallD - 1);
            Key(depth, add).Should().BeGreaterOrEqualTo(Key(MobileDepth(MX, MY, Z), 0));

            // columns clear of the piece keep their keys
            MobileWallOrder.CapAdd(0, in s, -20, 20, depth, out _).Should().Be(0);

            // standing on either tile: no lowered upper strip
            s = default;
            MobileWallOrder.ScanStep(map, MX, MY, Z, Vector3.Zero, MX, MY, ref s);
            s.MinAdd.Should().Be(0);
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

        [Theory]
        [InlineData("mounted")]
        [InlineData("equipped")]
        public void CompositeSpanGivesEveryLayerOneCap(string name)
        {
            // a piece only beyond every layer's own frame: no strip moves; with the composite's span reaching it, every strip
            // below the anchor of every layer takes the same key (a mount's capped strip never falls behind its rider's)
            MobileWallOrder.Scan far = Everywhere(true);
            far.Walls[0].Lo = 5000;
            far.Walls[0].Hi = 5100;
            uint shadow = KeyOf(812.25f), upper = KeyOf(812.25f + 1f), capped = BakeJob.DepthKeyD(81374, 1000, 7);

            for (int seed = 1; seed <= 20; seed++)
            {
                Depths(name, far, seed).Should().Equal(Depths(name, default, seed), $"seed {seed}");
                MobileWallOrder.Scan span = far;
                span.SpanLo = -10000;
                span.SpanHi = 10000;
                Depths(name, span, seed).Should().OnlyContain(d => d == shadow || d == upper || d == capped, $"seed {seed}");
            }
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("mounted")]
        public void PieceOverheadSplitsTheUpperStrip(string name)
        {
            // a piece overhead over every column whose art ends 40 px above the tile centre (the fixture draws at 600, 400:
            // centre row 422): every upper strip reaching above that row is split, its top rows a key below the piece
            MobileWallOrder.Scan over = default;
            over.Active = true;
            over.Count = 1;
            over.Walls[0] = new MobileWallOrder.Wall { D = 81260, Lo = -10000, Hi = 10000, Overhead = true, Bottom = -40 };
            uint top = BakeJob.DepthKeyD(81259, 1000, 7);
            int split = 0;

            for (int seed = 1; seed <= 20; seed++)
            {
                uint[] off = Depths(name, default, seed);
                uint[] on = Depths(name, over, seed);
                on.Length.Should().BeGreaterOrEqualTo(off.Length);
                split += on.Length - off.Length;
                on.Count(d => d == top).Should().BeGreaterOrEqualTo(on.Length - off.Length, $"seed {seed}: a capped top part per split (a strip wholly above the row is capped whole)");
                on.Where(d => d != top).Should().BeSubsetOf(off, $"seed {seed}: every other key as before");
            }

            split.Should().BeGreaterThan(0, "some upper strips reach above the piece's art");
            Depths(name, over with { Active = false }, 3).Should().Equal(Depths(name, default, 3), "switch off");
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
