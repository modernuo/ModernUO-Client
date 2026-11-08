using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

        [Fact]
        public void DecodeMapImage_Reads_A_Cached_Png_As_Rgba_And_Returns_Null_For_A_Bad_File()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuo-wm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                string png = Path.Combine(dir, "map.png");

                using (var src = new Image<Rgba32>(5, 3))
                {
                    src[0, 0] = new Rgba32(10, 20, 30, 255);
                    src[4, 2] = new Rgba32(200, 100, 50, 255);
                    src.SaveAsPng(png);
                }

                using (Image<Rgba32> decoded = WorldMapGump.DecodeMapImage(png))
                {
                    decoded.Should().NotBeNull();
                    decoded.Width.Should().Be(5);
                    decoded.Height.Should().Be(3);
                    decoded.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> pixels).Should().BeTrue("the upload needs one span");
                    pixels.Span[0].Should().Be(new Rgba32(10, 20, 30, 255));
                    pixels.Span[2 * 5 + 4].Should().Be(new Rgba32(200, 100, 50, 255));
                }

                string bad = Path.Combine(dir, "bad.png");
                File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
                WorldMapGump.DecodeMapImage(bad).Should().BeNull();
                WorldMapGump.DecodeMapImage(Path.Combine(dir, "missing.png")).Should().BeNull();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
