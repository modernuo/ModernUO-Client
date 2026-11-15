// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class TexmapsLoader : UOFileLoader
    {
        public const int MAX_LAND_TEXTURES_DATA_INDEX_COUNT = 0x4000;

        private SpriteEntry[] _sprites = Array.Empty<SpriteEntry>();

        public TexmapsLoader(UOFileManager fileManager) : base(fileManager) { }

        public int Count => _sprites.Length;

        public SpriteEntry[] PackSpriteEntries => _sprites;

        public override void Load()
        {
            _sprites = SpriteTable.Read(FileManager.Pack.PathOf("tex/texmaps.bin"));
        }

        public bool Exists(int idx) => (uint)idx < (uint)_sprites.Length && _sprites[idx].OrigW != 0 && _sprites[idx].OrigH != 0;

        public TexmapInfo GetTexmap(uint idx)
        {
            if (!Exists((int)idx))
            {
                return default;
            }

            ref readonly var sprite = ref _sprites[idx];

            return new TexmapInfo()
            {
                Pixels = PackSprites.DecodeColor(FileManager.Pack.Tex, in sprite),
                Width = sprite.OrigW,
                Height = sprite.OrigH
            };
        }
    }

    public ref struct TexmapInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
    }
}
