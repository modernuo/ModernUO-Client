// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// The only holder of the world map texture (game thread). Its operations are <see cref="Adopt{TImage}"/> (make a new
    /// texture from an image, disposing the old one) and <see cref="Release"/>/<see cref="Dispose"/>. The texture is stored
    /// in the field by the same statement that creates it, before anything else runs, and every failure after that disposes
    /// it here; the owner logs nothing (the caller logs the returned error), so no code path holds an unowned texture.
    /// Generic so the tests can run it without a graphics device.
    /// </summary>
    internal sealed class WorldMapTextureOwner<TTexture> : IDisposable where TTexture : class, IDisposable
    {
        private TTexture _texture;
        private bool _disposed;

        /// <summary>The current texture, or null (none yet, a failed upload, released or disposed).</summary>
        public TTexture Texture => _texture;

        /// <summary>
        /// Replaces the texture with one made from <paramref name="image"/>: the old texture is disposed first, then
        /// <paramref name="allocate"/> creates the new one straight into the owner and <paramref name="fill"/> uploads the
        /// pixels. The image is always disposed. On a failure (an exception from either step, or a null texture) the owner
        /// is left empty, the new texture disposed, and <paramref name="error"/> holds the exception (null for a null
        /// texture). After <see cref="Dispose"/> nothing is created. True when the new texture is in place.
        /// </summary>
        public bool Adopt<TImage>(TImage image, Func<TImage, TTexture> allocate, Action<TTexture, TImage> fill, out Exception error)
            where TImage : IDisposable
        {
            error = null;

            using (image)
            {
                Release();

                if (_disposed)
                {
                    return false;
                }

                try
                {
                    _texture = allocate(image);

                    if (_texture == null)
                    {
                        return false;
                    }

                    fill(_texture, image);

                    return true;
                }
                catch (Exception ex)
                {
                    Release();
                    error = ex;

                    return false;
                }
            }
        }

        /// <summary>Disposes the current texture and leaves the owner empty (a facet change; the next adopt fills it).</summary>
        public void Release()
        {
            TTexture texture = _texture;
            _texture = null;
            texture?.Dispose();
        }

        /// <summary>Disposes the current texture; later adopts dispose their image and create nothing.</summary>
        public void Dispose()
        {
            _disposed = true;
            Release();
        }
    }
}
