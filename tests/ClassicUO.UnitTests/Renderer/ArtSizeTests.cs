using System.Collections.Generic;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer.Arts;
using StbRectPackSharp;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // perf-6: item controls (ItemGump, StaticPic, ...) size themselves from Art.TryGetArtSize instead of decoding the art at
    // construction. The size must be the UV size GetArt would give, placeholder included, and reading it must not decode.
    [Collection("ArtStatics")]
    public class ArtSizeTests
    {
        private static Art MakeArt(SpriteEntry[] sprites)
        {
            var arts = new ArtLoader(new UOFileManager(default, Path.GetTempPath(), null));
            arts.SetTablesForTests(sprites);

            return new Art(arts, null, null);
        }

        [Fact]
        public void The_Size_Comes_From_The_Sprite_Table_Without_A_Decode()
        {
            var sprites = new SpriteEntry[0x4000 + 0x100];
            sprites[0x4000 + 0x10] = new SpriteEntry { OrigW = 30, OrigH = 41 };
            var art = MakeArt(sprites);
            int syncBefore = Art.TotalSyncLoads, missingBefore = Art.TotalMissingLogs;

            art.TryGetArtSize(0x10, out int w, out int h).Should().BeTrue();

            w.Should().Be(30);
            h.Should().Be(41);
            art.NeedsDecode(0x10).Should().BeTrue("the sprite is still not in the atlas");
            (Art.TotalSyncLoads - syncBefore).Should().Be(0);
            (Art.TotalMissingLogs - missingBefore).Should().Be(0);
        }

        [Fact]
        public void Missing_Art_Gets_The_Placeholder_Size_Without_A_Decode_Or_Log()
        {
            var sprites = new SpriteEntry[0x4000 + 0x100];
            sprites[0] = new SpriteEntry { OrigW = 44, OrigH = 44 };
            var art = MakeArt(sprites);
            int syncBefore = Art.TotalSyncLoads, missingBefore = Art.TotalMissingLogs;

            art.TryGetArtSize(0x42, out int w, out int h).Should().BeTrue();

            w.Should().Be(44);
            h.Should().Be(44);
            (Art.TotalSyncLoads - syncBefore).Should().Be(0);
            (Art.TotalMissingLogs - missingBefore).Should().Be(0, "the miss is logged by the first draw, as before");
        }

        [Fact]
        public void No_Size_Where_GetArt_Has_No_Texture()
        {
            var sprites = new SpriteEntry[0x4000 + 0x100];
            var art = MakeArt(sprites);

            // missing, and no placeholder either
            art.TryGetArtSize(0x42, out _, out _).Should().BeFalse();
            // past the table: GetArt returns SpriteInfo.Empty
            art.TryGetArtSize(0x100, out int w, out int h).Should().BeFalse();
            w.Should().Be(0);
            h.Should().Be(0);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(44, 44)]
        [InlineData(87, 130)]
        [InlineData(255, 31)]
        public void The_Atlas_Rect_Is_The_Decoded_Size(int w, int h)
        {
            // GetArt's UV is the rect TextureAtlas.AddSprite packs for the decoded width and height
            using var packer = new Packer(512, 512);

            packer.PackRect(w, h, out var rect).Should().BeTrue();

            rect.Width.Should().Be(w);
            rect.Height.Should().Be(h);
        }

        [Fact]
        public void Table_Sizes_Match_Decoded_Pack_Art()
        {
            string current = @"C:\Repositories\uo-packs\CURRENT";

            if (!File.Exists(current))
            {
                return;
            }

            string packDir = Path.Combine(@"C:\Repositories\uo-packs", File.ReadAllText(current).Trim());

            if (!File.Exists(Path.Combine(packDir, "manifest.json")))
            {
                return;
            }

            var sprites = SpriteTable.Read(Path.Combine(packDir, "art", "sprites.bin"));
            var pages = new PackPages(Path.Combine(packDir, "art"), 256 << 20);
            var art = MakeArt(sprites);
            var checkedIds = new List<int>();
            int missing = 0;

            for (int i = 0x4000; i < sprites.Length; i += 37)
            {
                ref readonly var e = ref sprites[i];
                uint graphic = (uint)(i - 0x4000);

                art.TryGetArtSize(graphic, out int w, out int h).Should().BeTrue($"art 0x{graphic:X}");

                if (e.OrigW == 0 || e.OrigH == 0)
                {
                    // missing art: Get's placeholder, sprite 0
                    w.Should().Be(sprites[0].OrigW);
                    h.Should().Be(sprites[0].OrigH);
                    missing++;

                    continue;
                }

                // what Art.Get passes to AddSprite: the decoded image's width and height
                var pixels = PackSprites.DecodeColor(pages, in e);
                pixels.Length.Should().Be(w * h, $"art 0x{graphic:X}");
                w.Should().Be(e.OrigW);
                h.Should().Be(e.OrigH);
                checkedIds.Add(i);
            }

            checkedIds.Count.Should().BeGreaterThan(300);
            art.NeedsDecode((uint)(checkedIds[0] - 0x4000)).Should().BeTrue();
        }
    }
}
