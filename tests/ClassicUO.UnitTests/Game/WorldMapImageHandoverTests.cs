using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// worldmap-texture: the world map's LoadMap task no longer creates or disposes the texture (FNA3D/SDL_GPU resources
    /// belong to the game thread, and Draw could use a texture the task had disposed). The task decodes the PNG and posts
    /// the image; the gump's Update takes it, uploads the texture and prints "map loaded". These tests cover the handover:
    /// the latest load wins, a closed gump drops late images, and every image is disposed exactly once.
    /// </summary>
    public sealed class WorldMapImageHandoverTests
    {
        private sealed class FakeImage : IDisposable
        {
            private static int _live;
            private int _disposed;

            public FakeImage() => Interlocked.Increment(ref _live);

            public static int Live => Volatile.Read(ref _live);

            public int DisposeCount => _disposed;

            public void Dispose()
            {
                if (Interlocked.Increment(ref _disposed) == 1)
                {
                    Interlocked.Decrement(ref _live);
                }
            }
        }

        [Fact]
        public void Nothing_Posted_Takes_Null()
        {
            new WorldMapGump.MapImageHandover<FakeImage>().Take().Should().BeNull();
        }

        [Fact]
        public void The_Current_Load_Is_Taken_Once_And_Not_Disposed()
        {
            var h = new WorldMapGump.MapImageHandover<FakeImage>();
            int load = h.Request();
            var image = new FakeImage();

            h.Post(load, image).Should().BeTrue();

            h.Take().Should().BeSameAs(image);
            h.Take().Should().BeNull();
            image.DisposeCount.Should().Be(0, "the caller owns a taken image");
        }

        [Fact]
        public void A_Superseded_Load_Is_Dropped_And_The_Latest_Wins()
        {
            var h = new WorldMapGump.MapImageHandover<FakeImage>();
            int first = h.Request();
            int second = h.Request();

            h.IsCurrent(first).Should().BeFalse();
            h.IsCurrent(second).Should().BeTrue();

            var late = new FakeImage();
            var latest = new FakeImage();

            h.Post(second, latest).Should().BeTrue();
            h.Post(first, late).Should().BeFalse();

            late.DisposeCount.Should().Be(1);
            h.Take().Should().BeSameAs(latest);
            latest.DisposeCount.Should().Be(0);
        }

        [Fact]
        public void A_New_Request_Drops_An_Untaken_Image()
        {
            var h = new WorldMapGump.MapImageHandover<FakeImage>();
            var image = new FakeImage();
            h.Post(h.Request(), image).Should().BeTrue();

            h.Request();

            image.DisposeCount.Should().Be(1);
            h.Take().Should().BeNull();
        }

        [Fact]
        public void Close_Drops_The_Pending_Image_And_Every_Later_Post()
        {
            var h = new WorldMapGump.MapImageHandover<FakeImage>();
            int load = h.Request();
            var pending = new FakeImage();
            h.Post(load, pending);

            h.Close();

            pending.DisposeCount.Should().Be(1);
            h.IsCurrent(load).Should().BeFalse();

            var late = new FakeImage();
            h.Post(load, late).Should().BeFalse();
            late.DisposeCount.Should().Be(1);
            h.Take().Should().BeNull();
        }

        [Fact]
        public void Concurrent_Loads_And_Close_Leak_Nothing_And_Dispose_Each_Image_Once()
        {
            int before = FakeImage.Live;
            var h = new WorldMapGump.MapImageHandover<FakeImage>();
            var all = new List<FakeImage>();
            var taken = new List<FakeImage>();
            var posters = new List<Task>();

            for (int i = 0; i < 200; i++)
            {
                int load = h.Request();
                var image = new FakeImage();
                all.Add(image);
                posters.Add(Task.Run(() => h.Post(load, image)));

                FakeImage t = h.Take();

                if (t != null)
                {
                    taken.Add(t);
                }
            }

            h.Close();
            Task.WaitAll(posters.ToArray());

            // the game thread disposes what it took (after uploading it)
            foreach (FakeImage t in taken)
            {
                t.Dispose();
            }

            h.Take().Should().BeNull();
            all.Should().OnlyContain(i => i.DisposeCount == 1);
            FakeImage.Live.Should().Be(before);
        }

        private sealed class FakeTexture : IDisposable
        {
            public int DisposeCount;

            public void Dispose() => DisposeCount++;
        }

        [Fact]
        public void ReplaceMapTexture_Swaps_The_Texture_And_Frees_The_Image()
        {
            var old = new FakeTexture();
            var made = new FakeTexture();
            FakeTexture current = old;
            var image = new FakeImage();

            WorldMapGump.ReplaceMapTexture(ref current, image, _ => made).Should().BeTrue();

            current.Should().BeSameAs(made);
            old.DisposeCount.Should().Be(1);
            made.DisposeCount.Should().Be(0);
            image.DisposeCount.Should().Be(1);
        }

        [Fact]
        public void ReplaceMapTexture_A_Failed_Upload_Is_Logged_And_Leaves_No_Texture()
        {
            var old = new FakeTexture();
            FakeTexture current = old;
            var image = new FakeImage();

            WorldMapGump.ReplaceMapTexture<FakeImage, FakeTexture>(ref current, image, _ => throw new InvalidOperationException("upload failed"))
                .Should().BeFalse("the caller prints 'loaded' only on success");

            current.Should().BeNull("Draw skips a null texture");
            old.DisposeCount.Should().Be(1);
            image.DisposeCount.Should().Be(1);

            var image2 = new FakeImage();
            WorldMapGump.ReplaceMapTexture<FakeImage, FakeTexture>(ref current, image2, _ => null).Should().BeFalse();
            current.Should().BeNull();
            image2.DisposeCount.Should().Be(1);
        }

        /// <summary>The decoder is FNA3D's stb_image; skipped where the native library can't be loaded.</summary>
        private sealed class Fna3dFactAttribute : FactAttribute
        {
            public Fna3dFactAttribute()
            {
                if (!NativeLibrary.TryLoad("FNA3D", typeof(Microsoft.Xna.Framework.Graphics.Texture2D).Assembly, null, out _))
                {
                    Skip = "the FNA3D native library is not next to the tests";
                }
            }
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuo-wm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            return dir;
        }

        private static byte[] MapLikePixels(int width, int height)
        {
            // a transparent one-pixel border (OFFSET_PIX_HALF) around opaque, varied colours, R in the first byte
            var rgba = new byte[width * height * 4];
            uint seed = 12345;

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    seed = seed * 1664525 + 1013904223;
                    int i = (y * width + x) * 4;
                    rgba[i] = (byte)(seed >> 24);
                    rgba[i + 1] = (byte)(seed >> 16);
                    rgba[i + 2] = (byte)(x * 7 + y);
                    rgba[i + 3] = 0xFF;
                }
            }

            return rgba;
        }

        [Fna3dFact]
        public void DecodeMapImage_Round_Trips_A_Png_Written_By_Png_Write()
        {
            string dir = NewTempDir();

            try
            {
                // 300x200 of noise compresses to well over one 64 KB IDAT chunk
                const int W = 300, H = 200;
                byte[] rgba = MapLikePixels(W, H);
                string png = Path.Combine(dir, "map.png");

                using (var stream = ClassicUO.Utility.AtomicFile.OpenWrite(png))
                {
                    Uo.Render.Png.Write(stream, W, H, rgba);
                    stream.Commit();
                }

                using WorldMapImage decoded = WorldMapGump.DecodeMapImage(png);

                decoded.Should().NotBeNull();
                decoded.Width.Should().Be(W);
                decoded.Height.Should().Be(H);
                decoded.ByteLength.Should().Be(rgba.Length);
                decoded.Span.SequenceEqual(rgba).Should().BeTrue("the cache PNG is lossless RGBA8");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // a cache file written by the old ImageSharp encoder (palette PNG with tRNS, no filter, the gump's settings), 6x4:
        // a transparent border around four opaque colours
        private const string OldImageSharpCachePng =
            "iVBORw0KGgoAAAANSUhEUgAAAAYAAAAECAMAAACa2r5xAAAAD1BMVEUAAAAIQMD/////AAAggBDgeqOjAAAABXRSTlMA/////xzQJlIAAAAUSURBVHicY4ABZiYWRiAFJJlhQgABNgAVCnz75gAAAABJRU5ErkJggg==";

        [Fna3dFact]
        public void DecodeMapImage_Reads_A_Cache_Png_Written_By_ImageSharp()
        {
            string dir = NewTempDir();

            try
            {
                string png = Path.Combine(dir, "old.png");
                File.WriteAllBytes(png, Convert.FromBase64String(OldImageSharpCachePng));

                // as ImageSharp decoded it (Rgba32 packed values, R in the low byte)
                uint[] expected =
                {
                    0, 0, 0, 0, 0, 0,
                    0, 0xFF0000FF, 0xFFFFFFFF, 0xFF108020, 0xFFC04008, 0,
                    0, 0xFFFFFFFF, 0xFF108020, 0xFFC04008, 0xFF0000FF, 0,
                    0, 0, 0, 0, 0, 0,
                };

                using WorldMapImage decoded = WorldMapGump.DecodeMapImage(png);

                decoded.Should().NotBeNull();
                decoded.Width.Should().Be(6);
                decoded.Height.Should().Be(4);
                MemoryMarshal.Cast<byte, uint>(decoded.Span).ToArray().Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fna3dFact]
        public void DecodeMapImage_Returns_Null_For_A_Bad_Truncated_Or_Missing_File()
        {
            string dir = NewTempDir();

            try
            {
                string good = Path.Combine(dir, "good.png");
                Uo.Render.Png.Write(good, 64, 64, MapLikePixels(64, 64));
                byte[] bytes = File.ReadAllBytes(good);

                string garbage = Path.Combine(dir, "garbage.png");
                File.WriteAllBytes(garbage, new byte[] { 1, 2, 3 });
                WorldMapGump.DecodeMapImage(garbage).Should().BeNull();

                // a TGA-looking header: stb_image is never asked to try other formats
                string tga = Path.Combine(dir, "tga.png");
                File.WriteAllBytes(tga, new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 32, 0 });
                WorldMapGump.DecodeMapImage(tga).Should().BeNull();

                foreach (int keep in new[] { 8, 20, 33, bytes.Length / 2, bytes.Length - 13 })
                {
                    string truncated = Path.Combine(dir, $"truncated{keep}.png");
                    File.WriteAllBytes(truncated, bytes.AsSpan(0, keep).ToArray());
                    WorldMapGump.DecodeMapImage(truncated).Should().BeNull($"{keep} of {bytes.Length} bytes");
                }

                WorldMapGump.DecodeMapImage(Path.Combine(dir, "missing.png")).Should().BeNull();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fna3dFact]
        public void A_Disposed_Image_Frees_Its_Pixels_Once()
        {
            string dir = NewTempDir();

            try
            {
                string png = Path.Combine(dir, "map.png");
                Uo.Render.Png.Write(png, 4, 4, MapLikePixels(4, 4));

                WorldMapImage image = WorldMapGump.DecodeMapImage(png);
                image.Pixels.Should().NotBe(IntPtr.Zero);

                image.Dispose();
                image.Dispose();

                image.Pixels.Should().Be(IntPtr.Zero);
                image.Span.IsEmpty.Should().BeTrue();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
