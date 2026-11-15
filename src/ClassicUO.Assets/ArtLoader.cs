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

        /// <summary>Tests: the sprite table without a pack on disk.</summary>
        internal void SetTablesForTests(SpriteEntry[] sprites)
        {
            _sprites = sprites;
            _entries = new ArtEntry[sprites.Length];
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

        public ArtInfo GetArt(uint idx) => GetArt(idx, null);

        /// <summary>Texels in sprite <paramref name="idx"/>'s untrimmed image (0 when absent), the buffer size <see cref="GetArt(uint, uint[])"/> needs.</summary>
        public int PixelCount(uint idx) => Exists((int)idx) ? _sprites[idx].OrigW * _sprites[idx].OrigH : 0;

        /// <summary>Decodes into <paramref name="buffer"/> (at least <see cref="PixelCount"/> long; null = a new array).</summary>
        public ArtInfo GetArt(uint idx, uint[] buffer)
        {
            if (!Exists((int)idx))
            {
                return default;
            }

            ref readonly var sprite = ref _sprites[idx];
            var pixels = buffer == null ? new uint[sprite.OrigW * sprite.OrigH] : buffer.AsSpan(0, sprite.OrigW * sprite.OrigH);
            PackSprites.DecodeColor(FileManager.Pack.Art, in sprite, pixels);
            var trim = PackSprites.Trim(in sprite);

            return new ArtInfo()
            {
                Pixels = pixels,
                Width = sprite.OrigW,
                Height = sprite.OrigH,
                TrimX = trim.X,
                TrimY = trim.Y,
                TrimW = trim.W,
                TrimH = trim.H
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

        /// <summary>The pack trim rect: every texel outside it is 0 (W = H = 0 when the sprite has no texels).</summary>
        public int TrimX, TrimY, TrimW, TrimH;
    }
}
