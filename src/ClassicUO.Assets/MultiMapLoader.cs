﻿#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassicUO.Assets
{
    public sealed class FacetLoader : IDisposable
    {
        private readonly PinnedBuffer file;

        public bool HasData => file.HasData;

        public FacetLoader(string path)
        {
            file = new UOFile(path);
        }

        public void Dispose()
        {
            file.Dispose();
        }

        public MultiMapInfo Load
        (
            int width,
            int height,
            int startx,
            int starty,
            int endx,
            int endy
        )
        {
            if (!file.HasData)
            {
                return default;
            }

            var reader = new StackDataReader(file.AsSpan());

            int w = reader.ReadInt16LE();

            int h = reader.ReadInt16LE();

            if (w < 1 || h < 1)
            {
                return default;
            }

            int startX = startx;
            int endX = endx <= 0 ? width : endx;

            int startY = starty;
            int endY = endy <= 0 ? height : endy;

            int pwidth = endX - startX;
            int pheight = endY - startY;

            var pixels = new uint[pwidth * pheight];

            for (int y = 0; y < h; y++)
            {
                int x = 0;

                int colorCount = reader.ReadInt32LE() / 3;

                for (int i = 0; i < colorCount; i++)
                {
                    int size = reader.ReadUInt8();

                    uint color = HuesHelper.Color16To32(reader.ReadUInt16LE()) | 0xFF_00_00_00;

                    for (int j = 0; j < size; j++)
                    {
                        if (x >= startX && x < endX && y >= startY && y < endY)
                        {
                            pixels[(y - startY) * pwidth + (x - startX)] = color;
                        }

                        x++;
                    }
                }
            }

            return new MultiMapInfo()
            {
                Pixels = pixels,
                Width = pwidth,
                Height = pheight,
            };
        }
    }

    public sealed class MultiMapLoader : IDisposable
    {
        private static MultiMapLoader _instance;
        private FacetLoader[] _facets = Array.Empty<FacetLoader>();
        private PinnedBuffer _file;

        private MultiMapLoader()
        {
        }

        public void Dispose()
        {
            _file?.Dispose();

            foreach (var i in _facets)
                i?.Dispose();
        }

        public static MultiMapLoader Instance => _instance ?? (_instance = new MultiMapLoader());

        private FacetLoader GetFacetLoader(int map)
        {
            if (map < 0)
            {
                return null;
            }

            if (map >= _facets.Length || _facets[map] == null)
            {
                string path = UOFileManager.GetUOFilePath($"facet{map:D2}.mul");
                if (!File.Exists(path))
                {
                    return null;
                }

                if (map >= _facets.Length)
                {
                    Array.Resize(ref _facets, map);
                }

                _facets[map] = new FacetLoader(path);
            }

            return _facets[map];
        }

        private unsafe MultiMapInfo LoadMap
        (
            int width,
            int height,
            int startx,
            int starty,
            int endx,
            int endy
        )
        {
            if (_file == null)
            {
                string path = UOFileManager.GetUOFilePath("Multimap.rle");
                if (!File.Exists(path))
                {
                    return default;
                }

                _file = new UOFile(path);
            }

            if (!_file.HasData)
            {
                Log.Warn("MultiMap.rle is not loaded!");

                return default;
            }

            var reader = new StackDataReader(_file.AsSpan());

            int w = reader.ReadInt32LE();
            int h = reader.ReadInt32LE();

            if (w < 1 || h < 1)
            {
                Log.Warn("Failed to load bounds from MultiMap.rle");

                return default;
            }

            int mapSize = width * height;

            startx = startx >> 1;
            endx = endx >> 1;

            int widthDivisor = endx - startx;

            if (widthDivisor == 0)
            {
                widthDivisor++;
            }

            starty = starty >> 1;
            endy = endy >> 1;

            int heightDivisor = endy - starty;

            if (heightDivisor == 0)
            {
                heightDivisor++;
            }

            int pwidth = (width << 8) / widthDivisor;
            int pheight = (height << 8) / heightDivisor;

            byte[] data = new byte[mapSize];

            int x = 0, y = 0;

            int maxPixelValue = 1;
            int startHeight = starty * pheight;

            while (!reader.IsEOF)
            {
                byte pic = reader.ReadUInt8();
                byte size = (byte) (pic & 0x7F);
                bool colored = (pic & 0x80) != 0;

                int currentHeight = y * pheight;
                int posY = width * ((currentHeight - startHeight) >> 8);

                for (int i = 0; i < size; i++)
                {
                    if (colored && x >= startx && x < endx && y >= starty && y < endy)
                    {
                        int position = posY + ((pwidth * (x - startx)) >> 8);

                        ref byte pixel = ref data[position];

                        if (pixel < 0xFF)
                        {
                            if (pixel == maxPixelValue)
                            {
                                maxPixelValue++;
                            }

                            pixel++;
                        }
                    }

                    x++;

                    if (x >= w)
                    {
                        x = 0;
                        y++;
                        currentHeight += pheight;
                        posY = width * ((currentHeight - startHeight) >> 8);
                    }
                }
            }

            if (maxPixelValue <= 0)
            {
                return default;
            }

            int s = Marshal.SizeOf<HuesGroup>();
            IntPtr ptr = Marshal.AllocHGlobal(s * HuesLoader.Instance.HuesRange.Length);

            for (int i = 0; i < HuesLoader.Instance.HuesRange.Length; i++)
            {
                Marshal.StructureToPtr(HuesLoader.Instance.HuesRange[i], ptr + i * s, false);
            }

            ushort* huesData = (ushort*)(byte*)(ptr + 30800);

            Span<uint> colorTable = stackalloc uint[byte.MaxValue];
            var pixels = new uint[mapSize];

            try
            {
                int colorOffset = 31 * maxPixelValue;

                for (int i = 0; i < maxPixelValue; i++)
                {
                    colorOffset -= 31;
                    colorTable[i] = HuesHelper.Color16To32(huesData[colorOffset / maxPixelValue]) | 0xFF_00_00_00;
                }

                for (int i = 0; i < mapSize; i++)
                {
                    pixels[i] = data[i] != 0 ? colorTable[data[i] - 1] : 0;
                }
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
            }

            return new MultiMapInfo()
            {
                Pixels = pixels,
                Width = width,
                Height = height,
            };
        }

        public MultiMapInfo LoadFacetOrMap
        (
            int? facet,
            int width,
            int height,
            int startx,
            int starty,
            int endx,
            int endy
        )
        {
            if (facet.HasValue)
            {
                var facetLoader = GetFacetLoader(facet.Value);
                if (facetLoader != null)
                {
                    return facetLoader.Load(width, height, startx, starty, endx, endy);
                }
            }

            return LoadMap(width, height, startx, starty, endx, endy);
        }
    }

    public ref struct MultiMapInfo
    {
        public Span<uint> Pixels;
        public int Width, Height;
    }
}
