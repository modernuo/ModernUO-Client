using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StbRectPackSharp;
using System;
using System.Collections.Generic;

namespace ClassicUO.Renderer
{
    public class TextureAtlas : IDisposable
    {
        private readonly int _width,
            _height;
        private readonly SurfaceFormat _format;
        private readonly GraphicsDevice _device;
        private readonly List<Texture2D> _textureList;
        private Packer _packer;

        public TextureAtlas(GraphicsDevice device, int width, int height, SurfaceFormat format)
        {
            _device = device;
            _width = width;
            _height = height;
            _format = format;

            _textureList = new List<Texture2D>();
        }

        public int TexturesCount => _textureList.Count;

        /// <summary>anim_atlas_lru: new pages get an <see cref="Animations.AtlasPage"/> as their Tag.</summary>
        public bool TrackPages { get; set; }

        public long PageBytes => (long)_width * _height * 4;

        public Texture2D GetPage(int index) => _textureList[index];

        private int _pageSerial;

        /// <summary>
        /// anim_atlas_lru: frees a page that is not the open one (only the open page has a packer, so a freed page's space
        /// is never handed out again). Callers drop every reference to the texture first.
        /// </summary>
        public void RemovePage(int index)
        {
            if (index < 0 || index >= _textureList.Count - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"page {index} of {_textureList.Count} can't be removed (the last page is open)");
            }

            Texture2D texture = _textureList[index];
            _textureList.RemoveAt(index);
            texture.Dispose();
        }

        public unsafe Texture2D AddSprite(
            ReadOnlySpan<uint> pixels,
            int width,
            int height,
            out Rectangle pr
        )
        {
            // a rect that can't fit an empty page would otherwise make a new page forever
            if (width <= 0 || height <= 0 || width > _width || height > _height || (long)width * height > pixels.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"sprite {width}x{height} ({pixels.Length} texels) can't go into a {_width}x{_height} atlas");
            }

            var index = _textureList.Count - 1;

            if (index < 0)
            {
                index = 0;
                CreateNewTexture2D();
            }

            if (!_packer.PackRect(width, height, out pr))
            {
                CreateNewTexture2D();
                index = _textureList.Count - 1;

                if (!_packer.PackRect(width, height, out pr))
                {
                    throw new InvalidOperationException($"sprite {width}x{height} doesn't fit an empty {_width}x{_height} atlas page");
                }
            }

            Texture2D texture = _textureList[index];

            fixed (uint* src = pixels)
            {
                texture.SetDataPointerEXT(0, pr, (IntPtr)src, sizeof(uint) * width * height);
                TextureUploads.Changed();
            }

            return texture;
        }

        private void CreateNewTexture2D()
        {
            Utility.Logging.Log.Trace($"creating texture: {_width}x{_height} {_format}");
            Texture2D texture = new Texture2D(_device, _width, _height, false, _format);

            if (TrackPages)
            {
                texture.Tag = new Animations.AtlasPage(_pageSerial++, Animations.AtlasPageClock.NowMs);
            }

            _textureList.Add(texture);

            _packer?.Dispose();
            _packer = new Packer(_width, _height);
        }

        public void SaveImages(string name)
        {
            for (int i = 0, count = TexturesCount; i < count; ++i)
            {
                var texture = _textureList[i];

                using (var stream = System.IO.File.Create($"atlas/{name}_atlas_{i}.png"))
                {
                    texture.SaveAsPng(stream, texture.Width, texture.Height);
                }
            }
        }

        public void Dispose()
        {
            foreach (Texture2D texture in _textureList)
            {
                if (!texture.IsDisposed)
                {
                    texture.Dispose();
                }
            }

            _packer.Dispose();
            _textureList.Clear();
        }
    }
}
