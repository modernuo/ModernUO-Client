// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;

namespace ClassicUO.Renderer
{
    public static class SolidColorTextureCache
    {
        private static readonly Dictionary<Color, Texture2D> _textures = new Dictionary<Color, Texture2D>();

        private static GraphicsDevice _device;

        /// <summary>Test seam: makes the texture of a color when no device was set (controls take one at construction). Only a cache miss reads it.</summary>
        internal static System.Func<Color, Texture2D> TextureWithoutDeviceForTests;

        public static void Initialize(GraphicsDevice device)
        {
            _device = device;
        }

        public static Texture2D GetTexture(Color color)
        {
            if (_textures.TryGetValue(color, out Texture2D texture))
            {
                return texture;
            }

            if (_device == null && TextureWithoutDeviceForTests != null)
            {
                texture = TextureWithoutDeviceForTests(color);
                _textures[color] = texture;

                return texture;
            }

            texture = new Texture2D
            (
                _device,
                1,
                1,
                false,
                SurfaceFormat.Color
            );

            texture.SetData(new[] { color });
            TextureUploads.Changed();
            _textures[color] = texture;

            return texture;
        }
    }
}