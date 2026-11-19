// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static ClassicUO.Game.UI.Gumps.WorldMapGump;
using SpriteFont = ClassicUO.Renderer.SpriteFont;

namespace ClassicUO.Game.UI.Gumps
{
    internal partial class WorldMapGump
    {
        private class CurLoader
        {
            public static unsafe Texture2D CreateTextureFromICO_Cur(Stream stream)
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int) stream.Length);

                try
                {
                    stream.Read(buffer, 0, buffer.Length);

                    StackDataReader reader = new StackDataReader(buffer.AsSpan(0, (int)stream.Length));

                    int bmp_pitch;
                    int i, pad;
                    SDL.SDL_Surface* surface;
                    byte* bits;
                    int expand_bmp;
                    int max_col = 0;
                    uint ico_of_s = 0;
                    uint* palette = stackalloc uint[256];

                    ushort bf_reserved, bf_type, bf_count;
                    uint bi_size, bi_width, bi_height;
                    ushort bi_planes, bi_bit_count;

                    uint bi_compression, bi_size_image, bi_x_perls_per_meter, bi_y_perls_per_meter, bi_clr_used, bi_clr_important;

                    bf_reserved = reader.ReadUInt16LE();
                    bf_type = reader.ReadUInt16LE();
                    bf_count = reader.ReadUInt16LE();

                    for (i = 0; i < bf_count; i++)
                    {
                        int b_width = reader.ReadUInt8();
                        int b_height = reader.ReadUInt8();
                        int b_color_count = reader.ReadUInt8();
                        byte b_reserver = reader.ReadUInt8();
                        ushort w_planes = reader.ReadUInt16LE();
                        ushort w_bit_count = reader.ReadUInt16LE();
                        uint dw_bytes_in_res = reader.ReadUInt32LE();
                        uint dw_image_offse = reader.ReadUInt32LE();

                        if (b_width == 0)
                        {
                            b_width = 256;
                        }

                        if (b_height == 0)
                        {
                            b_height = 256;
                        }

                        if (b_color_count == 0)
                        {
                            b_color_count = 256;
                        }

                        if (b_color_count > max_col)
                        {
                            max_col = b_color_count;
                            ico_of_s = dw_image_offse;
                        }
                    }

                    reader.Seek(ico_of_s);

                    bi_size = reader.ReadUInt32LE();

                    if (bi_size == 40)
                    {
                        bi_width = reader.ReadUInt32LE();
                        bi_height = reader.ReadUInt32LE();
                        bi_planes = reader.ReadUInt16LE();
                        bi_bit_count = reader.ReadUInt16LE();
                        bi_compression = reader.ReadUInt32LE();
                        bi_size_image = reader.ReadUInt32LE();
                        bi_x_perls_per_meter = reader.ReadUInt32LE();
                        bi_y_perls_per_meter = reader.ReadUInt32LE();
                        bi_clr_used = reader.ReadUInt32LE();
                        bi_clr_important = reader.ReadUInt32LE();
                    }
                    else
                    {
                        return null;
                    }

                    const int BI_RGB = 0;

                    switch (bi_compression)
                    {
                        case BI_RGB:

                            switch (bi_bit_count)
                            {
                                case 1:
                                case 4:
                                    expand_bmp = bi_bit_count;
                                    bi_bit_count = 8;

                                    break;

                                case 8:
                                    expand_bmp = 8;

                                    break;

                                case 32:
                                    expand_bmp = 0;

                                    break;

                                default: return null;
                            }

                            break;

                        default: return null;
                    }


                    bi_height >>= 1;

                    surface = (SDL.SDL_Surface*)SDL.SDL_CreateSurface((int)bi_width, (int)bi_height,
                        SDL.SDL_GetPixelFormatForMasks(32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000));


                    if (bi_bit_count <= 8)
                    {
                        if (bi_clr_used == 0)
                        {
                            bi_clr_used = (uint)(1 << bi_bit_count);
                        }

                        for (i = 0; i < bi_clr_used; i++)
                        {
                            palette[i] = reader.ReadUInt32LE();
                        }
                    }

                    bits = (byte*)(surface->pixels + surface->h * surface->pitch);

                    switch (expand_bmp)
                    {
                        case 1:
                            bmp_pitch = (int)(bi_width + 7) >> 3;
                            pad = bmp_pitch % 4 != 0 ? 4 - bmp_pitch % 4 : 0;

                            break;

                        case 4:
                            bmp_pitch = (int)(bi_width + 1) >> 1;
                            pad = bmp_pitch % 4 != 0 ? 4 - bmp_pitch % 4 : 0;

                            break;

                        case 8:
                            bmp_pitch = (int)bi_width;
                            pad = bmp_pitch % 4 != 0 ? 4 - bmp_pitch % 4 : 0;

                            break;

                        default:
                            bmp_pitch = (int)bi_width * 4;
                            pad = 0;

                            break;
                    }


                    while (bits > (byte*)surface->pixels)
                    {
                        bits -= surface->pitch;

                        switch (expand_bmp)
                        {
                            case 1:
                            case 4:
                            case 8:
                                {
                                    byte pixel = 0;
                                    int shift = 8 - expand_bmp;

                                    for (i = 0; i < surface->w; i++)
                                    {
                                        if (i % (8 / expand_bmp) == 0)
                                        {
                                            pixel = reader.ReadUInt8();
                                        }

                                        *((uint*)bits + i) = palette[pixel >> shift];
                                        pixel <<= expand_bmp;
                                    }
                                }

                                break;

                            default:

                                for (int k = 0; k < surface->pitch; k++)
                                {
                                    bits[k] = reader.ReadUInt8();
                                }

                                break;
                        }

                        if (pad != 0)
                        {
                            for (i = 0; i < pad; i++)
                            {
                                reader.ReadUInt8();
                            }
                        }
                    }


                    bits = (byte*)(surface->pixels + surface->h * surface->pitch);
                    expand_bmp = 1;
                    bmp_pitch = (int)(bi_width + 7) >> 3;
                    pad = bmp_pitch % 4 != 0 ? 4 - bmp_pitch % 4 : 0;

                    while (bits > (byte*)surface->pixels)
                    {
                        byte pixel = 0;
                        int shift = 8 - expand_bmp;

                        bits -= surface->pitch;

                        for (i = 0; i < surface->w; i++)
                        {
                            if (i % (8 / expand_bmp) == 0)
                            {
                                pixel = reader.ReadUInt8();
                            }

                            *((uint*)bits + i) |= pixel >> shift != 0 ? 0 : 0xFF000000;

                            pixel <<= expand_bmp;
                        }

                        if (pad != 0)
                        {
                            for (i = 0; i < pad; i++)
                            {
                                reader.ReadUInt8();
                            }
                        }
                    }

                    surface = (SDL.SDL_Surface*)INTERNAL_convertSurfaceFormat((IntPtr)surface);

                    int len = surface->w * surface->h * 4;
                    byte* pixels = (byte*)surface->pixels;

                    for (i = 0; i < len; i += 4, pixels += 4)
                    {
                        if (pixels[3] == 0)
                        {
                            pixels[0] = 0;
                            pixels[1] = 0;
                            pixels[2] = 0;
                        }
                    }

                    Texture2D texture = new Texture2D(Client.Game.GraphicsDevice, surface->w, surface->h);
                    texture.SetDataPointerEXT(0, new Rectangle(0, 0, surface->w, surface->h), surface->pixels, len);
                    ClassicUO.Renderer.TextureUploads.Changed();

                    SDL.SDL_DestroySurface((IntPtr)surface);

                    reader.Release();

                    return texture;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            private static unsafe IntPtr INTERNAL_convertSurfaceFormat(IntPtr surface)
            {
                IntPtr result = surface;
                SDL.SDL_Surface* surPtr = (SDL.SDL_Surface*) surface;
                SDL.SDL_PixelFormat pixelFormat = surPtr->format;

                // SurfaceFormat.Color is SDL_PIXELFORMAT_ABGR8888
                if (pixelFormat != SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888)
                {
                    // Create a properly formatted copy, free the old surface
                    result = SDL.SDL_ConvertSurface(surface, SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888);
                    SDL.SDL_DestroySurface(surface);
                }

                return result;
            }
        }
    }
}
