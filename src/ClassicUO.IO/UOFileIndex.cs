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

using System;

namespace ClassicUO.IO
{
    public struct UOFileIndex
    {
        public UOFileIndex
        (
            IntPtr address,
            uint fileSize,
            long offset,
            int length,
            int decompressed,
            short width = 0,
            short height = 0,
            ushort hue = 0
        )
        {
            FileAddress = address;
            FileSize = fileSize;
            Offset = offset;
            Length = length;
            DecompressedLength = decompressed;
            Width = width;
            Height = height;
            Hue = hue;

            AnimOffset = 0;
        }

        public IntPtr FileAddress;
        public uint FileSize;
        public long Offset;
        public int Length;
        public int DecompressedLength;
        public short Width;
        public short Height;
        public ushort Hue;
        public sbyte AnimOffset;

        public IntPtr StartAddress => FileAddress + (IntPtr)Offset;
        public IntPtr EndAddress => StartAddress + (IntPtr)Length;

        public bool IsValid()
        {
            return Offset >= 0 && Length > 0 && Offset != 0x0000_0000_FFFF_FFFF;
        }

        public static UOFileIndex Invalid = new UOFileIndex
        (
            IntPtr.Zero,
            0,
            0,
            0,
            0
        );

        /**
         * Is this the special #Invalid value?
         */
        public bool IsInvalid()
        {
            return FileAddress == IntPtr.Zero;
        }

        public unsafe byte* Data => (byte *)(FileAddress + Offset);
    }

    public struct UOFileIndex5D
    {
        public UOFileIndex5D(uint file, uint index, uint offset, uint length, uint extra = 0)
        {
            FileID = file;
            BlockID = index;
            Position = offset;
            Length = length;
            GumpData = extra;
        }

        public uint FileID;
        public uint BlockID;
        public uint Position;
        public uint Length;
        public uint GumpData;
    }
}
