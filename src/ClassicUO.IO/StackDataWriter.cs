using ClassicUO.Utility;
using System;
using System.Buffers.Binary;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace ClassicUO.IO
{
    public ref struct StackDataWriter
    {
        private const MethodImplOptions IMPL_OPTION = MethodImplOptions.AggressiveInlining
#if !NETFRAMEWORK && !NETSTANDARD2_0
                                                      | MethodImplOptions.AggressiveOptimization
#endif
            ;


        private byte[] _allocatedBuffer;
        private Span<byte> _buffer;
        private int _position;


        public StackDataWriter(int initialCapacity)
        {
            this = default;

            Position = 0;

            EnsureSize(initialCapacity);
        }

        public StackDataWriter(Span<byte> span)
        {
            this = default;

            Write(span);
        }


        public readonly byte[] AllocatedBuffer => _allocatedBuffer;
      
        public readonly Span<byte> RawBuffer => _buffer;
       
        public readonly ReadOnlySpan<byte> Buffer => _buffer.Slice(0, Position);
       
        public readonly Span<byte> BufferWritten => _buffer.Slice(0, BytesWritten);
      
        public int Position
        {
            [MethodImpl(IMPL_OPTION)]
            readonly get => _position;

            [MethodImpl(IMPL_OPTION)]
            set
            {
                _position = value;
                BytesWritten = Math.Max(value, BytesWritten);
            }
        }

        public int BytesWritten { get; private set; }



        [MethodImpl(IMPL_OPTION)]
        public void Seek(int position, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:

                    Position = position;

                    break;

                case SeekOrigin.Current:

                    Position += position;

                    break;

                case SeekOrigin.End:

                    Position = BytesWritten + position;

                    break;
            }

            EnsureSize(Position - _buffer.Length + 1);
        }


        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt8(byte b)
        {
            EnsureSize(1);

            _buffer[Position] = b;

            Position += 1;
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt8(sbyte b)
        {
            EnsureSize(sizeof(byte));

            _buffer[Position] = (byte) b;

            Position += sizeof(byte);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteBool(bool b)
        {
            WriteUInt8(b ? (byte) 0x01 : (byte) 0x00);
        }



        /* Little Endian */

        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt16LE(ushort b)
        {
            EnsureSize(sizeof(ushort));

            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(Position), b);

            Position += sizeof(ushort);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt16LE(short b)
        {
            EnsureSize(sizeof(short));

            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(Position), b);

            Position += sizeof(short);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt32LE(uint b)
        {
            EnsureSize(sizeof(uint));

            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(Position), b);

            Position += sizeof(uint);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt32LE(int b)
        {
            EnsureSize(sizeof(int));

            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(Position), b);

            Position += sizeof(int);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt64LE(ulong b)
        {
            EnsureSize(sizeof(ulong));

            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(Position), b);

            Position += sizeof(ulong);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt64LE(long b)
        {
            EnsureSize(sizeof(long));

            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(Position), b);

            Position += sizeof(long);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUnicodeLE(string str)
        {
            WriteString<char>(Encoding.Unicode, str, -1);
            WriteUInt16LE(0x0000);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUnicodeLE(string str, int length)
        {
            WriteString<char>(Encoding.Unicode, str, length);
        }




        /* Big Endian */

        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt16BE(ushort b)
        {
            EnsureSize(sizeof(ushort));

            BinaryPrimitives.WriteUInt16BigEndian(_buffer.Slice(Position), b);

            Position += sizeof(ushort);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt16BE(short b)
        {
            EnsureSize(sizeof(short));

            BinaryPrimitives.WriteInt16BigEndian(_buffer.Slice(Position), b);

            Position += sizeof(short);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt32BE(uint b)
        {
            EnsureSize(sizeof(uint));

            BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(Position), b);

            Position += sizeof(uint);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt32BE(int b)
        {
            EnsureSize(sizeof(int));

            BinaryPrimitives.WriteInt32BigEndian(_buffer.Slice(Position), b);

            Position += sizeof(int);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUInt64BE(ulong b)
        {
            EnsureSize(sizeof(ulong));

            BinaryPrimitives.WriteUInt64BigEndian(_buffer.Slice(Position), b);

            Position += sizeof(ulong);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteInt64BE(long b)
        {
            EnsureSize(sizeof(long));

            BinaryPrimitives.WriteInt64BigEndian(_buffer.Slice(Position), b);

            Position += sizeof(long);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUnicodeBE(string str)
        {
            WriteString<char>(Encoding.BigEndianUnicode, str, -1);
            WriteUInt16BE(0x0000);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteUnicodeBE(string str, int length)
        {
            WriteString<char>(Encoding.BigEndianUnicode, str, length);
        }

        



        [MethodImpl(IMPL_OPTION)]
        public void WriteUTF8(string str, int len)
        {
            WriteString<byte>(Encoding.UTF8, str, len);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteASCII(string str)
        {
            if (!string.IsNullOrEmpty(str))
            {
                foreach (var b in StringHelper.StringToCp1252Bytes(str))
                {
                    WriteUInt8(b);
                }
            }

            WriteUInt8(0x00);
        }

        [MethodImpl(IMPL_OPTION)]
        public void WriteASCII(string str, int length)
        {
            int start = Position;

            if (string.IsNullOrEmpty(str))
            {
                WriteZero(sizeof(byte));
            }
            else
            {
                foreach (var b in StringHelper.StringToCp1252Bytes(str, length))
                {
                    WriteUInt8(b);
                }
            }

            if (length > -1 && Position > start)
            {
                WriteZero(length * sizeof(byte) - (Position - start));
            }
        }


        [MethodImpl(IMPL_OPTION)]
        public void WriteZero(int count)
        {
            if (count > 0)
            {
                EnsureSize(count);

                _buffer.Slice(Position, count).Fill(0);

                Position += count;
            }
        }

        [MethodImpl(IMPL_OPTION)]
        public void Write(ReadOnlySpan<byte> span)
        {
            EnsureSize(span.Length);

            span.CopyTo(_buffer.Slice(Position));

            Position += span.Length;
        }

        // Thanks MUO :)
        private void WriteString<T>(Encoding encoding, string str, int length) where T : struct, IEquatable<T>
        {
            int sizeT = Unsafe.SizeOf<T>();

            if (sizeT > 2)
            {
                throw new InvalidConstraintException("WriteString only accepts byte, sbyte, char, short, and ushort as a constraint");
            }

            if (str == null)
            {
                str = string.Empty;
            }
     
            int byteCount = length > -1 ? length * sizeT : encoding.GetByteCount(str);
          
            if (byteCount == 0)
            {
                return;
            }

            EnsureSize(byteCount);

            int charLength = Math.Min(length > -1 ? length : str.Length, str.Length);

            int processed = encoding.GetBytes
            (
                str,
                0,
                charLength,
                _allocatedBuffer,
                Position
            );

            Position += processed;

            if (length > -1)
            {
                WriteZero(length * sizeT - processed);
            }       
        }

        [MethodImpl(IMPL_OPTION)]
        private void EnsureSize(int size)
        {
            if (Position + size > _buffer.Length)
            {
                Rent(Math.Max(BytesWritten + size, _buffer.Length * 2));
            }
        }

        [MethodImpl(IMPL_OPTION)]
        private void Rent(int size)
        {
            byte[] newBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(size);

            if (_allocatedBuffer != null)
            {
                _buffer.Slice(0, BytesWritten).CopyTo(newBuffer);

                Return();
            }

            _buffer = _allocatedBuffer = newBuffer;
        }

        [MethodImpl(IMPL_OPTION)]
        private void Return()
        {
            if (_allocatedBuffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(_allocatedBuffer);

                _allocatedBuffer = null;
            }
        }

        [MethodImpl(IMPL_OPTION)]
        public void Dispose()
        {
            Return();
        }
    }
}
