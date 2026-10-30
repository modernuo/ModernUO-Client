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
    }
}
