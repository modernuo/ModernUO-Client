using System;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // perf-16 (world_fill_diamond): the fill walks only the tiles of its square range whose objects the walk or the Gpu hidden
    // pass can still act on. A tile outside the bands must be culled by the walk for every Z and give CollectGpuHidden nothing
    // to hide; with the switch off every tile is walked.
    [Collection(nameof(FillDiamondTests))]
    public sealed class FillDiamondTests : IDisposable
    {
        public FillDiamondTests()
        {
            Reset();
        }

        public void Dispose()
        {
            Reset();
        }

        private static void Reset()
        {
#if CUO_DEVTOOLS
            FillDiamond.ResetForTests();
#else
            FillDiamond.OverrideForTests = null;
#endif
        }

        // (offX, offY, view width, view height, anchor margin): views like GetViewPort's (world pixels, margin 44 / zoom)
        public static TheoryData<int, int, int, int, int> Views => new()
        {
            { 0, 0, 1920, 1080, 44 },
            { 22 * 1000 - 960, 22 * 3000 - 540, 1920, 1080, 44 },
            { 22 * 1495 - 22 * 1629 - 1600, 22 * (1495 + 1629) - 900, 3200, 1800, 29 },
            { 5000, 70000, 6400, 3424, 17 },
            { -300, 41000, 800, 1400, 88 }
        };

        private static (Vector2 min, Vector2 max, Rectangle view) Frame(int w, int h, int margin)
        {
            var view = new Rectangle(0, 0, w, h);

            return (new Vector2(-margin, -margin), new Vector2(w + margin, h + margin), view);
        }

        // the walk's culling (AddTileToRenderList): anchor x beside the rectangle culls the tile, anchor y each object
        private static bool WalkCanDraw(int tx, int ty, int offX, int offY, Vector2 min, Vector2 max)
        {
            int sx = (tx - ty) * 22 - offX - 22;

            if (sx < min.X || sx > max.X)
            {
                return false;
            }

            for (int z = sbyte.MinValue; z <= sbyte.MaxValue; z++)
            {
                int sy = (tx + ty) * 22 - (z << 2) - offY - 22;

                if (sy >= min.Y && sy <= max.Y)
                {
                    return true;
                }
            }

            return false;
        }

        // CollectGpuHidden: its cut beside the view, then GpuLandVisible / GpuStaticVisible for any Z, MinZ and art height
        private static bool GpuCanHide(int tx, int ty, int offX, int offY, Rectangle view, int artH)
        {
            int sx = (tx - ty) * 22 - offX - 22;

            if (sx < view.Left - 320 || sx > view.Right + 320)
            {
                return false;
            }

            for (int z = sbyte.MinValue; z <= sbyte.MaxValue; z++)
            {
                int sy = (tx + ty) * 22 - (z << 2) - offY - 22;
                // land, stretched to the extremes (MinZ -128, corners 32 above)
                int top = sy - 128, bottom = sy + 44 + ((z - sbyte.MinValue) << 2);

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
        public void A_tile_outside_the_legacy_bands_is_culled_for_every_z(int offX, int offY, int w, int h, int margin)
        {
            var (min, max, view) = Frame(w, h, margin);
            var b = FillDiamond.Compute(offX, offY, min, max, false, view, 0);
            int cx = (offX + offY) / 44, cy = (offY - offX) / 44, r = Math.Max(w, h) / 22 + 40;
            int inside = 0, outside = 0;

            for (int tx = cx - r; tx <= cx + r; tx++)
            {
                for (int ty = cy - r; ty <= cy + r; ty++)
                {
                    if (b.Contains(tx, ty))
                    {
                        inside++;

                        continue;
                    }

                    outside++;

                    if (WalkCanDraw(tx, ty, offX, offY, min, max))
                    {
                        throw new Xunit.Sdk.XunitException($"tile {tx},{ty} outside {b} can be drawn");
                    }
                }
            }

            inside.Should().BeGreaterThan(0);
            outside.Should().BeGreaterThan(inside, "the square around the view is mostly outside the diamond");
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void A_tile_outside_the_gpu_bands_is_culled_and_has_nothing_to_hide(int offX, int offY, int w, int h, int margin)
        {
            var (min, max, view) = Frame(w, h, margin);
            const int artH = 300;
            var b = FillDiamond.Compute(offX, offY, min, max, true, view, artH);
            int cx = (offX + offY) / 44, cy = (offY - offX) / 44, r = Math.Max(w, h) / 22 + 60;

            for (int tx = cx - r; tx <= cx + r; tx++)
            {
                for (int ty = cy - r; ty <= cy + r; ty++)
                {
                    if (b.Contains(tx, ty))
                    {
                        continue;
                    }

                    if (WalkCanDraw(tx, ty, offX, offY, min, max) || GpuCanHide(tx, ty, offX, offY, view, artH))
                    {
                        throw new Xunit.Sdk.XunitException($"tile {tx},{ty} outside {b} can be drawn or hidden");
                    }
                }
            }
        }

        [Fact]
        public void The_bands_are_not_much_wider_than_the_view()
        {
            var (min, max, view) = Frame(1920, 1080, 44);
            var b = FillDiamond.Compute(0, 0, min, max, false, view, 0);

            // D: the view's width plus the margins and a tile of slack; S: its height plus Z's 1020 px and the slack
            (b.DHi - b.DLo).Should().BeLessThanOrEqualTo((1920 + 88) / 22 + 4);
            (b.SHi - b.SLo).Should().BeLessThanOrEqualTo((1080 + 88 + 1020) / 22 + 4);
        }

        [Fact]
        public void The_chunk_mask_matches_the_per_tile_test()
        {
            var (min, max, view) = Frame(1920, 1080, 44);
            var b = FillDiamond.Compute(22 * 100, 22 * 300, min, max, true, view, 250);
            int full = 0, empty = 0, partial = 0;

            for (int chunkX = 0; chunkX < 64; chunkX++)
            {
                for (int chunkY = 0; chunkY < 64; chunkY++)
                {
                    ulong mask = FillDiamond.ChunkMask(chunkX, chunkY, in b);
                    ulong expected = 0;

                    for (int x = 0; x < 8; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            if (b.Contains((chunkX << 3) + x, (chunkY << 3) + y))
                            {
                                expected |= 1UL << (x << 3 | y);
                            }
                        }
                    }

                    mask.Should().Be(expected, $"chunk {chunkX},{chunkY}");

                    if (mask == ulong.MaxValue)
                    {
                        full++;
                    }
                    else if (mask == 0)
                    {
                        empty++;
                    }
                    else
                    {
                        partial++;
                    }
                }
            }

            full.Should().BeGreaterThan(0);
            empty.Should().BeGreaterThan(0);
            partial.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Switch_off_walks_every_tile_switch_on_the_diamond(bool gpu)
        {
            var (min, max, view) = Frame(1920, 1080, 44);
            var b = FillDiamond.ComputeBands(0, 0, min, max, gpu, view, 250);

            FillDiamond.ChunkMasks(false, gpu, 40, -10, in b, out ulong hidden).Should().Be(ulong.MaxValue);
            hidden.Should().Be(ulong.MaxValue);
            FillDiamond.ChunkMasks(true, gpu, 40, -10, in b, out hidden).Should().Be(0UL, "a chunk far beside the view");
            hidden.Should().Be(0UL);
            FillDiamond.ChunkMasks(true, gpu, 0, 0, in b, out hidden).Should().Be(FillDiamond.ChunkMask(0, 0, in b.Walk)).And.NotBe(0UL);
            hidden.Should().Be(FillDiamond.ChunkMask(0, 0, in b.Hidden));
        }

        // perf-18: the Gpu world walks the Legacy bands; its hidden pass visits the wider ones
        [Theory]
        [MemberData(nameof(Views))]
        public void Gpu_walk_bands_are_the_legacy_bands_and_the_hidden_bands_hold_them(int offX, int offY, int w, int h, int margin)
        {
            var (min, max, view) = Frame(w, h, margin);
            var legacy = FillDiamond.ComputeBands(offX, offY, min, max, false, view, 0);
            var gpu = FillDiamond.ComputeBands(offX, offY, min, max, true, view, 300);

            legacy.Walk.Should().Be(FillDiamond.Compute(offX, offY, min, max, false, view, 0));
            legacy.Hidden.Should().Be(legacy.Walk);
            gpu.Walk.Should().Be(legacy.Walk);
            gpu.Hidden.Should().Be(FillDiamond.Compute(offX, offY, min, max, true, view, 300));
            gpu.Hidden.DLo.Should().BeLessThanOrEqualTo(gpu.Walk.DLo);
            gpu.Hidden.DHi.Should().BeGreaterThanOrEqualTo(gpu.Walk.DHi);
            gpu.Hidden.SLo.Should().BeLessThanOrEqualTo(gpu.Walk.SLo);
            gpu.Hidden.SHi.Should().BeGreaterThanOrEqualTo(gpu.Walk.SHi);
        }

        // perf-18: per chunk, the walked tiles are inside the hidden pass's; a chunk with an empty hidden mask (no chunk-level
        // work) has no tile the walk could draw on or the hidden pass could hide on; on the Gpu world some chunks are only
        // visited by the hidden pass (the split's gain)
        [Theory]
        [MemberData(nameof(Views))]
        public void Chunk_masks_split_the_walk_from_the_hidden_pass(int offX, int offY, int w, int h, int margin)
        {
            var (min, max, view) = Frame(w, h, margin);
            const int artH = 300;
            int cx = (offX + offY) / 44 >> 3, cy = (offY - offX) / 44 >> 3, r = Math.Max(w, h) / 22 / 8 + 10;

            foreach (bool gpu in new[] { false, true })
            {
                var b = FillDiamond.ComputeBands(offX, offY, min, max, gpu, view, artH);
                int hiddenOnly = 0, empty = 0;

                for (int chunkX = cx - r; chunkX <= cx + r; chunkX++)
                {
                    for (int chunkY = cy - r; chunkY <= cy + r; chunkY++)
                    {
                        ulong walk = FillDiamond.ChunkMasks(true, gpu, chunkX, chunkY, in b, out ulong hidden);

                        walk.Should().Be(FillDiamond.ChunkMask(chunkX, chunkY, in b.Walk));
                        (walk & ~hidden).Should().Be(0UL, $"chunk {chunkX},{chunkY}: walked tiles are hidden-pass tiles");

                        if (!gpu)
                        {
                            hidden.Should().Be(walk);
                        }

                        hiddenOnly += System.Numerics.BitOperations.PopCount(hidden & ~walk);

                        if (hidden != 0)
                        {
                            continue;
                        }

                        empty++;

                        for (int x = 0; x < 8; x++)
                        {
                            for (int y = 0; y < 8; y++)
                            {
                                int tx = (chunkX << 3) + x, ty = (chunkY << 3) + y;

                                if (WalkCanDraw(tx, ty, offX, offY, min, max) || gpu && GpuCanHide(tx, ty, offX, offY, view, artH))
                                {
                                    throw new Xunit.Sdk.XunitException($"tile {tx},{ty} of empty chunk {chunkX},{chunkY} can be drawn or hidden");
                                }
                            }
                        }
                    }
                }

                empty.Should().BeGreaterThan(0);

                if (gpu)
                {
                    hiddenOnly.Should().BeGreaterThan(0, "the Gpu hidden pass reaches tiles the walk does not");
                }
                else
                {
                    hiddenOnly.Should().Be(0);
                }
            }
        }

        [Fact]
        public void The_override_selects_the_switch_state()
        {
            FillDiamond.OverrideForTests = true;
            FillDiamond.Enabled.Should().BeTrue();
            FillDiamond.OverrideForTests = false;
            FillDiamond.Enabled.Should().BeFalse();
        }

        [Fact]
        public void Max_static_art_height_reads_the_static_entries_only()
        {
            var art = new Uo.Pack.SpriteEntry[0x4000 + 4];
            art[10].OrigH = 900;
            art[0x4000 + 2].OrigH = 333;

            FillDiamond.MaxStaticArtHeight(art).Should().Be(333);
            FillDiamond.MaxStaticArtHeight(null).Should().BeGreaterThan(333);
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("0", 0)]
        [InlineData("1", 10)]
        [InlineData("4", 4)]
        [InlineData("x", 0)]
        public void Check_every_parses(string v, int expected)
        {
            FillDiamond.ParseCheck(v).Should().Be(expected);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Check_frames_come_every_nth_fill()
        {
            FillDiamond.CheckEveryForTests = 3;
            int n = 0;

            for (int i = 0; i < 9; i++)
            {
                n += FillDiamond.BeginCheckFrame() ? 1 : 0;
            }

            n.Should().Be(3);
        }

        [Fact]
        public void Check_frames_count_walk_hidden_and_empty_chunks()
        {
            FillDiamond.CountChunk(0x0FUL, 0xFFUL);
            FillDiamond.CountChunk(0, 0);
            FillDiamond.CountChunk(ulong.MaxValue, ulong.MaxValue);

            FillDiamond.OutsideWalk.Should().Be(60 + 64);
            FillDiamond.OutsideHidden.Should().Be(56 + 64);
            FillDiamond.EmptyChunks.Should().Be(1);
            FillDiamond.Mismatches.Should().Be(0);
        }

        [Fact]
        public void Edge_overrides_outside_the_hidden_bands_are_mismatches()
        {
            var b = new FillDiamond.Bounds { DLo = 0, DHi = 1, SLo = 0, SHi = 1 };
            FillDiamond.EdgeHidden(40, 2, 3, in b);

            FillDiamond.Mismatches.Should().Be(3);
        }
#endif
    }
}
