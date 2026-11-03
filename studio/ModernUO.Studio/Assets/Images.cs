using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Assets
{
    /// <summary>A CPU-built image (hue previews, multi composites) on its own texture.</summary>
    internal sealed class CpuImage : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly ImGuiRenderer _renderer;
        private Texture2D _texture;

        public CpuImage(GraphicsDevice device, ImGuiRenderer renderer)
        {
            _device = device;
            _renderer = renderer;
        }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsEmpty => _texture == null;

        public SpriteInfo Sprite => _texture == null
            ? SpriteInfo.Empty
            : new SpriteInfo { Texture = _texture, UV = new Rectangle(0, 0, Width, Height) };

        public unsafe void Set(ReadOnlySpan<uint> pixels, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                Clear();

                return;
            }

            if (_texture == null || _texture.Width != width || _texture.Height != height)
            {
                Clear();
                _texture = new Texture2D(_device, width, height, false, SurfaceFormat.Color);
            }

            Width = width;
            Height = height;

            fixed (uint* p = pixels)
            {
                _texture.SetDataPointerEXT(0, null, (IntPtr)p, width * height * sizeof(uint));
            }

            TextureUploads.Changed();
        }

        public void Clear()
        {
            if (_texture != null)
            {
                _renderer.Unbind(_texture);
                _texture.Dispose();
                _texture = null;
            }

            Width = Height = 0;
        }

        public void Dispose() => Clear();
    }

    internal static class Images
    {
        /// <summary>Writes RGBA pixels (red in the low byte) as a PNG.</summary>
        public static unsafe void SavePng(GraphicsDevice device, string path, ReadOnlySpan<uint> pixels, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var texture = new Texture2D(device, width, height, false, SurfaceFormat.Color);

            fixed (uint* p = pixels)
            {
                texture.SetDataPointerEXT(0, null, (IntPtr)p, width * height * sizeof(uint));
            }

            using var stream = File.Create(path);
            texture.SaveAsPng(stream, width, height);
        }

        /// <summary>
        /// UO hueing on RGBA pixels: the 5-bit red of a pixel picks one of the hue's 32 shades. A partial hue (tiledata
        /// PartialHue, or bit 0x8000 of the hue) recolours only grey pixels. Hue ids are 1-based; 0 leaves the pixels alone.
        /// </summary>
        public static void ApplyHue(Span<uint> pixels, uint[] lut, int hue, bool partial)
        {
            partial |= (hue & 0x8000) != 0;
            hue &= 0x3FFF;

            if (hue == 0 || lut == null || (hue - 1) * 32 + 32 > lut.Length)
            {
                return;
            }

            int row = (hue - 1) * 32;

            for (int i = 0; i < pixels.Length; i++)
            {
                uint c = pixels[i];
                uint a = c >> 24;

                if (a == 0)
                {
                    continue;
                }

                uint r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF;

                if (partial && !(r == g && g == b))
                {
                    continue;
                }

                pixels[i] = (lut[row + (int)(r >> 3)] & 0x00FFFFFF) | (a << 24);
            }
        }

        /// <summary>A mono 16-bit PCM WAV file.</summary>
        public static void SaveWav(string path, ReadOnlySpan<byte> pcm, int sampleRate = 22050)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var stream = File.Create(path);
            using var w = new BinaryWriter(stream);
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(sampleRate);
            w.Write(sampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
    }
}
