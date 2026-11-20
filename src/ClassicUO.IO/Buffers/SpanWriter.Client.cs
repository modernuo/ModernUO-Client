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
            foreach (var b in StringHelper.StringToCp1252Bytes(str))
            {
                Write(b);
            }
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
        else
        {
            foreach (var b in StringHelper.StringToCp1252Bytes(str, length))
            {
                Write(b);
            }
        }

        if (length > -1 && Position > start)
        {
            WriteZero(length - (Position - start));
        }
    }
}
