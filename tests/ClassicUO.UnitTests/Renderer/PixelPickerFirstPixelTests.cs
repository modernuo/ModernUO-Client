using System;
using System.Collections.Generic;
using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // PixelPicker.Get hits exactly the opaque texels. The inherited run walk stopped on a run boundary and answered
    // "miss", so pixel 0,0 and the first texel of every opaque run were never pickable (owner decision 2026-11-02: fix).
    // Every way the client fills a picker is covered: Art (flat table, SetTrimmed to the visible box), Gump (flat table,
    // SetTrimmed over the whole image), Animation (dictionary, 64-bit keys, SetTrimmed), Texmap and RenderedText
    // (dictionary, Set), each with the SIMD and the scalar run writer.
    public class PixelPickerFirstPixelTests
    {
        public enum Source
        {
            Art,
            Gump,
            Animation,
            Texmap,
            RenderedText,
            DenseSet
        }

        public static IEnumerable<object[]> Sources()
        {
            foreach (Source s in Enum.GetValues<Source>())
            {
                yield return new object[] { s, false };
                yield return new object[] { s, true };
            }
        }

        private static ulong KeyFor(Source source, int i) => source switch
        {
            Source.Art => (ulong)i,
            Source.Gump => (ulong)(i * 3),
            Source.DenseSet => (ulong)(i + 1),
            // Animation.PickerKey packs body, action, direction and frame into 64 bits
            Source.Animation => ((ulong)(0x190 + i) << 32) | (3UL << 16) | (2UL << 8) | 1UL,
            _ => 0x1000UL + (ulong)i
        };

        private static PixelPicker NewPicker(Source source) => source switch
        {
            Source.Art or Source.Gump or Source.DenseSet => new PixelPicker(4096),
            _ => new PixelPicker()
        };

        private static void Fill(PixelPicker picker, Source source, ulong key, int w, int h, uint[] px)
        {
            switch (source)
            {
                case Source.Art:
                case Source.Animation:
                {
                    // the pack trim is the visible box; the picker gets the full image
                    PixelPicker.VisibleBounds(px, w, h, 0, 0, w, h, out int x0, out int y0, out int x1, out int y1);
                    int tw = x1 >= x0 ? x1 - x0 + 1 : 0;
                    int th = y1 >= y0 ? y1 - y0 + 1 : 0;
                    picker.SetTrimmed(key, w, h, tw > 0 ? x0 : 0, th > 0 ? y0 : 0, tw, th, px);

                    break;
                }

                case Source.Gump:
                    picker.SetTrimmed(key, w, h, 0, 0, w, h, px);

                    break;

                default:
                    picker.Set(key, w, h, px);

                    break;
            }
        }

        // what the inherited walk answered: a hit only on an opaque texel that follows an opaque texel (row-major)
        private static bool OldAnswer(uint[] px, int i) => i > 0 && px[i] != 0 && px[i - 1] != 0;

        private static bool IsOpaqueRunStart(uint[] px, int i) => px[i] != 0 && (i == 0 || px[i - 1] == 0);

        private static uint[] Image(int w, int h, Func<int, int, bool> opaque)
        {
            var px = new uint[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    px[y * w + x] = opaque(x, y) ? 0xFF00FF00u : 0u;
                }
            }

            return px;
        }

        private static List<(int w, int h, uint[] px)> Images()
        {
            var rnd = new Random(20261102);
            var list = new List<(int, int, uint[])>
            {
                (1, 1, Image(1, 1, (_, _) => true)),
                (1, 1, Image(1, 1, (_, _) => false)),
                (5, 4, Image(5, 4, (_, _) => true)),
                (5, 4, Image(5, 4, (_, _) => false)),
                // pixel 0,0 opaque, the rest transparent
                (6, 5, Image(6, 5, (x, y) => x == 0 && y == 0)),
                // a single texel in the middle, and one in the last position
                (7, 7, Image(7, 7, (x, y) => x == 3 && y == 3)),
                (7, 7, Image(7, 7, (x, y) => x == 6 && y == 6)),
                // 1-px-thin vertical edge: every texel is the first of its run
                (9, 12, Image(9, 12, (x, _) => x == 4)),
                (9, 12, Image(9, 12, (x, _) => x == 0)),
                (9, 12, Image(9, 12, (x, _) => x == 8)),
                // checkerboard: every opaque texel is a run start
                (11, 10, Image(11, 10, (x, y) => ((x + y) & 1) == 0)),
                (11, 10, Image(11, 10, (x, y) => ((x + y) & 1) == 1)),
                // runs longer than 127 (two-byte varints) and runs crossing rows
                (300, 3, Image(300, 3, (x, y) => x >= 10 && x < 290 || y == 1)),
                (40, 40, Image(40, 40, (x, y) => (x - 20) * (x - 20) + (y - 20) * (y - 20) < 300)),
                // diamond like a land/static tile
                (44, 44, Image(44, 44, (x, y) => Math.Abs(x - 21.5) + Math.Abs(y - 21.5) <= 22))
            };

            for (int n = 0; n < 24; n++)
            {
                int w = rnd.Next(1, 90), h = rnd.Next(1, 60);
                int fill = rnd.Next(0, 101);
                list.Add((w, h, Image(w, h, (_, _) => rnd.Next(100) < fill)));
            }

            return list;
        }

        [Theory]
        [MemberData(nameof(Sources))]
        public void Get_Hits_Exactly_The_Opaque_Texels(Source source, bool scalar)
        {
            List<(int w, int h, uint[] px)> images = Images();
            PixelPicker picker = NewPicker(source);
            bool saved = PixelPicker.ForceScalar;

            try
            {
                PixelPicker.ForceScalar = scalar;

                for (int k = 0; k < images.Count; k++)
                {
                    Fill(picker, source, KeyFor(source, k), images[k].w, images[k].h, images[k].px);
                }
            }
            finally
            {
                PixelPicker.ForceScalar = saved;
            }

            for (int k = 0; k < images.Count; k++)
            {
                (int w, int h, uint[] px) = images[k];
                ulong key = KeyFor(source, k);
                int runStarts = 0;

                for (int y = -2; y < h + 2; y++)
                {
                    for (int x = -2; x < w + 2; x++)
                    {
                        bool got = picker.Get(key, x, y);

                        if (x < 0 || y < 0 || x >= w || y >= h)
                        {
                            got.Should().BeFalse($"{source} image {k} ({w}x{h}) outside at {x},{y}");

                            continue;
                        }

                        int i = y * w + x;

                        if (px[i] == 0)
                        {
                            got.Should().BeFalse($"{source} image {k} ({w}x{h}) transparent at {x},{y}");
                        }
                        else if (IsOpaqueRunStart(px, i))
                        {
                            runStarts++;
                            got.Should().BeTrue($"{source} image {k} ({w}x{h}) first texel of an opaque run at {x},{y}");
                        }
                        else
                        {
                            // every other texel keeps the old answer (including the last texel of a run)
                            got.Should().Be(OldAnswer(px, i), $"{source} image {k} ({w}x{h}) at {x},{y}");
                            got.Should().BeTrue();
                        }
                    }
                }

                if (px.AsSpan().ContainsAnyExcept(0u))
                {
                    runStarts.Should().BeGreaterThan(0);
                }
            }
        }

        [Theory]
        [MemberData(nameof(Sources))]
        public void Pixel_Zero_Hits_When_Opaque(Source source, bool scalar)
        {
            PixelPicker picker = NewPicker(source);
            bool saved = PixelPicker.ForceScalar;

            uint[] corner = Image(4, 3, (x, y) => x == 0 && y == 0);
            uint[] full = Image(4, 3, (_, _) => true);
            uint[] none = Image(4, 3, (x, y) => x != 0 || y != 0);

            try
            {
                PixelPicker.ForceScalar = scalar;
                Fill(picker, source, KeyFor(source, 1), 4, 3, corner);
                Fill(picker, source, KeyFor(source, 2), 4, 3, full);
                Fill(picker, source, KeyFor(source, 3), 4, 3, none);
            }
            finally
            {
                PixelPicker.ForceScalar = saved;
            }

            picker.Get(KeyFor(source, 1), 0, 0).Should().BeTrue();
            picker.Get(KeyFor(source, 1), 1, 0).Should().BeFalse();
            picker.Get(KeyFor(source, 2), 0, 0).Should().BeTrue();
            picker.Get(KeyFor(source, 2), 3, 2).Should().BeTrue("the last texel of a run still hits");
            picker.Get(KeyFor(source, 3), 0, 0).Should().BeFalse();
            picker.Get(KeyFor(source, 3), 1, 0).Should().BeTrue("first texel of the opaque run after 0,0");
        }

        [Theory]
        [MemberData(nameof(Sources))]
        public void Thin_Vertical_Edge_Is_Pickable(Source source, bool scalar)
        {
            PixelPicker picker = NewPicker(source);
            bool saved = PixelPicker.ForceScalar;
            uint[] line = Image(5, 8, (x, _) => x == 2);

            try
            {
                PixelPicker.ForceScalar = scalar;
                Fill(picker, source, KeyFor(source, 7), 5, 8, line);
            }
            finally
            {
                PixelPicker.ForceScalar = saved;
            }

            for (int y = 0; y < 8; y++)
            {
                picker.Get(KeyFor(source, 7), 2, y).Should().BeTrue($"row {y}");
                picker.Get(KeyFor(source, 7), 1, y).Should().BeFalse();
                picker.Get(KeyFor(source, 7), 3, y).Should().BeFalse();
            }
        }
    }
}
