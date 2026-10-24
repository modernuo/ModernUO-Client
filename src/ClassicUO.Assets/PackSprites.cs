// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Assets
{
    /// <summary>CPU decode of pack sprites to ClassicUO's RGBA8 (red in the low byte), untrimmed to the original size.</summary>
    public static class PackSprites
    {
        public static uint[] DecodeColor(PackPages pages, in SpriteEntry e)
        {
            var pixels = new uint[e.OrigW * e.OrigH];

            if (!e.Present || e.W == 0 || e.H == 0)
            {
                return pixels;
            }

            int count = e.W * e.H;
            var rented = ArrayPool<byte>.Shared.Rent(count * 2);

            try
            {
                pages.ReadRect(e.Page, e.X, e.Y, e.W, e.H, rented.AsSpan(0, count * 2));
                var texels = MemoryMarshal.Cast<byte, ushort>(rented.AsSpan(0, count * 2));

                for (int row = 0; row < e.H; row++)
                {
                    PixelExpand.Expand(texels.Slice(row * e.W, e.W), pixels.AsSpan((e.TrimY + row) * e.OrigW + e.TrimX, e.W));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            return pixels;
        }

        public static uint[] DecodeIndexed(PackPages pages, in SpriteEntry e, ReadOnlySpan<uint> palette)
        {
            var pixels = new uint[e.OrigW * e.OrigH];

            if (!e.Present || e.W == 0 || e.H == 0)
            {
                return pixels;
            }

            int count = e.W * e.H;
            var rented = ArrayPool<byte>.Shared.Rent(count);

            try
            {
                var indices = rented.AsSpan(0, count);
                pages.ReadRect(e.Page, e.X, e.Y, e.W, e.H, indices);

                for (int row = 0; row < e.H; row++)
                {
                    var src = indices.Slice(row * e.W, e.W);
                    var dst = pixels.AsSpan((e.TrimY + row) * e.OrigW + e.TrimX, e.W);

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

            return pixels;
        }
    }
}
