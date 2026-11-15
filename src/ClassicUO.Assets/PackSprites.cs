// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Assets
{
    /// <summary>
    /// CPU decode of pack sprites to ClassicUO's RGBA8 (red in the low byte), untrimmed to the original size. Texels
    /// outside the sprite's trim rect (<see cref="SpriteEntry.TrimX"/>, TrimY, W, H) are always 0, so picker masks and
    /// visible bounds only need to look inside it.
    /// </summary>
    public static class PackSprites
    {
        public static uint[] DecodeColor(PackPages pages, in SpriteEntry e)
        {
            var pixels = new uint[e.OrigW * e.OrigH];
            DecodeColor(pages, in e, pixels);

            return pixels;
        }

        /// <summary>Decodes into <paramref name="pixels"/> (at least OrigW x OrigH; that prefix is overwritten).</summary>
        public static void DecodeColor(PackPages pages, in SpriteEntry e, Span<uint> pixels)
        {
            pixels = pixels.Slice(0, e.OrigW * e.OrigH);

            if (!e.Present || e.W == 0 || e.H == 0)
            {
                pixels.Clear();

                return;
            }

            ClearOutsideTrim(in e, pixels);
            int count = e.W * e.H;
            var rented = ArrayPool<byte>.Shared.Rent(count * 2);

            try
            {
                pages.ReadRect(e.Page, e.X, e.Y, e.W, e.H, rented.AsSpan(0, count * 2));
                var texels = MemoryMarshal.Cast<byte, ushort>(rented.AsSpan(0, count * 2));

                for (int row = 0; row < e.H; row++)
                {
                    PixelExpand.Expand(texels.Slice(row * e.W, e.W), pixels.Slice((e.TrimY + row) * e.OrigW + e.TrimX, e.W));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public static uint[] DecodeIndexed(PackPages pages, in SpriteEntry e, ReadOnlySpan<uint> palette)
        {
            var pixels = new uint[e.OrigW * e.OrigH];
            DecodeIndexed(pages, in e, palette, pixels);

            return pixels;
        }

        /// <summary>Decodes into <paramref name="pixels"/> (at least OrigW x OrigH; that prefix is overwritten).</summary>
        public static void DecodeIndexed(PackPages pages, in SpriteEntry e, ReadOnlySpan<uint> palette, Span<uint> pixels)
        {
            pixels = pixels.Slice(0, e.OrigW * e.OrigH);

            if (!e.Present || e.W == 0 || e.H == 0)
            {
                pixels.Clear();

                return;
            }

            ClearOutsideTrim(in e, pixels);
            int count = e.W * e.H;
            var rented = ArrayPool<byte>.Shared.Rent(count);

            try
            {
                var indices = rented.AsSpan(0, count);
                pages.ReadRect(e.Page, e.X, e.Y, e.W, e.H, indices);

                for (int row = 0; row < e.H; row++)
                {
                    var src = indices.Slice(row * e.W, e.W);
                    var dst = pixels.Slice((e.TrimY + row) * e.OrigW + e.TrimX, e.W);

                    for (int x = 0; x < src.Length; x++)
                    {
                        dst[x] = palette[src[x]];
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>The trim rect to pass to the picker and bounds helpers: empty when the sprite has no texels.</summary>
        public static (int X, int Y, int W, int H) Trim(in SpriteEntry e) =>
            e.Present && e.W != 0 && e.H != 0 ? (e.TrimX, e.TrimY, e.W, e.H) : (0, 0, 0, 0);

        // zeroes everything but the trim rect (which the decode overwrites)
        private static void ClearOutsideTrim(in SpriteEntry e, Span<uint> pixels)
        {
            int w = e.OrigW;
            pixels.Slice(0, e.TrimY * w + e.TrimX).Clear();

            for (int row = 0; row < e.H; row++)
            {
                int end = (e.TrimY + row) * w + e.TrimX + e.W;
                int next = row + 1 < e.H ? (e.TrimY + row + 1) * w + e.TrimX : pixels.Length;
                pixels.Slice(end, next - end).Clear();
            }
        }
    }
}
