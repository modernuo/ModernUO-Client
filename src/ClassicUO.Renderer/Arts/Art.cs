using ClassicUO.Assets;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Buffers;

namespace ClassicUO.Renderer.Arts
{
    public sealed class Art
    {
        private readonly SpriteInfo[] _spriteInfos;
        private readonly TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly Rectangle[] _realArtBounds;
        private readonly ArtLoader _artLoader;
        private readonly HuesLoader _huesLoader;

        public Art(ArtLoader artLoader, HuesLoader huesLoader, GraphicsDevice device)
        {
            _artLoader = artLoader;
            _huesLoader = huesLoader;
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
            _spriteInfos = new SpriteInfo[_artLoader.Count];
            _realArtBounds = new Rectangle[_spriteInfos.Length];
        }

        public void Reload()
        {
            Array.Clear(_spriteInfos);
            Array.Clear(_realArtBounds);
            _picker.Clear();
        }

        public ref readonly SpriteInfo GetLand(uint idx)
            => ref Get((uint)(idx & ~0x4000));

        public ref readonly SpriteInfo GetArt(uint idx)
            => ref Get(idx + 0x4000);

        private ref readonly SpriteInfo Get(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                // the untrimmed image only lives until the atlas upload: decode into a pooled buffer
                var buffer = ArrayPool<uint>.Shared.Rent(Math.Max(1, _artLoader.PixelCount(idx)));

                try
                {
                    var artInfo = _artLoader.GetArt(idx, buffer);

                    if (artInfo.Pixels.IsEmpty && idx > 0)
                    {
                        // Trying to load a texture that does not exist in the client MULs
                        // Degrading gracefully and only crash if not even the fallback ItemID exists
                        Log.Error(
                            $"Texture not found for sprite: idx: {idx}; itemid: {(idx > 0x4000 ? idx - 0x4000 : '-')}"
                        );
                        return ref Get(0); // ItemID of "UNUSED" placeholder
                    }

                    spriteInfo.Texture = _atlas.AddSprite(
                        artInfo.Pixels,
                        artInfo.Width,
                        artInfo.Height,
                        out spriteInfo.UV
                    );

                    PackSpriteMap.Register(spriteInfo.Texture, spriteInfo.UV, idx);

                    if (idx > 0x4000)
                    {
                        idx -= 0x4000;

                        // texels outside the pack trim rect are 0: the picker mask and the visible bounds only scan inside it
                        _picker.SetTrimmed(idx, artInfo.Width, artInfo.Height, artInfo.TrimX, artInfo.TrimY, artInfo.TrimW, artInfo.TrimH, artInfo.Pixels);
                        PixelPicker.VisibleBounds(artInfo.Pixels, artInfo.Width, artInfo.Height, artInfo.TrimX, artInfo.TrimY, artInfo.TrimW, artInfo.TrimH,
                            out int minX, out int minY, out int maxX, out int maxY);
                        _realArtBounds[idx] = new Rectangle(minX, minY, maxX - minX, maxY - minY);
                    }
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(buffer);
                }
            }

            return ref spriteInfo;
        }

        public unsafe IntPtr CreateCursorSurfacePtr(
            int index,
            ushort customHue,
            out int hotX,
            out int hotY,
            float dpiScale
        )
        {
            hotX = hotY = 0;

            var artInfo = _artLoader.GetArt((uint)(index + 0x4000));

            if (artInfo.Pixels.IsEmpty)
            {
                return IntPtr.Zero;
            }

            int srcWidth = artInfo.Width;
            int srcHeight = artInfo.Height;

            // Make a copy of pixels to avoid modifying the original
            var rentedBuffer = ArrayPool<uint>.Shared.Rent(artInfo.Pixels.Length);
            try
            {
                var pixelsCopy = rentedBuffer.AsSpan(0, artInfo.Pixels.Length);
                artInfo.Pixels.CopyTo(pixelsCopy);

                // Process the copy: find hotX/Y and clear marker pixels
                for (int y = 0; y < srcHeight; y++)
                {
                    for (int x = 0; x < srcWidth; x++)
                    {
                        int idx = y * srcWidth + x;
                        uint pixel = pixelsCopy[idx];

                        if (pixel == 0)
                            continue;

                        // Clear black marker pixels
                        if (pixel == 0xFF_00_00_00)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Check for green hotspot marker in first row/column
                        if (pixel == 0xFF_00_FF_00)
                        {
                            if (x == 0)
                                hotY = y;
                            if (y == 0)
                                hotX = x;
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Clear edge pixels (first/last row and column)
                        if (x == 0 || y == 0 || x == srcWidth - 1 || y == srcHeight - 1)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Apply custom hue if needed
                        if (customHue > 0)
                        {
                            Color c = default;
                            c.PackedValue = pixel;
                            pixelsCopy[idx] = HuesHelper.Color16To32(
                                _huesLoader.GetColor16(
                                    HuesHelper.ColorToHue(c),
                                    customHue
                                )
                            ) | 0xFF_00_00_00;
                        }
                    }
                }

                // Scale hotX/Y by dpiScale
                hotX = (int)(hotX * dpiScale);
                hotY = (int)(hotY * dpiScale);

                // Now create the surface from cleaned pixels
                fixed (uint* ptr = pixelsCopy)
                {
                    SDL.SDL_Surface* surface = (SDL.SDL_Surface*)
                        SDL.SDL_CreateSurfaceFrom(
                            srcWidth,
                            srcHeight,
                            SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888,
                            (IntPtr)ptr,
                            4 * srcWidth);

                    if (dpiScale != 1f)
                    {
                        int width = (int)(srcWidth * dpiScale);
                        int height = (int)(srcHeight * dpiScale);

                        SDL.SDL_Surface* newSurface = (SDL.SDL_Surface*)SDL.SDL_ScaleSurface(
                            (nint)surface,
                            width,
                            height,
                            SDL.SDL_ScaleMode.SDL_SCALEMODE_NEAREST);

                        SDL.SDL_DestroySurface((nint)surface);
                        surface = newSurface;
                    }

                    return (IntPtr)surface;
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rentedBuffer);
            } 
        }

        public Rectangle GetRealArtBounds(uint idx) =>
            idx < 0 || idx >= _realArtBounds.Length
                ? Rectangle.Empty
                : _realArtBounds[idx];

        public bool PixelCheck(uint idx, int x, int y) => _picker.Get(idx, x, y);
    }
}
