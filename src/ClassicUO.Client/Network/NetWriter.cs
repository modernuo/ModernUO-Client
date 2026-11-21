// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.IO;

namespace ClassicUO.Network
{
    /// <summary>
    /// span_reader (net-rework step 2): the writer outgoing packets are built with. With the switch on, ModernUO's
    /// <see cref="SpanWriter"/> (rented, resizable); off, the old <see cref="StackDataWriter"/>. Both lay out the same bytes. The
    /// API is SpanWriter's (integers need their exact type: Write((byte)x) writes one byte, Write((ushort)x) two, big-endian),
    /// so when the old path is deleted this type is replaced by SpanWriter. CUO_SPAN_READER_CHECK=1 builds every packet with
    /// both and compares the bytes when they are taken (<see cref="BufferWritten"/>).
    /// </summary>
    public ref struct NetWriter
    {
        private SpanWriter _new;
        private StackDataWriter _old;
        private readonly SpanBufferMode _mode;

        public NetWriter(int initialCapacity) : this(initialCapacity, SpanBuffers.Mode)
        {
        }

        public NetWriter(int initialCapacity, SpanBufferMode mode)
        {
            _mode = mode;
            _new = mode != SpanBufferMode.Old ? new SpanWriter(initialCapacity, true) : default;
            _old = mode != SpanBufferMode.New ? new StackDataWriter(initialCapacity) : default;
        }

        private readonly bool UseNew => _mode == SpanBufferMode.New;

        private readonly bool Both => _mode >= SpanBufferMode.CheckOld;

        public readonly int BytesWritten => UseNew ? _new.BytesWritten : _old.BytesWritten;

        public readonly int Position => UseNew ? _new.Position : _old.Position;

        /// <summary>The packet: everything written. Under the checker both builds are compared here.</summary>
        public Span<byte> BufferWritten
        {
            get
            {
                if (UseNew)
                {
                    return _new.BufferWritten;
                }
#if CUO_DEVTOOLS
                if (Both)
                {
                    SpanBuffers.Writes++;

                    if (!_old.BufferWritten.SequenceEqual(_new.BufferWritten))
                    {
                        SpanBuffers.WriterMismatch(_old.BufferWritten, _new.BufferWritten);
                    }

                    if (_mode == SpanBufferMode.CheckNew)
                    {
                        return _new.BufferWritten;
                    }
                }
#endif
                return _old.BufferWritten;
            }
        }

        public void Write(byte value)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value);
            if (!UseNew) _old.WriteUInt8(value);
        }

        public void Write(sbyte value)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value);
            if (!UseNew) _old.WriteInt8(value);
        }

        public void Write(bool value)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value);
            if (!UseNew) _old.WriteBool(value);
        }

        public void Write(ushort value)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value);
            if (!UseNew) _old.WriteUInt16BE(value);
        }

        public void Write(uint value)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value);
            if (!UseNew) _old.WriteUInt32BE(value);
        }

        public void Write(ulong value)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value);
            if (!UseNew) _old.WriteUInt64BE(value);
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(buffer);
            if (!UseNew) _old.Write(buffer);
        }

        /// <summary>Zero bytes; a count of zero or less writes nothing.</summary>
        public void WriteZero(int count)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteZero(count);
            if (!UseNew) _old.WriteZero(count);
        }

        /// <summary>A Cp1252 string and its NUL (what the old writer's WriteASCII(string) wrote).</summary>
        public void WriteCp1252Null(string value)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteCp1252Null(value);
            if (!UseNew) _old.WriteASCII(value);
        }

        /// <summary>A Cp1252 string in <paramref name="length"/> bytes, zero padded.</summary>
        public void WriteCp1252(string value, int length)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteCp1252(value, length);
            if (!UseNew) _old.WriteASCII(value, length);
        }

        public void WriteBigUniNull(string value)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteBigUniNull(value);
            if (!UseNew) _old.WriteUnicodeBE(value);
        }

        public void WriteBigUni(string value, int fixedLength)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteBigUni(value, fixedLength);
            if (!UseNew) _old.WriteUnicodeBE(value, fixedLength);
        }

        public void WriteLittleUniNull(string value)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteLittleUniNull(value);
            if (!UseNew) _old.WriteUnicodeLE(value);
        }

        public void WriteLittleUni(string value, int fixedLength)
        {
            if (_mode != SpanBufferMode.Old) _new.WriteLittleUni(value, fixedLength);
            if (!UseNew) _old.WriteUnicodeLE(value, fixedLength);
        }

        /// <summary>UTF-8 without a NUL; with <paramref name="fixedLength"/> >= 0, at most that many chars, zero padded to
        /// that many bytes.</summary>
        public void WriteUTF8(string value, int fixedLength)
        {
            if (_mode != SpanBufferMode.Old) _new.Write(value, SpanText.UTF8, fixedLength);
            if (!UseNew) _old.WriteUTF8(value, fixedLength);
        }

        public int Seek(int offset, SeekOrigin origin)
        {
            int position = 0;

            if (_mode != SpanBufferMode.Old) position = _new.Seek(offset, origin);

            if (!UseNew)
            {
                _old.Seek(offset, origin);
                position = _old.Position;
            }

            return position;
        }

        public void Dispose()
        {
            if (_mode != SpanBufferMode.Old) _new.Dispose();
            if (!UseNew) _old.Dispose();
        }
    }
}
