// SPDX-License-Identifier: BSD-2-Clause
// author: Max Kellermann <max.kellermann@gmail.com>

using System;
using System.Text;

namespace ClassicUO.IO
{
    /// <summary>
    ///     Utilities for reading data from raw buffers.
    /// </summary>
    public class ReaderUtil
    {
        public static string ReadFixedSizeString(ReadOnlySpan<byte> src)
        {
            return Encoding.UTF8.GetString(src).TrimEnd('\0');
        }

        public static unsafe string ReadFixedSizeString(byte* data, int size)
        {
            return ReadFixedSizeString(new ReadOnlySpan<byte>(data, size));
        }
    }
}
