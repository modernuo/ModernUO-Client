// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Frozen copy of the font-loading part of ClassicUO's FontsLoader (src/ClassicUO.Assets/FontsLoader.cs at
    /// 492e73c14, before text moved into the pack): <c>Load</c>, <c>GetCharUni</c> and <c>LoadChar</c> verbatim, minus
    /// the text layout and rendering, which read glyphs only through them. The converter writes text/fonts.bin and
    /// text/unifont.bin from it and verifies the pack against it.
    /// </summary>
    public sealed class FontsLoader : UOFileLoader
    {
        private FontCharacterData[,] _fontDataASCII;
        private FontCharacterDataUnicode[,] _fontDataUNICODE;
        private readonly UOFile[] _unicodeFontAddress = new UOFile[20];
        private readonly long[] _unicodeFontSize = new long[20];

        public FontsLoader(UOFileManager fileManager) : base(fileManager) { }

        public int FontCount { get; private set; }

        public override unsafe void Load()
        {
            UOFileMul fonts = new UOFileMul(FileManager.GetUOFilePath("fonts.mul"));

            for (int i = 0; i < 20; i++)
            {
                string path = FileManager.GetUOFilePath(
                    "unifont" + (i == 0 ? "" : i.ToString()) + ".mul"
                );

                if (File.Exists(path))
                {
                    _unicodeFontAddress[i] = new UOFileMul(path);
                }
            }

            int fontHeaderSize = sizeof(FontHeader);
            FontCount = 0;

            while (fonts.Position < fonts.Length)
            {
                bool exit = false;
                fonts.ReadUInt8();

                for (int i = 0; i < 224; i++)
                {
                    if (fonts.Position + fontHeaderSize >= fonts.Length)
                    {
                        break;
                    }

                    var fh = new FontHeader()
                    {
                        Width = fonts.ReadUInt8(),
                        Height = fonts.ReadUInt8(),
                        Unknown = fonts.ReadUInt8(),
                    };

                    int bcount = fh.Width * fh.Height * 2;

                    if (fonts.Position + bcount > fonts.Length)
                    {
                        exit = true;

                        break;
                    }

                    fonts.Seek(bcount, SeekOrigin.Current);
                }

                if (exit)
                {
                    break;
                }

                FontCount++;
            }

            if (FontCount < 1)
            {
                FontCount = 0;

                return;
            }

            _fontDataASCII = new FontCharacterData[FontCount, 224];
            fonts.Seek(0, SeekOrigin.Begin);

            for (int i = 0; i < FontCount; i++)
            {
                byte header = fonts.ReadUInt8();

                for (int j = 0; j < 224; j++)
                {
                    if (fonts.Position + 3 >= fonts.Length)
                    {
                        continue;
                    }

                    byte w = fonts.ReadUInt8();
                    byte h = fonts.ReadUInt8();
                    fonts.ReadUInt8();
                    var data = new ushort[w * h];
                    int read = fonts.Read(MemoryMarshal.AsBytes(data.AsSpan()));
                    _fontDataASCII[i, j] = new FontCharacterData(w, h, data);
                }
            }

            if (_unicodeFontAddress[1] == null)
            {
                _unicodeFontAddress[1] = _unicodeFontAddress[0];
                _unicodeFontSize[1] = _unicodeFontSize[0];
            }

            _fontDataUNICODE = new FontCharacterDataUnicode[_unicodeFontAddress.Length, 0x10000];
        }

        private static FontCharacterDataUnicode _nullChar = new FontCharacterDataUnicode();

        /// <summary>The glyph the client draws for <paramref name="c"/> in unicode font <paramref name="font"/> (Data null = none).</summary>
        public ref FontCharacterDataUnicode GetCharUni(byte font, char c)
        {
            int index = (int)c; /*((int)c & 0xFFFFF) - 0x20*/;

            if (index < 0)
            {
                return ref _nullChar;
            }

            ref var cc = ref _fontDataUNICODE[font, index];

            if (cc.Data == null)
            {
                LoadChar(font, index);

                if (cc.Data == null)
                {
                    return ref _nullChar;
                }
            }

            return ref cc;
        }

        private void LoadChar(byte font, int index)
        {
            var file = _unicodeFontAddress[font];

            if (file == null)
            {
                return;
            }
            file.Seek(index * 4, SeekOrigin.Begin);
            var lookup = file.ReadInt32();

            if (lookup == 0)
            {
                return;
            }

            file.Seek(lookup, SeekOrigin.Begin);

            ref var cc = ref _fontDataUNICODE[font, index];

            cc.OffsetX = file.ReadInt8();
            cc.OffsetY = file.ReadInt8();
            cc.Width = file.ReadInt8();
            cc.Height = file.ReadInt8();

            if (cc.Width > 0 && cc.Height > 0)
            {
                cc.Data = new byte[(((cc.Width - 1) / 8) + 1) * cc.Height];
                file.Read(cc.Data);
            }
        }

        public bool UnicodeFontExists(byte font)
        {
            return font < 20 && _unicodeFontAddress[font] != null;
        }

        /// <summary>The ASCII glyph table entry the client reads for font <paramref name="font"/>, glyph <paramref name="index"/> (0..223).</summary>
        public ref FontCharacterData GetAscii(int font, int index) => ref _fontDataASCII[font, index];

        public override void ClearResources()
        {
            foreach (var f in _unicodeFontAddress)
            {
                f?.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FontHeader
    {
        public byte Width, Height, Unknown;
    }

    public struct FontCharacterData
    {
        public FontCharacterData(byte w, byte h, ushort[] data)
        {
            Width = w;
            Height = h;
            Data = data;
        }

        public byte Width, Height;
        public ushort[] Data;
    }

    public struct FontCharacterDataUnicode
    {
        public FontCharacterDataUnicode(sbyte w, sbyte h, sbyte offX, sbyte offY, byte[] data)
        {
            OffsetX = offX;
            OffsetY = offY;
            Width = w;
            Height = h;
            Data = data;
        }

        public sbyte OffsetX, OffsetY;
        public sbyte Width, Height;
        public byte[] Data;
    }
}
