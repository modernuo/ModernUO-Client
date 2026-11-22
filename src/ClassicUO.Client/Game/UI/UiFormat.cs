// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Game.UI
{
    // G2: text pieces written into a char buffer for the refreshing gumps. The numbers go through their own TryFormat
    // (current culture, as ToString and string interpolation), not through a generic interpolation handler, whose tier-0
    // code boxes each value until it tiers up.
    internal static class UiFormat
    {
        public static void Put(Span<char> destination, ref int length, ReadOnlySpan<char> text)
        {
            text.CopyTo(destination.Slice(length));
            length += text.Length;
        }

        public static void Put(Span<char> destination, ref int length, long value, ReadOnlySpan<char> format = default)
        {
            value.TryFormat(destination.Slice(length), out int written, format);
            length += written;
        }

        public static void Put(Span<char> destination, ref int length, float value, ReadOnlySpan<char> format)
        {
            value.TryFormat(destination.Slice(length), out int written, format);
            length += written;
        }

        public static void Put(Span<char> destination, ref int length, decimal value, ReadOnlySpan<char> format)
        {
            value.TryFormat(destination.Slice(length), out int written, format);
            length += written;
        }

        // {x,-width}: pads what was written since start with spaces up to width
        public static void PadRight(Span<char> destination, ref int length, int start, int width)
        {
            while (length - start < width)
            {
                destination[length++] = ' ';
            }
        }
    }
}
