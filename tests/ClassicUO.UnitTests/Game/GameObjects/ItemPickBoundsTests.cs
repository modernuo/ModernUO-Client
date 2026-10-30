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
    // The item pick test rejects the mouse outside the art's drawn rectangle before it looks at the art. With first-sight
    // item art decoded on workers (FirstSightArt), that rectangle comes from the pack's sprite size before the first draw
    // (and for 44 x 44 art, whose offset is 0, 0), so the reject must never drop a pixel the picker would hit: every
    // visible pixel of the art, and every pixel 5 px down-right of the mouse (a stack's second sprite), is inside it.
    public class ItemPickBoundsTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        [Fact]
        public void The_Reject_From_The_Pack_Size_Never_Drops_A_Picker_Hit()
        {
            if (!File.Exists(Path.Combine(PackDir, "manifest.json")))
            {
                return;
            }

            var sprites = SpriteTable.Read(Path.Combine(PackDir, "art", "sprites.bin"));
            var pages = new PackPages(Path.Combine(PackDir, "art"), 256 << 20);
            var picker = new PixelPicker();
            var ids = new List<int>();
            int square = 0;

            for (int i = 0x4000; i < sprites.Length; i++)
            {
                ref readonly var e = ref sprites[i];

                if (e.OrigW == 0 || e.OrigH == 0)
                {
                    continue;
                }

                // every 44 x 44 sprite (offset 0, 0: the case the reject used to skip) and a sample of the rest
                if (e.OrigW == 44 && e.OrigH == 44 ? square++ < 200 : i % 97 == 0)
                {
                    ids.Add(i);
                }
            }

            ids.Count.Should().BeGreaterThan(300);
            square.Should().BeGreaterThan(0);

            foreach (int i in ids)
            {
                ref readonly var e = ref sprites[i];
                var pixels = PackSprites.DecodeColor(pages, in e);
                var t = PackSprites.Trim(in e);
                ulong id = (ulong)(i - 0x4000);
                picker.SetTrimmed(id, e.OrigW, e.OrigH, t.X, t.Y, t.W, t.H, pixels);
                Item.PickEntryFromSize(e.OrigW, e.OrigH, out int ew, out int eh);

                for (int y = -8; y < e.OrigH + 8; y++)
                {
                    for (int x = -8; x < e.OrigW + 8; x++)
                    {
                        if (picker.Get(id, x, y))
                        {
                            Item.OutsidePickBounds(ew, eh, x, y).Should().BeFalse($"art 0x{i - 0x4000:X} ({e.OrigW}x{e.OrigH}) pixel {x},{y}");
                            Item.OutsidePickBounds(ew, eh, x - 5, y - 5).Should().BeFalse($"art 0x{i - 0x4000:X} stack pixel {x},{y}");
                        }
                    }
                }
            }
        }

        // A multi component's pick test anchors its pixel mask where View.DrawStaticAnimated draws the sprite, from the
        // pack's size, before any draw has stored the anchor on the art entry (the first-sight prefetch can fill the mask
        // first). The wooden wall 0x0006 is 44 x 106: with the unset anchor (0, 0) its mask sat 62 px too high.
        [Fact]
        public void A_Multi_Picks_The_Pixels_Its_View_Draws_Before_The_First_Draw()
        {
            if (!File.Exists(Path.Combine(PackDir, "manifest.json")))
            {
                return;
            }

            var sprites = SpriteTable.Read(Path.Combine(PackDir, "art", "sprites.bin"));
            var pages = new PackPages(Path.Combine(PackDir, "art"), 64 << 20);
            const ushort wall = 0x0006;
            ref readonly var e = ref sprites[wall + 0x4000];
            e.OrigW.Should().Be(44);
            e.OrigH.Should().Be(106);

            var picker = new PixelPicker();
            var pixels = PackSprites.DecodeColor(pages, in e);
            var t = PackSprites.Trim(in e);
            picker.SetTrimmed(wall, e.OrigW, e.OrigH, t.X, t.Y, t.W, t.H, pixels);

            // View.DrawStaticAnimated: index.Width = (UV.Width >> 1) - 22, index.Height = UV.Height - 44; x -= Width, y -= Height
            const int x = 500, y = 400;
            int drawLeft = x - ((e.OrigW >> 1) - 22), drawTop = y - (e.OrigH - 44);
            Multi.SpriteTopLeft(x, y, e.OrigW, e.OrigH, out int left, out int top);
            left.Should().Be(drawLeft);
            top.Should().Be(drawTop);
            (top - y).Should().Be(-62);

            int hits = 0, oldMisses = 0;

            for (int py = 0; py < e.OrigH; py++)
            {
                for (int px = 0; px < e.OrigW; px++)
                {
                    if (!picker.Get(wall, px, py)) // the pixels the mask (and the art) shows
                    {
                        continue;
                    }

                    // the mouse over a drawn pixel at screen (drawLeft + px, drawTop + py)
                    int mouseX = drawLeft + px, mouseY = drawTop + py;
                    picker.Get(wall, mouseX - left, mouseY - top).Should().BeTrue($"drawn pixel {px},{py}");
                    hits++;

                    if (!picker.Get(wall, mouseX - x, mouseY - y))
                    {
                        oldMisses++; // the anchor (0, 0) the old test used before the first draw
                    }
                }
            }

            hits.Should().BeGreaterThan(500);
            oldMisses.Should().BeGreaterThan(hits / 4);
        }
    }
}
