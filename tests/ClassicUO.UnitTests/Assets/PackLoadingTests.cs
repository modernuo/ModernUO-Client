using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Pack-loading changes (perf-opportunities.md, "ClassicUO pack-loading path"): picker masks and visible bounds from the
    // trim rect, pooled chunk/region buffers, header-only page reads, the palette LRU and the cached land z lookup. Each new
    // path is checked against the code it replaced. Tests that need the real pack skip when it is not on this machine.
    public class PackLoadingTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        private static bool HavePack => File.Exists(Path.Combine(PackDir, "manifest.json"));

        // ---- PixelPicker.SetTrimmed / VisibleBounds against the old full-image scans ----

        private static void OldBounds(ReadOnlySpan<uint> pixels, int w, int h, out int minX, out int minY, out int maxX, out int maxY)
        {
            // Art.Get's loop before the change
            int pos1 = 0;
            minX = w; minY = h; maxX = 0; maxY = 0;

            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    if (pixels[pos1++] != 0)
                    {
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
        }

        private static void AssertSameMask(PixelPicker expected, PixelPicker actual, ulong id, int w, int h)
        {
            expected.GetDimensions(id, out int ew, out int eh);
            actual.GetDimensions(id, out int aw, out int ah);
            (aw, ah).Should().Be((ew, eh));

            for (int y = -1; y <= h; y++)
            {
                for (int x = -1; x <= w; x++)
                {
                    if (actual.Get(id, x, y) != expected.Get(id, x, y) || actual.Get(id, x, y, 1) != expected.Get(id, x, y, 1))
                    {
                        throw new Xunit.Sdk.XunitException($"mask {id} differs at ({x},{y}) in a {w}x{h} image");
                    }
                }
            }
        }

        private static (uint[] Pixels, int TrimX, int TrimY, int TrimW, int TrimH) RandomSprite(Random rnd, int w, int h)
        {
            var pixels = new uint[w * h];
            int tw = rnd.Next(0, w + 1), th = rnd.Next(0, h + 1);
            int tx = rnd.Next(0, w - tw + 1), ty = rnd.Next(0, h - th + 1);
            int mode = rnd.Next(4); // sparse, dense, long runs, solid

            for (int y = ty; y < ty + th; y++)
            {
                for (int x = tx; x < tx + tw; x++)
                {
                    bool visible = mode switch
                    {
                        0 => rnd.Next(5) == 0,
                        1 => rnd.Next(5) != 0,
                        2 => ((x / (1 + (y % 7))) & 1) == 0,
                        _ => true
                    };

                    pixels[y * w + x] = visible ? (uint)rnd.Next(1, int.MaxValue) | 0xFF000000 : 0;
                }
            }

            return (pixels, tx, ty, tw, th);
        }

        [Fact]
        public void SetTrimmed_Matches_Set_On_Random_Sprites_Simd_And_Scalar()
        {
            var rnd = new Random(1234);

            for (int n = 0; n < 400; n++)
            {
                int w = rnd.Next(1, 90), h = rnd.Next(1, 90);
                var s = RandomSprite(rnd, w, h);
                var old = new PixelPicker();
                var simd = new PixelPicker();
                var scalar = new PixelPicker();
                old.Set((ulong)n, w, h, s.Pixels);
                simd.SetTrimmed((ulong)n, w, h, s.TrimX, s.TrimY, s.TrimW, s.TrimH, s.Pixels);

                try
                {
                    PixelPicker.ForceScalar = true;
                    scalar.SetTrimmed((ulong)n, w, h, s.TrimX, s.TrimY, s.TrimW, s.TrimH, s.Pixels);
                }
                finally
                {
                    PixelPicker.ForceScalar = false;
                }

                AssertSameMask(old, simd, (ulong)n, w, h);
                AssertSameMask(old, scalar, (ulong)n, w, h);
            }
        }

        [Fact]
        public void VisibleBounds_Matches_Full_Scan_On_Random_Sprites()
        {
            var rnd = new Random(99);

            for (int n = 0; n < 1000; n++)
            {
                int w = rnd.Next(1, 70), h = rnd.Next(1, 70);
                var s = RandomSprite(rnd, w, h);
                OldBounds(s.Pixels, w, h, out int a, out int b, out int c, out int d);
                PixelPicker.VisibleBounds(s.Pixels, w, h, s.TrimX, s.TrimY, s.TrimW, s.TrimH, out int e, out int f, out int g, out int k);
                (e, f, g, k).Should().Be((a, b, c, d), $"sprite {n} ({w}x{h}, trim {s.TrimX},{s.TrimY} {s.TrimW}x{s.TrimH})");
            }
        }

        [Fact]
        public void Picker_And_Bounds_Match_Old_Path_On_Pack_Art_And_Anim_Samples()
        {
            if (!HavePack)
            {
                return;
            }

            // art: every 23rd static that exists
            var sprites = SpriteTable.Read(Path.Combine(PackDir, "art", "sprites.bin"));
            var art = new PackPages(Path.Combine(PackDir, "art"), 64 << 20);
            int checkedArt = 0;

            for (int i = 0x4000; i < sprites.Length && checkedArt < 600; i += 23)
            {
                ref readonly var e = ref sprites[i];

                if (e.OrigW == 0 || e.OrigH == 0)
                {
                    continue;
                }

                var pixels = PackSprites.DecodeColor(art, in e);
                var trim = PackSprites.Trim(in e);
                var old = new PixelPicker();
                var neu = new PixelPicker();
                old.Set((ulong)i, e.OrigW, e.OrigH, pixels);
                neu.SetTrimmed((ulong)i, e.OrigW, e.OrigH, trim.X, trim.Y, trim.W, trim.H, pixels);
                AssertSameMask(old, neu, (ulong)i, e.OrigW, e.OrigH);

                OldBounds(pixels, e.OrigW, e.OrigH, out int a, out int b, out int c, out int d);
                PixelPicker.VisibleBounds(pixels, e.OrigW, e.OrigH, trim.X, trim.Y, trim.W, trim.H, out int x0, out int y0, out int x1, out int y1);
                (x0, y0, x1, y1).Should().Be((a, b, c, d), $"art {i}");
                checkedArt++;
            }

            checkedArt.Should().BeGreaterThan(100);

            // anim: every 97th frame, decoded with its palette
            var (_, _, frames) = AnimTableFile.Read(Path.Combine(PackDir, "anim", AnimTableFile.FileName));
            var (framePalette, palettes) = AnimPaletteFile.Read(Path.Combine(PackDir, "anim", AnimPaletteFile.FileName));
            var anim = new PackPages(Path.Combine(PackDir, "anim"), 64 << 20);
            var palette = new uint[256];
            int checkedAnim = 0;

            for (int i = 0; i < frames.Length && checkedAnim < 600; i += 97)
            {
                ref readonly var e = ref frames[i].Sprite;

                if (!e.Present || e.OrigW == 0 || e.OrigH == 0)
                {
                    continue;
                }

                for (int c = 0; c < 256; c++)
                {
                    ushort v = palettes[framePalette[i] * 256 + c];
                    palette[c] = c == 0 || v == 0 ? 0 : v | 0xFF000000u; // zero exactly where the RGBA expansion is zero
                }

                var pixels = PackSprites.DecodeIndexed(anim, in e, palette);
                var trim = PackSprites.Trim(in e);
                var old = new PixelPicker();
                var neu = new PixelPicker();
                old.Set((ulong)i, e.OrigW, e.OrigH, pixels);
                neu.SetTrimmed((ulong)i, e.OrigW, e.OrigH, trim.X, trim.Y, trim.W, trim.H, pixels);
                AssertSameMask(old, neu, (ulong)i, e.OrigW, e.OrigH);
                checkedAnim++;
            }

            checkedAnim.Should().BeGreaterThan(100);
        }

        [Fact]
        public void DecodeColor_Into_Dirty_Buffer_Matches_New_Array()
        {
            if (!HavePack)
            {
                return;
            }

            var sprites = SpriteTable.Read(Path.Combine(PackDir, "art", "sprites.bin"));
            var art = new PackPages(Path.Combine(PackDir, "art"), 64 << 20);
            var buffer = new uint[256 * 256];

            for (int i = 0x4000; i < sprites.Length; i += 211)
            {
                ref readonly var e = ref sprites[i];

                if (e.OrigW == 0 || e.OrigH == 0 || e.OrigW * e.OrigH > buffer.Length)
                {
                    continue;
                }

                buffer.AsSpan().Fill(0xDEADBEEF);
                PackSprites.DecodeColor(art, in e, buffer);
                buffer.AsSpan(0, e.OrigW * e.OrigH).SequenceEqual(PackSprites.DecodeColor(art, in e)).Should().BeTrue($"art {i}");
            }
        }

        // ---- PackPages: chunk decode outside the lock with pooled buffers ----

        [Fact]
        public void PackPages_Parallel_Reads_With_Tiny_Cache_Match_Source()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuo-packpages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                const int side = 128;
                var rnd = new Random(7);
                var sources = new byte[3][];

                for (int p = 0; p < sources.Length; p++)
                {
                    sources[p] = new byte[side * side * 2];
                    rnd.NextBytes(sources[p]);
                    // Kind 2 with 4 KiB chunks (many chunk boundaries), Kind 1 and Kind 0
                    ushort kind = p == 0 ? FileKind.ChunkedBrotli : p == 1 ? FileKind.Brotli : FileKind.Raw;
                    AtlasPage.Write(PackConst.PagePath(dir, p), side, 1, PixelFormat.B5G5R5A1, sources[p], kind, 4096);
                }

                // the cache holds about 3 chunks, so reads evict and reuse buffers constantly
                var pages = new PackPages(dir, 3 * 4096);

                Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
                {
                    var r = new Random(n);
                    int p = r.Next(sources.Length);
                    int w = r.Next(1, 40), h = r.Next(1, 40);
                    int x = r.Next(0, side - w), y = r.Next(0, side - h);
                    var dst = new byte[w * h * 2];
                    pages.ReadRect(p, x, y, w, h, dst);

                    for (int row = 0; row < h; row++)
                    {
                        if (!dst.AsSpan(row * w * 2, w * 2).SequenceEqual(sources[p].AsSpan(((y + row) * side + x) * 2, w * 2)))
                        {
                            throw new Xunit.Sdk.XunitException($"page {p} rect {x},{y} {w}x{h} row {row} differs");
                        }
                    }
                });

                pages.IsResident(0, 0, 0, side, side).Should().BeFalse();
                pages.Touch(0, 0, 0, 4, 4);
                pages.IsResident(0, 0, 0, 4, 4).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- AtlasPage.ReadHeader: header bytes only ----

        [Fact]
        public void ReadHeader_Matches_OpenChain_For_All_Kinds()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuo-pagehdr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var chain = new byte[AtlasPage.ChainBytes(256, 3, PixelFormat.B5G5R5A1)];
                new Random(3).NextBytes(chain);
                int i = 0;

                foreach (ushort kind in new[] { FileKind.Raw, FileKind.Brotli, FileKind.ChunkedBrotli })
                {
                    string path = PackConst.PagePath(dir, i++);
                    AtlasPage.Write(path, 256, 3, PixelFormat.B5G5R5A1, chain, kind, 8192);

                    using (var s = AtlasPage.OpenChain(path, out var expected))
                    {
                        AtlasPage.ReadHeader(path).Should().Be(expected, $"kind {kind}");
                    }
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ReadHeader_Matches_OpenChain_On_Pack_Pages()
        {
            if (!HavePack)
            {
                return;
            }

            foreach (var sub in new[] { "art", "tex", "anim" })
            {
                for (int i = 0; i < 40; i += 7)
                {
                    string path = PackConst.PagePath(Path.Combine(PackDir, sub), i);

                    if (!File.Exists(path))
                    {
                        break;
                    }

                    using var s = AtlasPage.OpenChain(path, out var expected);
                    AtlasPage.ReadHeader(path).Should().Be(expected, path);
                }
            }
        }

        // ---- RegionFile.DecompressInto: one pooled buffer, no stream copies ----

        private static byte[] OldDecompressBrotli(byte[] fileBytes)
        {
            // RegionFile.Decompress's Kind 1 branch before the change
            int hs = StructIo.SizeOf<FileHeader>();
            using var outMs = new MemoryStream(fileBytes.Length * 6);
            outMs.Write(fileBytes, 0, hs);

            using (var br = new System.IO.Compression.BrotliStream(new MemoryStream(fileBytes, hs, fileBytes.Length - hs, false), System.IO.Compression.CompressionMode.Decompress))
            {
                br.CopyTo(outMs);
            }

            var logical = outMs.ToArray();
            logical[6] = 0;
            logical[7] = 0;

            return logical;
        }

        [Fact]
        public void DecompressInto_Matches_Old_Decode_And_Reuses_Buffer()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuo-region-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var rnd = new Random(5);
                var header = new RegionHeader { Facet = 0, Rx = 1, Ry = 2, BlocksW = 4, BlocksH = 3 };
                var blocks = new BlockData[12];

                for (int b = 0; b < blocks.Length; b++)
                {
                    if (b == 5)
                    {
                        continue; // an empty block
                    }

                    var land = new LandCell[PackConst.CellsPerBlock];

                    for (int c = 0; c < land.Length; c++)
                    {
                        land[c] = new LandCell { TileId = (ushort)rnd.Next(0x4000), Z = (sbyte)rnd.Next(-128, 127) };
                    }

                    var statics = Enumerable.Range(0, rnd.Next(0, 9)).Select(_ => new StaticCell { Graphic = (ushort)rnd.Next(1, 0x4000), X = (byte)rnd.Next(8), Y = (byte)rnd.Next(8), Z = (sbyte)rnd.Next(-20, 60), Hue = (ushort)rnd.Next(3000) }).ToArray();
                    blocks[b] = new BlockData(land, statics);
                }

                string raw = Path.Combine(dir, "raw.bin"), brotli = Path.Combine(dir, "brotli.bin");
                RegionFile.Write(raw, header, blocks, false);
                RegionFile.Write(brotli, header, blocks, true);
                var rawBytes = File.ReadAllBytes(raw);
                var brotliBytes = File.ReadAllBytes(brotli);

                // a too-small buffer grows, a big one is reused as is
                byte[] buffer = new byte[16];
                int n = RegionFile.DecompressInto(brotliBytes, ref buffer);
                buffer.AsSpan(0, n).SequenceEqual(OldDecompressBrotli(brotliBytes)).Should().BeTrue();
                buffer.AsSpan(0, n).SequenceEqual(rawBytes).Should().BeTrue();

                var big = new byte[n * 3];
                var same = big;
                RegionFile.DecompressInto(rawBytes, ref big).Should().Be(rawBytes.Length);
                big.Should().BeSameAs(same);
                RegionFile.Decompress(brotliBytes).Should().Equal(rawBytes);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void DecompressInto_Matches_Old_Decode_On_Pack_Regions()
        {
            if (!HavePack)
            {
                return;
            }

            byte[] buffer = null;

            foreach (var (rx, ry) in new[] { (0, 0), (2, 3), (5, 1) })
            {
                var path = PackConst.RegionPath(Path.Combine(PackDir, "map", "0"), rx, ry);
                var file = File.ReadAllBytes(path);
                int n = RegionFile.DecompressInto(file, ref buffer);
                var expected = System.Runtime.InteropServices.MemoryMarshal.Read<FileHeader>(file).Kind == FileKind.Brotli ? OldDecompressBrotli(file) : RegionFile.Decompress(file);
                buffer.AsSpan(0, n).SequenceEqual(expected).Should().BeTrue(path);
            }
        }

        // ---- MapLoader.GetLandZ: cached block, same z as TryGetBlock, survives region eviction ----

        [Fact]
        public void GetLandZ_Matches_TryGetBlock_Across_Region_Evictions()
        {
            if (!HavePack)
            {
                return;
            }

            ClientVersionHelper.IsClientVersionValid("7.0.103.0", out var cv);
            using var pack = PackAssets.OpenBefore12ForTests(PackDir);
            var fm = new UOFileManager(cv, Path.GetTempPath(), pack);
            fm.Maps.Load();
            var maps = fm.Maps;

            sbyte Expected(int x, int y)
            {
                if (x < 0 || y < 0 || !maps.TryGetBlock(0, x >> 3, y >> 3, out var block))
                {
                    return -125;
                }

                return block.Land[((y & 7) << 3) + (x & 7)].Z;
            }

            // walk across 40 regions (more than the 24 cached), checking a few cells in each, then come back
            for (int pass = 0; pass < 2; pass++)
            {
                for (int r = 0; r < 40; r++)
                {
                    int bx = (r % 8) * 64 * 8 + 37, by = (r / 8) * 64 * 8 + 11;

                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (8, 8), (-1, -1), (7, 7) })
                    {
                        maps.GetLandZ(0, bx + dx, by + dy).Should().Be(Expected(bx + dx, by + dy));
                    }
                }
            }

            maps.GetLandZ(0, -1, 5).Should().Be(-125);
            maps.GetLandZ(0, 100000, 5).Should().Be(-125);
            maps.GetLandZ(99, 5, 5).Should().Be(-125);
        }

        // ---- LruCache (anim palettes) ----

        [Fact]
        public void LruCache_Evicts_Least_Recently_Used_And_Returns_It()
        {
            var lru = new LruCache<int, string>(3);
            lru.Add(1, "a", out _).Should().BeFalse();
            lru.Add(2, "b", out _).Should().BeFalse();
            lru.Add(3, "c", out _).Should().BeFalse();
            lru.TryGet(1, out _).Should().BeTrue(); // 2 is now the oldest

            lru.Add(4, "d", out var evicted).Should().BeTrue();
            evicted.Should().Be("b");
            lru.TryGet(2, out _).Should().BeFalse();
            lru.TryGet(1, out var one).Should().BeTrue();
            one.Should().Be("a");
            lru.Count.Should().Be(3);

            lru.Add(3, "c2", out _).Should().BeFalse(); // replacing does not evict
            lru.TryGet(3, out var three).Should().BeTrue();
            three.Should().Be("c2");
        }
    }
}
