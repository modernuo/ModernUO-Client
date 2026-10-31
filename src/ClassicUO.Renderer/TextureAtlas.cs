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
