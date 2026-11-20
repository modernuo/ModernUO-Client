// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using SDL3;

namespace ClassicUO.Utility
{
    public static class StringHelper
    {
        private static readonly char[] _dots = { '.', ',', ';', '!' };

        // A struct enumerable (no enumerator allocation); `foreach` call sites compile unchanged. Span writers use
        // GetBytesCp1252.
        public static Cp1252.ByteEnumerable StringToCp1252Bytes(string s, int length = -1) => new Cp1252.ByteEnumerable(s, length);

        /// <summary>The text_string_cache switch (ClassicUO.Game.TextStringCache sets it): null decodes every string.</summary>
        public static Cp1252StringCache Cp1252Cache;

        public delegate void Cp1252CacheCheckHandler(ReadOnlySpan<byte> bytes, string result);

        /// <summary>CUO_TEXT_CACHE_CHECK (developer builds): sees every cached decode with its bytes.</summary>
        public static Cp1252CacheCheckHandler Cp1252CacheCheck;

        public static string Cp1252ToString(ReadOnlySpan<byte> strCp1252)
        {
            Cp1252StringCache cache = Cp1252Cache;

            if (cache == null)
            {
                return Cp1252.GetString(strCp1252);
            }

            string s = cache.GetString(strCp1252);
            Cp1252CacheCheck?.Invoke(strCp1252, s);

            return s;
        }

        /// <summary>Cp1252ToString, then (when safe) the chars that fail <see cref="IsSafeChar"/> dropped; one allocation.</summary>
        public static string Cp1252ToString(ReadOnlySpan<byte> strCp1252, bool safe) => Cp1252.GetString(strCp1252, safe);

        /// <summary>Cp1252 bytes of <paramref name="chars"/> (destination at least chars.Length bytes); returns the count.</summary>
        public static int GetBytesCp1252(ReadOnlySpan<char> chars, Span<byte> destination) => Cp1252.GetBytes(chars, destination);

        /// <summary>A fixed-length Cp1252 field: the string truncated to destination.Length chars, the rest zeroed.</summary>
        public static void GetBytesCp1252Fixed(ReadOnlySpan<char> chars, Span<byte> destination) => Cp1252.GetBytesFixed(chars, destination);

        /// <summary>
        /// <paramref name="str"/> without the chars that fail <see cref="IsSafeChar"/>; the same instance when it has none
        /// (one vectorized scan).
        /// </summary>
        public static string RemoveUnsafeChars(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return str;
            }

            ReadOnlySpan<char> chars = str.AsSpan();
            int first = chars.IndexOfAnyExceptInRange(' ', '�');

            if (first < 0)
            {
                return str;
            }

            char[] rented = null;
            Span<char> buffer = chars.Length <= 256
                ? stackalloc char[256]
                : rented = System.Buffers.ArrayPool<char>.Shared.Rent(chars.Length);

            chars.Slice(0, first).CopyTo(buffer);
            int n = first;

            for (int i = first + 1; i < chars.Length; i++)
            {
                if (IsSafeChar(chars[i]))
                {
                    buffer[n++] = chars[i];
                }
            }

            string result = new string(buffer.Slice(0, n));

            if (rented != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }

            return result;
        }

        public static string CapitalizeFirstCharacter(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            if (str.Length == 1)
            {
                return char.ToUpper(str[0]).ToString();
            }

            return char.ToUpper(str[0]) + str.Substring(1);
        }


        // In place; same result as the string overload (each character is tested before it is written, and the
        // next one is still the original).
        public static void CapitalizeAllWords(Span<char> chars)
        {
            bool capitalizeNext = true;

            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];

                if (capitalizeNext)
                {
                    chars[i] = char.ToUpper(c);
                }

                if (!char.IsWhiteSpace(c))
                {
                    capitalizeNext = i + 1 < chars.Length && char.IsWhiteSpace(chars[i + 1]);
                }
            }
        }

        public static string CapitalizeAllWords(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            Span<char> span = stackalloc char[str.Length];
            ValueStringBuilder sb = new ValueStringBuilder(span);
            bool capitalizeNext = true;

            for (int i = 0; i < str.Length; i++)
            {
                sb.Append(capitalizeNext ? char.ToUpper(str[i]) : str[i]);

                if (!char.IsWhiteSpace(str[i]))
                {
                    capitalizeNext = i + 1 < str.Length && char.IsWhiteSpace(str[i + 1]);
                }
            }

            string ss = sb.ToString();

            sb.Dispose();

            return ss;
        }

        public static string CapitalizeWordsByLimitator(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            Span<char> span = stackalloc char[str.Length];
            ValueStringBuilder sb = new ValueStringBuilder(span);

            bool capitalizeNext = true;

            for (int i = 0; i < str.Length; i++)
            {
                sb.Append(capitalizeNext ? char.ToUpper(str[i]) : str[i]);
                capitalizeNext = false;

                for (int j = 0; j < _dots.Length; j++)
                {
                    if (str[i] == _dots[j])
                    {
                        capitalizeNext = true;

                        break;
                    }
                }
            }

            string ss = sb.ToString();

            sb.Dispose();

            return ss;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSafeChar(int c)
        {
            return c >= 0x20 && c < 0xFFFE;
        }

        public static void AddSpaceBeforeCapital(string[] str, bool checkAcronyms = true)
        {
            for (int i = 0; i < str.Length; i++)
            {
                str[i] = AddSpaceBeforeCapital(str[i], checkAcronyms);
            }
        }

        public static string AddSpaceBeforeCapital(string str, bool checkAcronyms = true)
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                return "";
            }

            ValueStringBuilder sb = new ValueStringBuilder(str.Length * 2);
            sb.Append(str[0]);

            for (int i = 1, len = str.Length - 1; i <= len; i++)
            {
                if (char.IsUpper(str[i]))
                {
                    if (str[i - 1] != ' ' && !char.IsUpper(str[i - 1]) || checkAcronyms && char.IsUpper(str[i - 1]) && i < len && !char.IsUpper(str[i + 1]))
                    {
                        sb.Append(' ');
                    }
                }

                sb.Append(str[i]);
            }

            string s = sb.ToString();

            sb.Dispose();

            return s;
        }

        public static string RemoveUpperLowerChars(string str, bool removelower = true)
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                return "";
            }

            Span<char> span = stackalloc char[str.Length];
            ValueStringBuilder sb = new ValueStringBuilder(span);

            for (int i = 0; i < str.Length; i++)
            {
                if (char.IsUpper(str[i]) == removelower || str[i] == ' ')
                {
                    sb.Append(str[i]);
                }
            }

            string ss = sb.ToString();

            sb.Dispose();

            return ss;
        }

        public static string IntToAbbreviatedString(int num)
        {
            if (num > 999999 || num < -999999)
            {
                return string.Format("{0}M+", num / 1000000);
            }

            if (num > 999 || num < -999)
            {
                return string.Format("{0}K+", num / 1000);
            }

            return num.ToString();
        }

        public static string GetClipboardText(bool multiline)
        {
            if (SDL.SDL_HasClipboardText())
            {
                string s = multiline ? SDL.SDL_GetClipboardText() : SDL.SDL_GetClipboardText()?.Replace('\n', ' ') ?? null;

                if (!string.IsNullOrEmpty(s))
                {
                    if (s.IndexOf('\r') >= 0)
                    {
                        s = s.Replace("\r", "");
                    }

                    if (s.IndexOf('\t') >= 0)
                    {
                        return s.Replace("\t", "   ");
                    }

                    return s;
                }
            }

            return null;
        }

        public static string GetPluralAdjustedString(string str, bool plural = false)
        {
            if (str.Contains("%"))
            {
                string[] parts = str.Split(new[] { '%' }, System.StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 2)
                {
                    return str;
                }

                Span<char> span = stackalloc char[str.Length];
                ValueStringBuilder sb = new ValueStringBuilder(span);

                sb.Append(parts[0]);

                if (parts[1].Contains("/"))
                {
                    string[] pluralparts = parts[1].Split('/');

                    if (plural)
                    {
                        sb.Append(pluralparts[0]);
                    }
                    else if (pluralparts.Length > 1)
                    {
                        sb.Append(pluralparts[1]);
                    }
                }
                else if (plural)
                {
                    sb.Append(parts[1]);
                }

                if (parts.Length == 3)
                {
                    sb.Append(parts[2]);
                }

                string ss = sb.ToString();

                sb.Dispose();

                return ss;
            }

            return str;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool UnsafeCompare(char* buffer, string str, int length)
        {
            for (int i = 0; i < length && i < str.Length; ++i)
            {
                var c0 = char.IsLetter(buffer[i]) ? char.ToLowerInvariant(buffer[i]) : buffer[i];
                var c1 = char.IsLetter(str[i]) ? char.ToLowerInvariant(str[i]) : str[i];

                if (c0 != c1)
                {
                    return false;
                }
            }

            return true;
        }
    }
}