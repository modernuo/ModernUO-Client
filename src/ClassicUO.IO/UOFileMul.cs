#region license

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
using System.Runtime.InteropServices;

namespace ClassicUO.IO
{
    public class UOFileMul
    {
        [StructLayout(LayoutKind.Sequential, Pack=1, Size=12)]
        private struct RawIndexEntry
        {
            public uint Offset;
            public int Length;
            public uint Size;
        };

        public static unsafe void FillEntries(PinnedBuffer dataFile, PinnedBuffer idxFile, ref UOFileIndex[] entries)
        {
            int count = (int) idxFile.Length / 12;
            entries = new UOFileIndex[count];

            var idxPtr = (RawIndexEntry *)idxFile.StartAddress;

            for (int i = 0; i < count; i++, idxPtr++)
            {
                ref UOFileIndex e = ref entries[i];
                e.FileAddress = dataFile.StartAddress;   // .mul mmf address
                e.FileSize = (uint) dataFile.Length; // .mul mmf length
                e.Offset = idxPtr->Offset; // .idx offset
                e.Length = idxPtr->Length;  // .idx length
                e.DecompressedLength = 0;   // UNUSED HERE --> .UOP

                uint size = idxPtr->Size;

                if (size > 0)
                {
                    e.Width = (short) (size >> 16);
                    e.Height = (short) (size & 0xFFFF);
                }
            }
        }
    }
}
