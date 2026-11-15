using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Game.GameObjects
{
    // Statics and multi components reject the mouse outside their art before the pixel picker (most drawn objects are not
    // under the mouse). The reject must never change a pick: wherever it rejects, the picker misses too.
    public class StaticPickBoundsTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        [Theory]
        [InlineData(44, 44)]
        [InlineData(45, 44)]
        [InlineData(44, 106)]
        [InlineData(1, 1)]
        [InlineData(87, 130)]
        [InlineData(88, 45)]
        public void The_Static_Reject_Covers_The_Mask_Of_Any_Size(int w, int h)
        {
            // what View/ChunkMesh/ItemSpriteExtract store on the art entry for a w x h sprite
            int ew = (short)((w >> 1) - 22), eh = (short)(h - 44);
            var picker = new PixelPicker(16);
            var pixels = new uint[w * h];
            pixels.AsSpan().Fill(0xFF00FF00);
            picker.Set(3, w, h, pixels);

            for (int y = -50; y < h + 50; y++)
            {
                for (int x = -50; x < w + 50; x++)
                {
                    bool hit = picker.Get(3, x, y);
                    bool inMask = x >= 0 && y >= 0 && x < w && y < h;
                    // the picker never hits outside its size (nor at pixel 0, 0 of any mask: its run walk starts there)
                    if (hit)
                    {
                        inMask.Should().BeTrue();
                    }

                    if (Static.OutsidePickBounds(ew, eh, x, y))
                    {
                        hit.Should().BeFalse($"{w}x{h} pixel {x},{y}");
                    }

                    Multi.OutsideSprite(w, h, x, y).Should().Be(!inMask, $"{w}x{h} pixel {x},{y}");
                }
            }

            // the static bound is at most two columns wider than the mask and as tall
            Static.OutsidePickBounds(ew, eh, w + 2, 0).Should().BeTrue();
            Static.OutsidePickBounds(ew, eh, 0, h).Should().BeTrue();
            Static.OutsidePickBounds(ew, eh, -1, 0).Should().BeTrue();
            Static.OutsidePickBounds(ew, eh, 0, -1).Should().BeTrue();
            Static.OutsidePickBounds(ew, eh, w - 1, h - 1).Should().BeFalse();
        }

        [Fact]
        public void The_Rejects_Never_Drop_A_Picker_Hit_On_Pack_Art()
        {
            if (!File.Exists(Path.Combine(PackDir, "manifest.json")))
            {
                return;
            }

            var sprites = SpriteTable.Read(Path.Combine(PackDir, "art", "sprites.bin"));
            var pages = new PackPages(Path.Combine(PackDir, "art"), 256 << 20);
            var picker = new PixelPicker(sprites.Length);
            var ids = new List<int>();

            for (int i = 0x4000; i < sprites.Length; i++)
            {
                ref readonly var e = ref sprites[i];

                if (e.OrigW != 0 && e.OrigH != 0 && i % 61 == 0)
                {
                    ids.Add(i);
                }
            }

            ids.Count.Should().BeGreaterThan(300);
            int hits = 0, rejected = 0;

            foreach (int i in ids)
            {
                ref readonly var e = ref sprites[i];
                var pixels = PackSprites.DecodeColor(pages, in e);
                var t = PackSprites.Trim(in e);
                ulong id = (ulong)(i - 0x4000);
                picker.SetTrimmed(id, e.OrigW, e.OrigH, t.X, t.Y, t.W, t.H, pixels);
                int ew = (short)((e.OrigW >> 1) - 22), eh = (short)(e.OrigH - 44);
                bool sized = (ew | eh) != 0;

                for (int y = -8; y < e.OrigH + 8; y++)
                {
                    for (int x = -8; x < e.OrigW + 8; x++)
                    {
                        bool hit = picker.Get(id, x, y);
                        hits += hit ? 1 : 0;

                        if (sized && Static.OutsidePickBounds(ew, eh, x, y))
                        {
                            rejected++;
                            hit.Should().BeFalse($"static art 0x{id:X} ({e.OrigW}x{e.OrigH}) pixel {x},{y}");
                        }

                        if (Multi.OutsideSprite(e.OrigW, e.OrigH, x, y))
                        {
                            hit.Should().BeFalse($"multi art 0x{id:X} ({e.OrigW}x{e.OrigH}) pixel {x},{y}");
                        }
                    }
                }
            }

            hits.Should().BeGreaterThan(10000);
            rejected.Should().BeGreaterThan(10000);
        }
    }
}
