// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.IO.Compression;

namespace ClassicUO.Utility
{
    /// <summary>
    /// zlib (RFC 1950) decompression into a caller-supplied span, on the .NET 11 <see cref="ZLibDecoder"/>.
    /// One decoder per thread is reused through Reset(), so a call allocates nothing after the first on that thread.
    /// </summary>
    public static class ZlibInflater
    {
        [ThreadStatic] private static ZLibDecoder _decoder;

        /// <summary>
        /// Inflates <paramref name="source"/> into <paramref name="dest"/>. Returns false for a truncated or corrupt
        /// stream, or when the output does not fit; <paramref name="written"/> is then undefined. A shorter result than
        /// dest.Length succeeds, as with the native uncompress it replaces: the caller compares written when it cares.
        /// </summary>
        public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> dest, out int written)
        {
            // Misconfigured servers may send a wrong dlen; the stream then fails or yields fewer bytes, and the caller
            // drops the packet. No further handling.
            ZLibDecoder decoder = _decoder ??= new ZLibDecoder();
            decoder.Reset();

            OperationStatus status;

            if (dest.IsEmpty)
            {
                // The decoder reports DestinationTooSmall for an empty destination even when the payload is empty.
                Span<byte> probe = stackalloc byte[1];
                status = decoder.Decompress(source, probe, out _, out written);

                if (status != OperationStatus.Done || written != 0)
                {
                    written = 0;
                    return false;
                }

                return true;
            }

            status = decoder.Decompress(source, dest, out _, out written);

            return status == OperationStatus.Done;
        }
    }
}
