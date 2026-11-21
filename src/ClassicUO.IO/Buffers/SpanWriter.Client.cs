// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Utility;

namespace ClassicUO.IO;

// Client members of the copied SpanWriter: the byte layouts StackDataWriter produced, so outgoing packets stay identical.
public ref partial struct SpanWriter
{
    /// <summary>Everything written, up to the furthest position reached (a length patched by Seek does not shorten it).</summary>
    public Span<byte> BufferWritten => _buffer[..BytesWritten];

    /// <summary>Clear with StackDataWriter.WriteZero's rule: a count of zero or less writes nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteZero(int count)
    {
        if (count > 0)
        {
            Clear(count);
        }
    }

    /// <summary>A Cp1252 string and its NUL (StackDataWriter.WriteASCII(string)).</summary>
    public void WriteCp1252Null(string str)
    {
        if (!string.IsNullOrEmpty(str))
        {
            GrowIfNeeded(str.Length);
            Position += StringHelper.GetBytesCp1252(str, _buffer[_position..]);
        }

        Write((byte)0);
    }

    /// <summary>
    /// A Cp1252 string in <paramref name="length"/> bytes, zero padded (StackDataWriter.WriteASCII(string, int), including its
    /// rule that an empty string writes at least one zero byte).
    /// </summary>
    public void WriteCp1252(string str, int length)
    {
        int start = Position;

        if (string.IsNullOrEmpty(str))
        {
            WriteZero(1);
        }
        else if (length > 0)
        {
            // A string longer than the field is cut at length chars; the rest of the field is zeroed.
            GrowIfNeeded(length);
            StringHelper.GetBytesCp1252Fixed(str, _buffer.Slice(_position, length));
            Position += length;

            return;
        }
        else
        {
            GrowIfNeeded(str.Length);
            Position += StringHelper.GetBytesCp1252(str, _buffer[_position..]);
        }

        if (length > -1 && Position > start)
        {
            WriteZero(length - (Position - start));
        }
    }
}
