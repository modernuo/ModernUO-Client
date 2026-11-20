// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ClassicUO.Utility;

namespace ClassicUO.IO;

/// <summary>
/// Text decoding for the copied SpanReader / SpanWriter (it stands in for ModernUO's Server.Text.TextEncoding). The rules are
/// the client's, the same as StackDataReader / StackDataWriter: the encodings are the BCL ones the old reader used, and a
/// safe read drops every char that fails <see cref="StringHelper.IsSafeChar"/>, so both readers return the same strings.
/// </summary>
public static class SpanText
{
    public static Encoding UTF8 => Encoding.UTF8;
    public static Encoding Unicode => Encoding.BigEndianUnicode;
    public static Encoding UnicodeLE => Encoding.Unicode;
    public static Encoding Latin1 => Encoding.Latin1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetByteLengthForEncoding(this Encoding encoding) =>
        encoding.BodyName switch
        {
            "utf-16BE" => 2,
            "utf-16"   => 2,
            "utf-32BE" => 3,
            "utf-32"   => 3,
            _          => 1
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOfTerminator(this ReadOnlySpan<byte> buffer, int sizeT) =>
        sizeT switch
        {
            2 => MemoryMarshal.Cast<byte, char>(buffer).IndexOf((char)0) * 2,
            4 => MemoryMarshal.Cast<byte, uint>(buffer).IndexOf((uint)0) * 4,
            _ => buffer.IndexOf((byte)0)
        };

    public static string GetString(ReadOnlySpan<byte> span, Encoding encoding, bool safeString) =>
        Filter(encoding.GetString(span), safeString);

    public static string GetStringAscii(ReadOnlySpan<byte> bytes, bool safeString) => Filter(Encoding.ASCII.GetString(bytes), safeString);

    public static string GetStringLatin1(ReadOnlySpan<byte> bytes, bool safeString) => Filter(Encoding.Latin1.GetString(bytes), safeString);

    public static string GetStringUtf8(ReadOnlySpan<byte> bytes, bool safeString) => Filter(UTF8.GetString(bytes), safeString);

    public static string GetStringBigUni(ReadOnlySpan<byte> bytes, bool safeString) => Filter(Unicode.GetString(bytes), safeString);

    public static string GetStringLittleUni(ReadOnlySpan<byte> bytes, bool safeString) => Filter(UnicodeLE.GetString(bytes), safeString);

    /// <summary>Cp1252, as the server's "ASCII" strings are read by the client (StringHelper.Cp1252ToString).</summary>
    public static string GetStringCp1252(ReadOnlySpan<byte> bytes, bool safeString) =>
        bytes.IsEmpty ? string.Empty : Filter(StringHelper.Cp1252ToString(bytes), safeString);

    /// <summary>StackDataReader's safe filter: every char that is not <see cref="StringHelper.IsSafeChar"/> is dropped.</summary>
    public static string Filter(string result, bool safeString)
    {
        if (!safeString)
        {
            return result;
        }

        ReadOnlySpan<char> chars = result.AsSpan();
        int first = -1;

        for (int i = 0; i < chars.Length; i++)
        {
            if (!StringHelper.IsSafeChar(chars[i]))
            {
                first = i;

                break;
            }
        }

        if (first < 0)
        {
            return result;
        }

        Span<char> buff = stackalloc char[256];
        var sb = new ValueStringBuilder(buff);
        sb.Append(chars.Slice(0, first));

        for (int i = first + 1; i < chars.Length; i++)
        {
            if (StringHelper.IsSafeChar(chars[i]))
            {
                sb.Append(chars[i]);
            }
        }

        string filtered = sb.ToString();
        sb.Dispose();

        return filtered;
    }
}
