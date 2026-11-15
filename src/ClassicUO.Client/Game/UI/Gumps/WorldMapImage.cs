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
    /// A decoded world map image: RGBA8 pixels (R first, rows top to bottom) in native memory owned by this object and
    /// freed by <see cref="Dispose"/>. The load task decodes it and the game thread uploads and disposes it
    /// (<see cref="WorldMapGump.MapImageHandover{T}"/>).
    /// </summary>
    internal sealed unsafe class WorldMapImage : IDisposable
    {
        private IntPtr _pixels;

        private WorldMapImage(int width, int height, IntPtr pixels)
        {
            Width = width;
            Height = height;
            _pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public int ByteLength => Width * Height * 4;

        /// <summary>The RGBA pixels; zero after <see cref="Dispose"/>.</summary>
        public IntPtr Pixels => _pixels;

        public ReadOnlySpan<byte> Span => _pixels == IntPtr.Zero ? default : new ReadOnlySpan<byte>((void*)_pixels, ByteLength);

        public void Dispose()
        {
            IntPtr pixels = Interlocked.Exchange(ref _pixels, IntPtr.Zero);

            if (pixels != IntPtr.Zero)
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

                return new WorldMapImage(width, height, pixels);
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
