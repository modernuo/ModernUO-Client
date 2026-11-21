// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassicUO.IO;

// Client members of the copied SpanReader. They throw past the end like the copied members (net-rework step 2: a short
// packet is caught once at dispatch and ends the connection).
public ref partial struct SpanReader
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count) => Seek(count, SeekOrigin.Current);

    /// <summary>A NUL-terminated (or rest-of-packet) Cp1252 string: what StackDataReader.ReadASCII returned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadCp1252() => ReadStringCp1252(false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadCp1252(int fixedLength) => ReadStringCp1252(false, fixedLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadCp1252Safe() => ReadStringCp1252(true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadCp1252Safe(int fixedLength) => ReadStringCp1252(true, fixedLength);

    // ReadStringLatin1 with the client's Cp1252 decoding (bytes 0x80-0x9F are Cp1252 letters, not C1 controls).
    private string ReadStringCp1252(bool safeString, int fixedLength = -1)
    {
        if (fixedLength == 0)
        {
            return "";
        }

        var isFixedLength = fixedLength > -1;

        int size = isFixedLength ? fixedLength : Remaining;
        if (size > Remaining)
        {
            throw new EndOfStreamException("Cannot read past the end of the buffer.");
        }

        var span = _buffer.Slice(Position, size);
        var index = span.IndexOf((byte)0);

        if (index > -1)
        {
            span = _buffer.Slice(Position, index);
        }

        Position += isFixedLength || index < 0 ? size : index + 1;

        return SpanText.GetStringCp1252(span, safeString);
    }

    /// <summary>
    /// A fixed-length little-endian UTF-16 string decoded into <paramref name="destination"/> instead of a new string (the
    /// allocation-free 0xD6 argument read); returns the chars written.
    /// </summary>
    public int ReadLittleUni(int fixedLength, scoped Span<char> destination)
    {
        if (fixedLength <= 0)
        {
            return 0;
        }

        int size = fixedLength * 2;

        if (size > Remaining)
        {
            throw new EndOfStreamException("Cannot read past the end of the buffer.");
        }

        ReadOnlySpan<byte> slice = _buffer.Slice(Position, size);
        int index = MemoryMarshal.Cast<byte, char>(slice).IndexOf('\0') * 2;
        int written = Encoding.Unicode.GetChars(slice.Slice(0, index < 0 ? size : index), destination);
        Position += size;

        return written;
    }
}
