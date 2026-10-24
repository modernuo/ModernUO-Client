// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility;
using System;
using Uo.Pack;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClassicUO.Assets
{
    public sealed class HuesLoader : UOFileLoader
    {
        public HuesLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public HuesGroup[] HuesRange { get; private set; }

        public int HuesCount { get; private set; }

        public FloatHues[] Palette { get; private set; }

        public ushort[] RadarCol { get; private set; }

        public override void Load()
        {
            var entries = HueRawFile.Read(FileManager.Pack.PathOf(TableConst.HueRawFile));
            int groups = (entries.Length + 7) >> 3;
            HuesCount = groups * 8;
            HuesRange = new HuesGroup[groups];
            var blocks = MemoryMarshal.Cast<HueEntry, HuesBlock>(entries.AsSpan());

            for (int i = 0; i < blocks.Length; i++)
            {
                HuesRange[i >> 3].Header = 0;
                HuesRange[i >> 3].Entries[i & 7] = blocks[i];
            }

            (PackHueCount, _lut) = HueFile.Read(FileManager.Pack.PathOf("hues.bin"));
            RadarCol = RadarColFile.Read(FileManager.Pack.PathOf(TableConst.RadarColFile));
        }

        private uint[] _lut;

        public int PackHueCount { get; private set; }

        public uint[] PackHues => _lut;

        public float[] CreateHuesPalette()
        {
            float[] p = new float[32 * 3 * HuesCount];

            Palette = new FloatHues[HuesCount];
            int entrycount = HuesCount >> 3;

            for (int i = 0; i < entrycount; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    int idx = i * 8 + j;

                    Palette[idx].Palette = new float[32 * 3];

                    for (int h = 0; h < 32; h++)
                    {
                        int idx1 = h * 3;

                        ushort c = HuesRange[i].Entries[j].ColorTable[h];

                        Palette[idx].Palette[idx1] = ((c >> 10) & 0x1F) / 31.0f;

                        Palette[idx].Palette[idx1 + 1] = ((c >> 5) & 0x1F) / 31.0f;

                        Palette[idx].Palette[idx1 + 2] = (c & 0x1F) / 31.0f;

                        p[idx * 96 + idx1 + 0] = Palette[idx].Palette[idx1];

                        p[idx * 96 + idx1 + 1] = Palette[idx].Palette[idx1 + 1];

                        p[idx * 96 + idx1 + 2] = Palette[idx].Palette[idx1 + 2];

                        //p[iddd++] = Palette[idx].Palette[idx1];
                        //p[iddd++] = Palette[idx].Palette[idx1 + 1];
                        //p[iddd++] = Palette[idx].Palette[idx1 + 2];
                    }
                }
            }

            return p;
        }

        public void CreateShaderColors(uint[] buffer)
        {
            int n = Math.Min(buffer.Length, _lut.Length);
            _lut.AsSpan(0, n).CopyTo(buffer);
        }

        //public float[] GetColorForShader(ushort color)
        //{
        //    if (color != 0)
        //    {
        //        if (color >= HuesCount)
        //        {
        //            color %= (ushort)HuesCount;

        //            if (color <= 0)
        //                color = 1;
        //        }

        //        return Palette[color - 1].Palette;
        //    }

        //    return _empty;
        //}

        public ushort GetColor16(ushort c, ushort color)
        {
            if (color != 0 && color < HuesCount)
            {
                color -= 1;
                int g = color >> 3;
                int e = color % 8;

                return HuesRange[g].Entries[e].ColorTable[(c >> 10) & 0x1F];
            }

            return c;
        }

        public uint GetPolygoneColor(ushort c, ushort color)
        {
            if (color != 0 && color < HuesCount)
            {
                color -= 1;
                int g = color >> 3;
                int e = color % 8;

                return HuesHelper.Color16To32(HuesRange[g].Entries[e].ColorTable[c]);
            }

            return 0xFF010101;
        }

        public uint GetUnicodeFontColor(ushort c, ushort color)
        {
            if (color != 0 && color < HuesCount)
            {
                color -= 1;
                int g = color >> 3;
                int e = color % 8;

                return HuesRange[g].Entries[e].ColorTable[8];
            }

            return HuesHelper.Color16To32(c);
        }

        public uint GetColor(ushort c, ushort color)
        {
            if (color != 0 && color < HuesCount)
            {
                color -= 1;
                int g = color >> 3;
                int e = color % 8;

                return HuesHelper.Color16To32(HuesRange[g].Entries[e].ColorTable[(c >> 10) & 0x1F]);
            }

            return color != 0 ? HuesHelper.Color16To32(color) : HuesHelper.Color16To32(c);
        }

        public uint GetPartialHueColor(ushort c, ushort color)
        {
            if (color != 0 && color < HuesCount)
            {
                color -= 1;
                int g = color >> 3;
                int e = color % 8;
                uint cl = HuesHelper.Color16To32(c);

                byte R = (byte) (cl & 0xFF);
                byte G = (byte) ((cl >> 8) & 0xFF);
                byte B = (byte) ((cl >> 16) & 0xFF);

                if (R == G && R == B)
                {
                    cl = HuesHelper.Color16To32(HuesRange[g].Entries[e].ColorTable[(c >> 10) & 0x1F]);
                }

                return cl;
            }

            return HuesHelper.Color16To32(c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetRadarColorData(int c)
        {
            if (c >= 0 && c < RadarCol.Length)
            {
                return RadarCol[c];
            }

            return 0;
        }
    }


    [InlineArray(32)]
    public struct ColorTableArray
    {
        private ushort _a0;
    }

    [InlineArray(8)]
    public struct HuesBlockArray
    {
        private HuesBlock _a0;
    }


    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HuesBlock
    {
        public ColorTableArray ColorTable;
        public ushort TableStart;
        public ushort TableEnd;
        public unsafe fixed byte Name[20];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HuesGroup
    {
        public uint Header;
        public HuesBlockArray Entries;
    }

    public struct FloatHues
    {
        //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)]
        public float[] Palette;
    }
}