// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;

namespace Uo.Buffers;

// Members the client's earlier ValueStringBuilder had and the ModernUO copy lacks, kept so call sites keep their meaning.
// Note the copy's ToString() does not dispose (the earlier one did): every call site disposes after ToString().
public ref partial struct ValueStringBuilder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        int pos = _length;

        if ((uint)pos < (uint)_chars.Length)
        {
            _chars[pos] = c;
            _length = pos + 1;
        }
        else
        {
            Grow(1);
            _chars[_length++] = c;
        }
    }

    public void Insert(int index, scoped ReadOnlySpan<char> s)
    {
        int count = s.Length;

        if (_length > _chars.Length - count)
        {
            Grow(count);
        }

        int remaining = _length - index;
        _chars.Slice(index, remaining).CopyTo(_chars.Slice(index + count));
        s.CopyTo(_chars.Slice(index));
        _length += count;
    }

    /// <summary>Replaces the first occurrence of <paramref name="oldChars"/> with <paramref name="newChars"/>.</summary>
    public void Replace(scoped ReadOnlySpan<char> oldChars, scoped ReadOnlySpan<char> newChars) =>
        Replace(oldChars, newChars, 0, _length);

    /// <summary>Replaces the first occurrence of <paramref name="oldChars"/> in the range.</summary>
    public void Replace(scoped ReadOnlySpan<char> oldChars, scoped ReadOnlySpan<char> newChars, int startIndex, int count)
    {
        int indexOf = _chars.Slice(startIndex, count).IndexOf(oldChars);

        if (indexOf == -1)
        {
            return;
        }

        indexOf += startIndex;
        int common = Math.Min(oldChars.Length, newChars.Length);
        newChars.Slice(0, common).CopyTo(_chars.Slice(indexOf));

        if (newChars.Length > oldChars.Length)
        {
            Insert(indexOf + common, newChars.Slice(common));
        }
        else if (newChars.Length < oldChars.Length)
        {
            Remove(indexOf + common, oldChars.Length - common);
        }
    }
}
