using System;
using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // PixelPicker's flat table for small keys (art and gump ids) must answer every query exactly as the dictionary did.
    public class PixelPickerDenseTests
    {
        private static uint[] RandomImage(Random rnd, int w, int h)
        {
            var pixels = new uint[w * h];
            int fill = rnd.Next(0, 101);

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = rnd.Next(100) < fill ? (uint)rnd.Next(1, int.MaxValue) : 0u;
            }

            return pixels;
        }

        private static void AssertSame(PixelPicker expected, PixelPicker actual, ulong id, int w, int h)
        {
            expected.GetDimensions(id, out int ew, out int eh);
            actual.GetDimensions(id, out int aw, out int ah);
            (aw, ah).Should().Be((ew, eh), $"dimensions of {id}");

            for (int y = -2; y < h + 2; y++)
            {
                for (int x = -2; x < w + 2; x++)
                {
                    actual.Get(id, x, y).Should().Be(expected.Get(id, x, y), $"id {id} at {x},{y}");
                    actual.Get(id, x, y, 1).Should().Be(expected.Get(id, x, y, 1), $"id {id} at {x},{y} range 1");
                }
            }
        }

        // m_Data is a byte array doubled when full (256 KB at first): entries written across several growths read back
        // exactly as their pixels, through both the flat table and the dictionary, also after Clear refills it
        [Fact]
        public void Entries_Survive_Data_Growth()
        {
            var rnd = new Random(777);
            var picker = new PixelPicker(64);
            var images = new (ulong id, int w, int h, uint[] px)[48];

            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < images.Length; i++)
                {
                    int w = rnd.Next(100, 300), h = rnd.Next(100, 300);
                    ulong id = i % 2 == 0 ? (ulong)i : 1000UL + (ulong)i;
                    images[i] = (id, w, h, RandomImage(rnd, w, h));
                    picker.Set(id, w, h, images[i].px);
                }

                foreach (var (id, w, h, px) in images)
                {
                    picker.GetDimensions(id, out int gw, out int gh);
                    (gw, gh).Should().Be((w, h));

                    for (int y = 0; y < h; y += 3)
                    {
                        for (int x = 0; x < w; x += 3)
                        {
                            int i = y * w + x;
                            bool expected = px[i] != 0;
                            picker.Get(id, x, y).Should().Be(expected, $"id {id} at {x},{y} round {round}");
                        }
                    }
                }

                picker.Clear();
            }
        }

        [Fact]
        public void Dense_And_Dictionary_Agree()
        {
            const int Capacity = 300;
            var rnd = new Random(12345);
            var dict = new PixelPicker();
            var dense = new PixelPicker(Capacity);
            var ids = new ulong[] { 0, 1, 5, 255, 256, 299, 300, 301, 5000, 0x1_0000_0001 };
            var sizes = new (int w, int h)[ids.Length];

            for (int i = 0; i < ids.Length; i++)
            {
                int w = rnd.Next(1, 200);
                int h = rnd.Next(1, 40);
                sizes[i] = (w, h);
                uint[] pixels = RandomImage(rnd, w, h);

                if (i % 2 == 0)
                {
                    dict.Set(ids[i], w, h, pixels);
                    dense.Set(ids[i], w, h, pixels);
                }
                else
                {
                    int tx = rnd.Next(0, w), ty = rnd.Next(0, h);
                    int tw = rnd.Next(0, w - tx + 1), th = rnd.Next(0, h - ty + 1);

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            if (x < tx || x >= tx + tw || y < ty || y >= ty + th)
                            {
                                pixels[y * w + x] = 0;
                            }
                        }
                    }

                    dict.SetTrimmed(ids[i], w, h, tx, ty, tw, th, pixels);
                    dense.SetTrimmed(ids[i], w, h, tx, ty, tw, th, pixels);
                }

                // a second Set of the same key is ignored by both
                dict.Set(ids[i], 3, 3, new uint[9]);
                dense.Set(ids[i], 3, 3, new uint[9]);
            }

            for (int i = 0; i < ids.Length; i++)
            {
                AssertSame(dict, dense, ids[i], sizes[i].w, sizes[i].h);
            }

            foreach (ulong missing in new ulong[] { 2, 254, 298, 302, 9999, ulong.MaxValue })
            {
                AssertSame(dict, dense, missing, 4, 4);
            }

            dict.Clear();
            dense.Clear();

            foreach (ulong id in ids)
            {
                dense.Get(id, 0, 0).Should().BeFalse();
                dense.GetDimensions(id, out int w, out int h);
                (w, h).Should().Be((0, 0));
            }

            // keys set again after Clear point at the new data
            uint[] full = new uint[16];
            Array.Fill(full, 1u);
            dict.Set(5, 4, 4, full);
            dense.Set(5, 4, 4, full);
            AssertSame(dict, dense, 5, 4, 4);
        }
    }
}
