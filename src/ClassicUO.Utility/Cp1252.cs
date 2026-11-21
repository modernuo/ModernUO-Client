/*************************************************************************
 * ModernUO                                                              *
 * Copyright 2019-2026 - ModernUO Development Team                       *
 * Email: hi@modernuo.com                                                *
 * File: TextEncoding.cs                                                 *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 *************************************************************************/

// Adapted from ModernUO Projects/Server/Text/TextEncoding.cs (commit 8e2ddc7ab): GetStringLatin1's single-allocation decode
// and its safe mode (one SearchValues pre-scan, filtering into a stack or pooled char buffer only on a hit). Used here
// outside the GPL under agreement from its author, Kamron Batman. Changes: Windows-1252 instead of Latin1 (bytes
// 0x80-0x9F decode to the typographic chars, writes map them back), the client's safe rule (StringHelper.IsSafeChar drops
// only C0 here), span encoders with the client's '?' rules, and ArrayPool<char>.Shared instead of STArrayPool.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace ClassicUO.Utility
{
    /// <summary>
    /// Windows-1252, the code page of the server's one-byte ("ASCII") strings. Bytes outside 0x80-0x9F are Latin1.
    /// </summary>
    public static class Cp1252
    {
        private const int StackChars = 256;

        // Bytes 0x80-0x9F. 0x81, 0x8D, 0x8F, 0x90 and 0x9D are unassigned in Windows-1252 and stay their Latin1 char.
        private static ReadOnlySpan<char> C1ToChar =>
            "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
            "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

        // Bytes a decode must remap (C1) or a safe decode must drop (C0: the decoded chars that fail IsSafeChar).
        private static readonly SearchValues<byte> _remapOrUnsafe = SearchValues.Create(
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97,
            0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F
        );

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ToChar(byte b) => (uint)(b - 0x80) < 0x20 ? C1ToChar[b - 0x80] : (char)b;

        /// <summary>Decodes Cp1252 bytes; allocates only the result.</summary>
        public static string GetString(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            // Benchmark (measure/text-encoding, JIT and NativeAOT): up to 16 bytes the table loop is as fast as Latin1 and
            // needs no scan; above, the two-pass decode (scan, Latin1 or widen-and-patch) wins.
            return bytes.Length <= 16 ? GetStringTable(bytes) : GetStringTwoPass(bytes);
        }

        // Decode variants, public so the benchmark (measure/text-encoding/bench) can compare them; GetString dispatches.

        /// <summary>Variant 0: scan for 0x80-0x9F, then decode into a stack or pooled buffer and copy into the string.</summary>
        public static string GetStringBuffered(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            int first = bytes.IndexOfAnyInRange((byte)0x80, (byte)0x9F);

            return first < 0 ? Encoding.Latin1.GetString(bytes) : Decode(bytes, first, false);
        }

        /// <summary>
        /// Variant 1, two passes: scan for 0x80-0x9F; none: Latin1.GetString; hits: string.Create, Latin1 widen straight into
        /// the string, then patch the hits from the table (no intermediate buffer).
        /// </summary>
        public static string GetStringTwoPass(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            int first = bytes.IndexOfAnyInRange((byte)0x80, (byte)0x9F);

            if (first < 0)
            {
                return Encoding.Latin1.GetString(bytes);
            }

            return string.Create(bytes.Length, bytes, static (chars, b) =>
            {
                Encoding.Latin1.GetChars(b, chars);
                int i = b.IndexOfAnyInRange((byte)0x80, (byte)0x9F);

                while (i >= 0)
                {
                    chars[i] = C1ToChar[b[i] - 0x80];
                    int next = b.Slice(i + 1).IndexOfAnyInRange((byte)0x80, (byte)0x9F);
                    i = next < 0 ? -1 : i + 1 + next;
                }
            });
        }

        /// <summary>
        /// Variant 2, one SIMD pass: widen bytes to chars and test 0x80-0x9F in the same pass (Vector256 or Vector128, any
        /// platform the vector API accelerates); clean blocks are stored as widened, dirty blocks patched scalar; scalar tail.
        /// </summary>
        public static string GetStringSimd(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            return string.Create(bytes.Length, bytes, static (chars, b) => WidenCp1252(b, chars));
        }

        private static void WidenCp1252(ReadOnlySpan<byte> src, Span<char> dst)
        {
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref ushort d = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(dst));
            int n = src.Length;
            int i = 0;

            if (Vector256.IsHardwareAccelerated && n >= Vector256<byte>.Count)
            {
                Vector256<byte> lo = Vector256.Create((byte)0x80);
                Vector256<byte> span = Vector256.Create((byte)0x1F);

                for (; i <= n - Vector256<byte>.Count; i += Vector256<byte>.Count)
                {
                    Vector256<byte> v = Vector256.LoadUnsafe(ref s, (nuint)i);
                    (Vector256<ushort> a, Vector256<ushort> c) = Vector256.Widen(v);
                    a.StoreUnsafe(ref d, (nuint)i);
                    c.StoreUnsafe(ref d, (nuint)(i + Vector256<ushort>.Count));

                    if (Vector256.LessThanOrEqualAny(v - lo, span))
                    {
                        PatchC1(src, dst, i, Vector256<byte>.Count);
                    }
                }
            }

            if (Vector128.IsHardwareAccelerated && n - i >= Vector128<byte>.Count)
            {
                Vector128<byte> lo = Vector128.Create((byte)0x80);
                Vector128<byte> span = Vector128.Create((byte)0x1F);

                for (; i <= n - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref s, (nuint)i);
                    (Vector128<ushort> a, Vector128<ushort> c) = Vector128.Widen(v);
                    a.StoreUnsafe(ref d, (nuint)i);
                    c.StoreUnsafe(ref d, (nuint)(i + Vector128<ushort>.Count));

                    if (Vector128.LessThanOrEqualAny(v - lo, span))
                    {
                        PatchC1(src, dst, i, Vector128<byte>.Count);
                    }
                }
            }

            for (; i < n; i++)
            {
                dst[i] = ToChar(src[i]);
            }
        }

        private static void PatchC1(ReadOnlySpan<byte> src, Span<char> dst, int start, int count)
        {
            for (int j = start, end = start + count; j < end; j++)
            {
                uint k = (uint)(src[j] - 0x80);

                if (k < 0x20)
                {
                    dst[j] = C1ToChar[(int)k];
                }
            }
        }

        // Variant 3: every byte through a 256-entry char table (branch-free scalar).
        private static readonly char[] _byteToChar = BuildByteToChar();

        private static char[] BuildByteToChar()
        {
            var t = new char[256];

            for (int i = 0; i < 256; i++)
            {
                t[i] = (uint)(i - 0x80) < 0x20 ? C1ToChar[i - 0x80] : (char)i;
            }

            return t;
        }

        /// <summary>Variant 3: a scalar 256-entry table lookup per byte, written straight into the string.</summary>
        public static string GetStringTable(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            return string.Create(bytes.Length, bytes, static (chars, b) =>
            {
                char[] t = _byteToChar;

                for (int i = 0; i < b.Length; i++)
                {
                    chars[i] = t[b[i]];
                }
            });
        }

        /// <summary>
        /// Decodes Cp1252 bytes and, when <paramref name="safe"/>, drops every char that fails StringHelper.IsSafeChar (for a
        /// Cp1252 decode those are the C0 bytes 0x00-0x1F). Allocates only the result.
        /// </summary>
        public static string GetString(ReadOnlySpan<byte> bytes, bool safe)
        {
            if (!safe)
            {
                return GetString(bytes);
            }

            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            int first = bytes.IndexOfAny(_remapOrUnsafe);

            if (first < 0)
            {
                return Encoding.Latin1.GetString(bytes);
            }

            int c0 = bytes.Slice(first).IndexOfAnyInRange((byte)0x00, (byte)0x1F);

            return c0 < 0 ? GetString(bytes) : Decode(bytes, first, true);
        }

        private static string Decode(ReadOnlySpan<byte> bytes, int first, bool safe)
        {
            char[] rented = null;
            Span<char> chars = bytes.Length <= StackChars
                ? stackalloc char[StackChars]
                : rented = ArrayPool<char>.Shared.Rent(bytes.Length);

            Encoding.Latin1.GetChars(bytes.Slice(0, first), chars);
            int n = first;

            for (int i = first; i < bytes.Length; i++)
            {
                byte b = bytes[i];

                if (safe && b < 0x20)
                {
                    continue;
                }

                chars[n++] = ToChar(b);
            }

            string result = new string(chars.Slice(0, n));

            if (rented != null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }

            return result;
        }

        /// <summary>
        /// Encodes <paramref name="chars"/> into <paramref name="destination"/> (at least chars.Length bytes) and returns the
        /// byte count: one byte per code point. U+0080-U+009F, unmapped chars, surrogate pairs and lone surrogates become '?'.
        /// </summary>
        public static int GetBytes(ReadOnlySpan<char> chars, Span<byte> destination)
        {
            // One pass: narrows the ASCII prefix and stops at the first non-ASCII char.
            if (Ascii.FromUtf16(chars, destination, out int ascii) == OperationStatus.Done)
            {
                return ascii;
            }

            return EncodeFrom(chars, destination, ascii);
        }

        /// <summary>Encode variant: two range scans for a char that needs remapping, Latin1 narrowing before it.</summary>
        public static int GetBytesLatin1Scan(ReadOnlySpan<char> chars, Span<byte> destination)
        {
            int first = IndexOfRemap(chars);

            if (first < 0)
            {
                return Encoding.Latin1.GetBytes(chars, destination);
            }

            if (first > 0)
            {
                Encoding.Latin1.GetBytes(chars.Slice(0, first), destination);
            }

            return EncodeFrom(chars, destination, first);
        }

        private static int EncodeFrom(ReadOnlySpan<char> chars, Span<byte> destination, int first)
        {
            int n = first;

            for (int i = first; i < chars.Length; i++)
            {
                char c = chars[i];

                if (char.IsHighSurrogate(c) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                {
                    i++;
                    destination[n++] = (byte)'?';
                }
                else
                {
                    destination[n++] = ToByte(c);
                }
            }

            return n;
        }

        // First char whose byte is not its Latin1 value (U+0080-U+009F or above U+00FF), or -1.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IndexOfRemap(ReadOnlySpan<char> chars)
        {
            int high = chars.IndexOfAnyExceptInRange('\0', '\xFF');
            ReadOnlySpan<char> scan = high < 0 ? chars : chars.Slice(0, high);
            int c1 = scan.IndexOfAnyInRange('\x80', '\x9F');

            return c1 >= 0 ? c1 : high;
        }

        /// <summary>
        /// Fixed-length field:encodes the first destination.Length chars of <paramref name="chars"/> (a surrogate pair that
        /// starts inside the field counts as one '?') and zero fills the rest of <paramref name="destination"/>.
        /// </summary>
        public static void GetBytesFixed(ReadOnlySpan<char> chars, Span<byte> destination)
        {
            int n = GetBytes(chars.Length > destination.Length ? chars.Slice(0, destination.Length) : chars, destination);
            destination.Slice(n).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ToByte(char c)
        {
            if (c < 0x80)
            {
                return (byte)c;
            }

            if (c <= 0x9F)
            {
                return (byte)'?';
            }

            return c <= 0xFF ? (byte)c : ToByteTypographic(c);
        }

        /// <summary>
        /// The ASCII-font glyph byte of a char: like <see cref="ToByte"/>, but U+0080-U+009F stay their byte (an unassigned
        /// Cp1252 byte decodes to that char, and fonts 4 and 8 draw moon phases at 0x80-0x87), so every decoded byte indexes
        /// its own glyph. Chars outside Cp1252 give '?'.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ToGlyphByte(char c) => c <= 0xFF ? (byte)c : ToByteTypographic(c);

        private static byte ToByteTypographic(char c)
        {
            switch (c)
            {
                case '€': return 0x80; // €
                case '‚': return 0x82; // ‚
                case 'ƒ': return 0x83; // ƒ
                case '„': return 0x84; // „
                case '…': return 0x85; // …
                case '†': return 0x86; // †
                case '‡': return 0x87; // ‡
                case 'ˆ': return 0x88; // ˆ
                case '‰': return 0x89; // ‰
                case 'Š': return 0x8A; // Š
                case '‹': return 0x8B; // ‹
                case 'Œ': return 0x8C; // Œ
                case 'Ž': return 0x8E; // Ž
                case '‘': return 0x91; // ‘
                case '’': return 0x92; // ’
                case '“': return 0x93; // “
                case '”': return 0x94; // ”
                case '•': return 0x95; // •
                case '–': return 0x96; // –
                case '—': return 0x97; // —
                case '˜': return 0x98; // ˜
                case '™': return 0x99; // ™
                case 'š': return 0x9A; // š
                case '›': return 0x9B; // ›
                case 'œ': return 0x9C; // œ
                case 'ž': return 0x9E; // ž
                case 'Ÿ': return 0x9F; // Ÿ
                default: return (byte)'?';
            }
        }

        /// <summary>
        /// The bytes of <c>s</c> one at a time (the first <c>length</c> UTF-16 units when length &gt; 0), without an enumerator
        /// allocation. StringHelper.StringToCp1252Bytes returns it so `foreach` call sites compile unchanged.
        /// </summary>
        public readonly struct ByteEnumerable
        {
            private readonly string _s;
            private readonly int _length;

            public ByteEnumerable(string s, int length)
            {
                _s = s;
                _length = length > 0 ? Math.Min(length, s.Length) : s.Length;
            }

            public ByteEnumerator GetEnumerator() => new ByteEnumerator(_s, _length);
        }

        public struct ByteEnumerator
        {
            private readonly string _s;
            private readonly int _length;
            private int _next;

            public ByteEnumerator(string s, int length)
            {
                _s = s;
                _length = length;
                _next = 0;
                Current = 0;
            }

            public byte Current { get; private set; }

            public bool MoveNext()
            {
                int i = _next;

                if (i >= _length)
                {
                    return false;
                }

                char c = _s[i];

                // A pair that starts inside the length is read whole (as the old iterator's char.IsSurrogatePair(s, i)).
                if (char.IsHighSurrogate(c) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1]))
                {
                    Current = (byte)'?';
                    _next = i + 2;
                }
                else
                {
                    Current = ToByte(c);
                    _next = i + 1;
                }

                return true;
            }
        }
    }
}
