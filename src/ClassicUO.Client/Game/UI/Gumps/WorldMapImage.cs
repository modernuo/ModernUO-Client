// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// A world map image in native memory owned by this object and freed by <see cref="Dispose"/>, rows top to bottom:
    /// RGBA8 (<see cref="SurfaceFormat.Color"/>, R first) decoded by stb_image (<see cref="DecodePng"/>) or adopted from
    /// the build (<see cref="AdoptNativeMemory"/>), or 16-bit <see cref="SurfaceFormat.Bgra5551"/> made from either by
    /// <see cref="ConvertToBgra5551"/>. The load task makes it and the game thread uploads and disposes it
    /// (<see cref="WorldMapGump.MapImageHandover{T}"/>).
    /// </summary>
    internal sealed unsafe class WorldMapImage : IDisposable
    {
        private IntPtr _pixels;
        private readonly bool _nativeMemory;

        private WorldMapImage(int width, int height, IntPtr pixels, bool nativeMemory, SurfaceFormat format = SurfaceFormat.Color)
        {
            Width = width;
            Height = height;
            _pixels = pixels;
            _nativeMemory = nativeMemory;
            Format = format;
        }

        /// <summary>
        /// worldmap-audit P-8 (owner 2026-11-19): the facet texture is Bgra5551 where the device samples it (half the
        /// memory: 58.8 MB instead of 117.5 MB on Felucca). Set once at device creation (GameController.LoadContent); false
        /// keeps RGBA8. The map's radar colours are 5 bits per channel, so only height-shaded pixels change, by at most
        /// 4/255 per channel (measure/worldmap-audit/p8).
        /// </summary>
        public static volatile bool Use16Bit;

        /// <summary>
        /// Makes the 16-bit copy of an RGBA8 image (any thread) and disposes <paramref name="source"/> on every path: the
        /// caller hands its ownership over and owns only the result.
        /// </summary>
        public static WorldMapImage ConvertToBgra5551(WorldMapImage source)
        {
            using (source)
            {
                if (source.Format != SurfaceFormat.Color)
                {
                    throw new InvalidOperationException($"not an RGBA8 image: {source.Format}");
                }

                int count = source.Width * source.Height;
                void* dst = NativeMemory.Alloc((nuint)count, sizeof(ushort));

                try
                {
                    Rgba8ToBgra5551(source.Span, new Span<ushort>(dst, count));

                    return new WorldMapImage(source.Width, source.Height, (IntPtr)dst, nativeMemory: true, SurfaceFormat.Bgra5551);
                }
                catch
                {
                    NativeMemory.Free(dst);

                    throw;
                }
            }
        }

        /// <summary>
        /// RGBA8 to Bgra5551 (B in bits 0-4, G 5-9, R 10-14, A bit 15: DXGI B5G5R5A1, SDL_GPU B5G5R5A1, GL BGRA +
        /// UNSIGNED_SHORT_1_5_5_5_REV): nearest 5-bit level per channel ((v * 31 + 127) / 255, exact for the radar colours'
        /// 5-bit levels), alpha set from 128 up (the map image's alpha is 0 or 255).
        /// </summary>
        public static void Rgba8ToBgra5551(ReadOnlySpan<byte> rgba, Span<ushort> dst)
        {
            if (rgba.Length != dst.Length * 4)
            {
                throw new ArgumentException("size mismatch");
            }

            for (int i = 0, j = 0; i < dst.Length; i++, j += 4)
            {
                int r = (rgba[j] * 31 + 127) / 255;
                int g = (rgba[j + 1] * 31 + 127) / 255;
                int b = (rgba[j + 2] * 31 + 127) / 255;
                int a = rgba[j + 3] >= 128 ? 0x8000 : 0;

                dst[i] = (ushort)(a | (r << 10) | (g << 5) | b);
            }
        }

        /// <summary>RGBA8 (<see cref="SurfaceFormat.Color"/>) or <see cref="SurfaceFormat.Bgra5551"/>.</summary>
        public SurfaceFormat Format { get; }

        public int BytesPerPixel => Format == SurfaceFormat.Bgra5551 ? 2 : 4;

        /// <summary>
        /// Takes ownership of a <see cref="NativeMemory"/> buffer of width*height RGBA pixels (the freshly built map, which
        /// is then not read back from the PNG just written). From this call on the image frees it; the caller must not.
        /// </summary>
        public static WorldMapImage AdoptNativeMemory(int width, int height, void* pixels) =>
            new WorldMapImage(width, height, (IntPtr)pixels, nativeMemory: true);

        public int Width { get; }

        public int Height { get; }

        /// <summary>The pixel bytes, which is also the texture's size made from it (worldmap.texture_bytes).</summary>
        public int ByteLength => Width * Height * BytesPerPixel;

        /// <summary>The pixels; zero after <see cref="Dispose"/>.</summary>
        public IntPtr Pixels => _pixels;

        public ReadOnlySpan<byte> Span => _pixels == IntPtr.Zero ? default : new ReadOnlySpan<byte>((void*)_pixels, ByteLength);

        public void Dispose()
        {
            IntPtr pixels = Interlocked.Exchange(ref _pixels, IntPtr.Zero);

            if (pixels == IntPtr.Zero)
            {
                return;
            }

            if (_nativeMemory)
            {
                NativeMemory.Free((void*)pixels);
            }
            else
            {
                Fna3d.ImageFree(pixels);
            }
        }

        private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>
        /// Decodes a PNG from a seekable stream with FNA3D's stb_image (any thread; no GPU work). The pixels stay in the
        /// native buffer stb_image allocated: no managed copy of the ~118 MB facet image. Throws on a file that is not a
        /// PNG or does not decode.
        /// </summary>
        public static WorldMapImage DecodePng(Stream stream)
        {
            // stb_image would also try GIF/JPEG/TGA, and FNA3D_Image_Load writes through a null result when a TGA header
            // sets the size before the decode fails; the cache only holds PNGs, so anything else is rejected here
            long start = stream.Position;
            Span<byte> signature = stackalloc byte[8];

            if (stream.ReadAtLeast(signature, 8, throwOnEndOfStream: false) != 8 || !signature.SequenceEqual(PngSignature))
            {
                throw new InvalidDataException("not a PNG file");
            }

            stream.Seek(start, SeekOrigin.Begin);

            GCHandle handle = GCHandle.Alloc(stream);

            try
            {
                int width = 0, height = 0, len = 0;
                IntPtr pixels = Fna3d.ImageLoad(GCHandle.ToIntPtr(handle), &width, &height, &len);

                if (pixels == IntPtr.Zero)
                {
                    throw new InvalidDataException("stb_image could not decode the PNG");
                }

                if (width <= 0 || height <= 0 || len != width * height * 4)
                {
                    Fna3d.ImageFree(pixels);

                    throw new InvalidDataException($"stb_image returned {width}x{height}, {len} bytes");
                }

                return new WorldMapImage(width, height, pixels, nativeMemory: false);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// FNA3D's image reader (stb_image, the one Texture2D.FromStream uses), called directly: FNA's public
        /// Texture2D.TextureDataFromStreamEXT copies the pixels into a managed array, which for the facet image is a
        /// ~118 MB large-object allocation left to the GC. The library is loaded through FNA's assembly so its DllMap
        /// (FNA.dll.config on Linux and macOS) resolves the name, as it does for FNA's own calls.
        /// </summary>
        private static class Fna3d
        {
            private static readonly delegate* unmanaged[Cdecl]<
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int>,
                delegate* unmanaged[Cdecl]<IntPtr, int, void>,
                delegate* unmanaged[Cdecl]<IntPtr, int>,
                IntPtr, int*, int*, int*, int, int, byte, IntPtr> _load;

            private static readonly delegate* unmanaged[Cdecl]<IntPtr, void> _free;

            static Fna3d()
            {
                IntPtr lib = NativeLibrary.Load("FNA3D", typeof(Texture2D).Assembly, null);
                _load = (delegate* unmanaged[Cdecl]<
                    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int>,
                    delegate* unmanaged[Cdecl]<IntPtr, int, void>,
                    delegate* unmanaged[Cdecl]<IntPtr, int>,
                    IntPtr, int*, int*, int*, int, int, byte, IntPtr>)NativeLibrary.GetExport(lib, "FNA3D_Image_Load");
                _free = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "FNA3D_Image_Free");
            }

            public static IntPtr ImageLoad(IntPtr context, int* width, int* height, int* len) =>
                _load(&Read, &Skip, &Eof, context, width, height, len, -1, -1, 0);

            public static void ImageFree(IntPtr pixels) => _free(pixels);

            // the callbacks run on the decoding thread inside native code: an exception must not escape them, so a stream
            // error reads as the end of the data and stb_image fails the decode
            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static int Read(IntPtr context, IntPtr data, int size)
            {
                try
                {
                    var stream = (Stream)GCHandle.FromIntPtr(context).Target;

                    return stream.Read(new Span<byte>((void*)data, size));
                }
                catch
                {
                    return 0;
                }
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void Skip(IntPtr context, int n)
            {
                try
                {
                    var stream = (Stream)GCHandle.FromIntPtr(context).Target;
                    stream.Seek(n, SeekOrigin.Current);
                }
                catch
                {
                }
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static int Eof(IntPtr context)
            {
                try
                {
                    var stream = (Stream)GCHandle.FromIntPtr(context).Target;

                    return stream.Position >= stream.Length ? 1 : 0;
                }
                catch
                {
                    return 1;
                }
            }
        }
    }
}
