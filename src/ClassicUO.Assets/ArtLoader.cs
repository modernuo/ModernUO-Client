// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class ArtLoader : UOFileLoader
    {
        public const int MAX_LAND_DATA_INDEX_COUNT = 0x4000;
        public const int MAX_STATIC_DATA_INDEX_COUNT = 0x14000;

        private static ArtEntry _invalid;

        private SpriteEntry[] _sprites = Array.Empty<SpriteEntry>();
        private ArtEntry[] _entries = Array.Empty<ArtEntry>();

        public ArtLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public int Count => _sprites.Length;

        public SpriteEntry[] PackSpriteEntries => _sprites;

        /// <summary>Per-sprite draw state (anchor offsets, animated-static frame offset), indexed like the sprite table.</summary>
        public ArtEntry[] Entries => _entries;

        public override void Load()
        {
            _sprites = SpriteTable.Read(FileManager.Pack.PathOf("art/sprites.bin"));
            _entries = new ArtEntry[_sprites.Length];
        }

        public bool Exists(int idx) => (uint)idx < (uint)_sprites.Length && _sprites[idx].OrigW != 0 && _sprites[idx].OrigH != 0;

        public ref ArtEntry GetEntry(int idx)
        {
            if (!Exists(idx))
            {
                _invalid = default;

                return ref _invalid;
            }

            return ref _entries[idx];
        }

        public ArtInfo GetArt(uint idx)
        {
            if (!Exists((int)idx))
            {
                return default;
            }

            ref readonly var sprite = ref _sprites[idx];

            return new ArtInfo()
            {
                Pixels = PackSprites.DecodeColor(FileManager.Pack.Art, in sprite),
                Width = sprite.OrigW,
                Height = sprite.OrigH
            };
        }
    }

    public struct ArtEntry
    {
        public int Width;
        public int Height;
        public sbyte AnimOffset;
    }

    public ref struct ArtInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
    }
}
