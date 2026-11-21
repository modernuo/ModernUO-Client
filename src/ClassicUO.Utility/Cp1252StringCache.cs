// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ClassicUO.Utility
{
    /// <summary>
    /// A bounded cache of decoded Cp1252 strings (names, labels and system lines repeat), keyed by the bytes: a hit returns
    /// the earlier string and allocates nothing. Two-way set associative with one LRU bit per set, so a lookup touches at
    /// most two entries and eviction is free. Single-threaded: lookups from any thread but the owner decode without the
    /// cache. Dependency-free (System only) so it can be copied with <see cref="Cp1252"/>.
    /// </summary>
    public sealed class Cp1252StringCache
    {
        private readonly int _owner;
        private readonly int _setMask;
        private readonly int _maxLength;
        private readonly string[] _strings;
        private readonly int[] _hashes;
        private readonly byte[] _lru; // per set: the way to replace next

        public long Hits, Misses, Bypassed, CharsSaved;

        /// <param name="sets">Number of sets (rounded up to a power of two); capacity is twice that.</param>
        /// <param name="maxLength">Longer strings bypass the cache.</param>
        public Cp1252StringCache(int sets = 1024, int maxLength = 64)
        {
            sets = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, sets));
            _owner = Environment.CurrentManagedThreadId;
            _setMask = sets - 1;
            _maxLength = maxLength;
            _strings = new string[sets * 2];
            _hashes = new int[sets * 2];
            _lru = new byte[sets];
        }

        public int Capacity => _strings.Length;

        public int Count
        {
            get
            {
                int n = 0;

                foreach (string s in _strings)
                {
                    if (s != null)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>The decoded string of <paramref name="bytes"/> (Cp1252.GetString), from the cache when it holds it.</summary>
        public string GetString(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            if (bytes.Length > _maxLength || Environment.CurrentManagedThreadId != _owner)
            {
                Bypassed++;

                return Cp1252.GetString(bytes);
            }

            int hash = Hash(bytes);
            int set = hash & _setMask;
            int way0 = set << 1;

            if (_hashes[way0] == hash && Matches(_strings[way0], bytes))
            {
                _lru[set] = 1;
                Hits++;
                CharsSaved += bytes.Length;

                return _strings[way0];
            }

            if (_hashes[way0 + 1] == hash && Matches(_strings[way0 + 1], bytes))
            {
                _lru[set] = 0;
                Hits++;
                CharsSaved += bytes.Length;

                return _strings[way0 + 1];
            }

            Misses++;
            string s = Cp1252.GetString(bytes);
            int victim = way0 + _lru[set];
            _strings[victim] = s;
            _hashes[victim] = hash;
            _lru[set] ^= 1;

            return s;
        }

        public void Clear()
        {
            Array.Clear(_strings);
            Array.Clear(_hashes);
            Array.Clear(_lru);
        }

        // 8 bytes per step, multiply-rotate mixing: cheaper than HashCode.AddBytes for short keys (a lookup is mostly
        // the hash) and good enough for set selection, since a match is confirmed by comparing the string.
        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            const ulong K = 0x9E3779B97F4A7C15;
            ulong h = (ulong)bytes.Length * K;
            int i = 0;

            for (; i + 8 <= bytes.Length; i += 8)
            {
                h = BitOperations.RotateLeft((h ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i))) * K, 29);
            }

            if (i < bytes.Length)
            {
                ulong tail = 0;

                for (int j = bytes.Length - 1; j >= i; j--)
                {
                    tail = (tail << 8) | bytes[j];
                }

                h = BitOperations.RotateLeft((h ^ tail) * K, 29);
            }

            h ^= h >> 32;

            return (int)(h * K >> 32);
        }

        private static bool Matches(string s, ReadOnlySpan<byte> bytes)
        {
            if (s == null || s.Length != bytes.Length)
            {
                return false;
            }

            if (Ascii.Equals(bytes, s))
            {
                return true;
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                if (s[i] != Cp1252.ToChar(bytes[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
