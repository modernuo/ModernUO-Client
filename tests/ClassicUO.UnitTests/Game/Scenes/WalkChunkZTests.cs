using System;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // walk_chunk_z (WalkChunkZ): the fill's S band per chunk from the chunk's recorded Z range. The tight band must keep every
    // tile whose objects (anchored inside the range) the walk could draw or the Gpu hidden pass hide, and the range must hold
    // every object of the chunk through adds, moves, Z changes, removals, rebuilds and reloads.
    [Collection("ChunkStaticTileDataCollection")] // Chunk.StaticTileData is process-wide
    public sealed class WalkChunkZTests : IDisposable
    {
        private readonly StaticTiles[] _saved;

        public WalkChunkZTests()
        {
            _saved = Chunk.StaticTileData;
            Chunk.StaticTileData = new StaticTiles[0x10000];
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _saved;
            WalkChunkZ.OverrideForTests = null;
        }

        public static TheoryData<int, int, int, int, int, int, int> Views => new()
        {
            { 0, 0, 1920, 1080, 44, 0, 0 },
            { 22 * 1000 - 960, 22 * 3000 - 540, 1920, 1080, 44, -5, 20 },
            { 22 * 1495 - 22 * 1629 - 1600, 22 * (1495 + 1629) - 900, 3200, 1800, 29, -128, -100 },
            { 5000, 70000, 6400, 3424, 17, 100, 127 },
            { -300, 41000, 800, 1400, 88, -30, 60 }
        };

        private static (Vector2 min, Vector2 max, Rectangle view) Frame(int w, int h, int margin)
            => (new Vector2(-margin, -margin), new Vector2(w + margin, h + margin), new Rectangle(0, 0, w, h));

        // the walk's anchor cull for objects at Z zLo..zHi (stretched land's MinZ inside the range too)
        private static bool WalkCanDraw(int tx, int ty, int offX, int offY, Vector2 min, Vector2 max, int zLo, int zHi)
        {
            int sx = (tx - ty) * 22 - offX - 22;

            if (sx < min.X || sx > max.X)
            {
                return false;
            }

            for (int z = zLo; z <= zHi; z++)
            {
                int sy = (tx + ty) * 22 - (z << 2) - offY - 22;

                // the most a stretched land's MinZ can lower its top cull: MinZ at the range's low end
                if (sy + (z << 2) - (zLo << 2) >= min.Y && sy <= max.Y)
                {
                    return true;
                }
            }

            return false;
        }

        // GpuLandVisible / GpuStaticVisible for land and statics anchored inside the range
        private static bool GpuCanHide(int tx, int ty, int offX, int offY, Rectangle view, int artH, int zLo, int zHi)
        {
            int sx = (tx - ty) * 22 - offX - 22;

            if (sx < view.Left - 320 || sx > view.Right + 320)
            {
                return false;
            }

            for (int z = zLo; z <= zHi; z++)
            {
                int sy = (tx + ty) * 22 - (z << 2) - offY - 22;
                int top = sy - 128, bottom = sy + 44 + ((z - zLo) << 2);

                if (bottom > view.Top && top < view.Bottom)
                {
                    return true;
                }

                int y1 = sy + 44;

                if (y1 > view.Top && y1 - artH < view.Bottom)
                {
                    return true;
                }
            }

            return false;
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void The_tight_band_is_inside_the_full_band_and_keeps_every_reachable_tile(int offX, int offY, int w, int h, int margin, int zLo, int zHi)
        {
            var (min, max, view) = Frame(w, h, margin);
            const int artH = 300;

            foreach (bool gpu in new[] { false, true })
            {
                FillDiamond.Bands full = FillDiamond.ComputeBands(offX, offY, min, max, gpu, view, gpu ? artH : 0);
                int cx = (offX + offY) / 44, cy = (offY - offX) / 44, r = Math.Max(w, h) / 22 + 60;
                int kept = 0;

                for (int chunkX = (cx - r) >> 3; chunkX <= (cx + r) >> 3; chunkX++)
                {
                    for (int chunkY = (cy - r) >> 3; chunkY <= (cy + r) >> 3; chunkY++)
                    {
                        if (chunkX < 0 || chunkY < 0)
                        {
                            continue;
                        }

                        ulong walk = FillDiamond.ChunkMasks(gpu, chunkX, chunkY, in full, zLo, zHi, out ulong hidden);
                        ulong fullWalk = FillDiamond.ChunkMasks(gpu, chunkX, chunkY, in full, out ulong fullHidden);

                        (walk & ~fullWalk).Should().Be(0UL, "the tight walk band is inside the full one");
                        (hidden & ~fullHidden).Should().Be(0UL, "the tight hidden band is inside the full one");
                        (walk & ~hidden).Should().Be(0UL, "the walk band is inside the hidden band");

                        for (int cell = 0; cell < 64; cell++)
                        {
                            int tx = (chunkX << 3) + (cell >> 3), ty = (chunkY << 3) + (cell & 7);
                            bool draw = WalkCanDraw(tx, ty, offX, offY, min, max, zLo, zHi);
                            bool hide = gpu && GpuCanHide(tx, ty, offX, offY, view, artH, zLo, zHi);

                            if (draw && (walk & (1UL << cell)) == 0)
                            {
                                throw new Xunit.Sdk.XunitException($"gpu {gpu}: tile {tx},{ty} drawable at z {zLo}..{zHi} outside the tight walk band");
                            }

                            if ((draw || hide) && (hidden & (1UL << cell)) == 0)
                            {
                                throw new Xunit.Sdk.XunitException($"gpu {gpu}: tile {tx},{ty} reachable at z {zLo}..{zHi} outside the tight hidden band");
                            }

                            kept += draw ? 1 : 0;
                        }
                    }
                }

                kept.Should().BeGreaterThan(0);
            }
        }

        [Fact]
        public void The_full_z_range_gives_the_full_band_and_a_narrow_range_a_much_smaller_one()
        {
            var (min, max, view) = Frame(1920, 1080, 44);
            FillDiamond.Bounds b = FillDiamond.Compute(0, 0, min, max, true, view, 250);

            FillDiamond.ForZ(in b, sbyte.MinValue, sbyte.MaxValue).Should().Be(b);

            FillDiamond.Bounds t = FillDiamond.ForZ(in b, 0, 20);
            (t.SHi - t.SLo).Should().BeLessThan((b.SHi - b.SLo) - 40, "Z 0..20 drops most of the 1,020 px of Z slack");
            t.SLo.Should().BeGreaterThanOrEqualTo(b.SLo);
            t.SHi.Should().BeLessThanOrEqualTo(b.SHi);
        }

        [Fact]
        public void An_empty_range_gives_the_full_masks()
        {
            var (min, max, view) = Frame(1920, 1080, 44);
            FillDiamond.Bands b = FillDiamond.ComputeBands(22 * 100, 22 * 300, min, max, true, view, 250);

            for (int chunkX = 0; chunkX < 40; chunkX++)
            {
                for (int chunkY = 0; chunkY < 40; chunkY++)
                {
                    ulong w0 = FillDiamond.ChunkMasks(true, chunkX, chunkY, in b, out ulong h0);
                    ulong w1 = FillDiamond.ChunkMasks(true, chunkX, chunkY, in b, int.MaxValue, int.MinValue, out ulong h1);
                    w1.Should().Be(w0);
                    h1.Should().Be(h0);
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_switch_follows_its_override(bool on)
        {
            WalkChunkZ.OverrideForTests = on;
            WalkChunkZ.Enabled.Should().Be(on);
        }

        private static Land NewLand(World world, sbyte z)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { world }, null);
            land.Z = z;

            return land;
        }

        private static (int lo, int hi) Range(Chunk c) => (c.WalkZLo, c.WalkZHi);

        [Fact]
        public void The_range_holds_every_object_through_adds_moves_z_changes_removals_and_rebuilds()
        {
            var world = new World();
            Chunk a = Chunk.Create(world, 0, 0), b = Chunk.Create(world, 1, 0);

            Range(a).Should().Be((int.MaxValue, int.MinValue), "a new chunk is empty");

            Land land = NewLand(world, 10);
            a.AddGameObject(land, 0, 0);
            Range(a).Should().Be((10, 10));

            // stretched land: MinZ and its corners
            Land hill = NewLand(world, 12);
            hill.IsStretched = true;
            hill.MinZ = -4;
            hill.YOffsets.Top = 12 * 4;
            hill.YOffsets.Right = 30 * 4;
            hill.YOffsets.Left = -4 * 4;
            hill.YOffsets.Bottom = 0;
            a.AddGameObject(hill, 1, 0);
            Range(a).Should().Be((-4, 30));

            var st = new Static(world) { Graphic = 1, Z = 40 };
            a.AddGameObject(st, 2, 2);
            Range(a).Should().Be((-4, 40));

            Item item = world.GetOrCreateItem(0x4100_0001);
            item.Z = -60;
            a.AddGameObject(item, 3, 3);
            Range(a).Should().Be((-60, 40), "a dynamic item joins the index");

            Mobile mobile = world.GetOrCreateMobile(0x0010_0001);
            mobile.Z = 0;
            a.AddGameObject(mobile, 4, 4);
            mobile.Z = 127;
            Range(a).Should().Be((-60, 127), "a Z change on a tile grows the range");
            mobile.Z = -128;
            Range(a).Should().Be((-128, 127));

            // moved to another chunk: the new chunk holds it, the old one keeps its (conservative) range
            b.AddGameObject(mobile, 0, 0);
            Range(b).Should().Be((-128, -128));
            Range(a).Should().Be((-128, 127));

            item.RemoveFromTile();
            Range(a).Should().Be((-128, 127), "removals never shrink the range");

            a.RecomputeWalkZ();
            Range(a).Should().Be((-4, 40), "a rebuild recomputes it exactly: land, hill and static remain");

            st.Z = 50;
            Range(a).Should().Be((-4, 50));

            a.ClearForReload();
            Range(a).Should().Be((int.MaxValue, int.MinValue), "a reload empties it");
        }

        [Fact]
        public void A_z_change_off_the_map_touches_no_chunk()
        {
            var world = new World();
            Chunk a = Chunk.Create(world, 0, 0);
            var st = new Static(world) { Graphic = 1, Z = 5 };
            a.AddGameObject(st, 0, 0);
            st.RemoveFromTile();
            st.Z = 90;
            Range(a).Should().Be((5, 5));
        }
    }
}
