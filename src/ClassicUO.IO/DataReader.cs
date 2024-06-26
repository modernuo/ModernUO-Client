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

using ClassicUO.Utility;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ClassicUO.IO
{
    /// <summary>
    ///     A fast Little Endian data reader.
    /// </summary>
    public sealed unsafe class DataReader
    {
        private readonly PinnedBuffer buffer;

        public long Position { get; set; }

        public IntPtr PositionAddress => (IntPtr) (buffer.StartAddress + Position);

        public bool IsEOF => Position >= buffer.Length;

        public DataReader(PinnedBuffer _buffer)
        {
            buffer = _buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Seek(long idx)
        {
            Position = idx;
            EnsureSize(0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Seek(int idx)
        {
            Position = idx;
            EnsureSize(0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int count)
        {
            EnsureSize(count);
            Position += count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            EnsureSize(1);

            return buffer.Data[Position++];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sbyte ReadSByte()
        {
            return (sbyte) ReadByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short ReadShort()
        {
            EnsureSize(2);

            short v = *(short*)PositionAddress;
            Position += 2;

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUShort()
        {
            EnsureSize(2);

            ushort v = *(ushort*)PositionAddress;
            Position += 2;

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt()
        {
            EnsureSize(4);

            int v = *(int*)PositionAddress;

            Position += 4;

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt()
        {
            EnsureSize(4);

            uint v = *(uint*)PositionAddress;
            Position += 4;

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadLong()
        {
            EnsureSize(8);

            long v = *(long*)PositionAddress;
            Position += 8;

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadULong()
        {
            EnsureSize(8);

            ulong v = *(ulong*)PositionAddress;
            Position += 8;

            return v;
        }

        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureSize(int size)
        {
            if (Position + size > buffer.Length)
            {
#if DEBUG
                throw new IndexOutOfRangeException();
#else
                ClassicUO.Utility.Logging.Log.Error($"size out of range. {Position + size} > {buffer.Length}");
#endif
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUShortReversed()
        {
            EnsureSize(2);

            return (ushort) ((ReadByte() << 8) | ReadByte());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUIntReversed()
        {
            EnsureSize(4);

            return (uint) ((ReadByte() << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte());
        }
    }
}
