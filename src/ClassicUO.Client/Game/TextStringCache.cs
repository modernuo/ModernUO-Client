// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// text_string_cache: server Cp1252 strings (names, labels, system lines) are decoded through a bounded cache keyed by
    /// their bytes (<see cref="Cp1252StringCache"/>, 1024 sets x 2 ways, strings up to 64 bytes), so a repeated string
    /// allocates nothing. Read once at startup on the game thread; other threads decode without it. In developer builds
    /// CUO_TEXT_STRING_CACHE=1|0 overrides the setting and CUO_TEXT_CACHE_CHECK=1 compares every cached result with a fresh
    /// decode and logs a summary (hit rate, bytes saved, length histogram) every 10 s.
    /// </summary>
    internal static class TextStringCache
    {
        public const int Sets = 1024;
        public const int MaxLength = 64;

        /// <summary>Tests: the switch (null: the setting and the override).</summary>
        internal static bool? OnOverride;

        public static bool On
        {
            get
            {
                if (OnOverride.HasValue)
                {
                    return OnOverride.Value;
                }

#if CUO_DEVTOOLS
                string env = Environment.GetEnvironmentVariable("CUO_TEXT_STRING_CACHE");

                if (env == "1" || env == "0")
                {
                    return env == "1";
                }
#endif
                return Settings.GlobalSettings != null && Settings.GlobalSettings.TextStringCache;
            }
        }

        public static void Enable()
        {
            StringHelper.Cp1252Cache = On ? new Cp1252StringCache(Sets, MaxLength) : null;
            StringHelper.Cp1252CacheCheck = null;

#if CUO_DEVTOOLS
            if (Environment.GetEnvironmentVariable("CUO_TEXT_CACHE_CHECK") == "1")
            {
                StringHelper.Cp1252CacheCheck = Check;
                _nextSummary = Environment.TickCount64 + 10_000;
            }
#endif
            Log.Info($"[text-cache] text_string_cache {(StringHelper.Cp1252Cache != null ? "on" : "off")}");
        }

#if CUO_DEVTOOLS
        // Length buckets: 1-8, 9-16, 17-32, 33-64, 65-128, 129-256, >256.
        private static readonly long[] _lengths = new long[7];
        private static long _checked, _mismatches, _withC1, _nextSummary;

        private static void Check(ReadOnlySpan<byte> bytes, string result)
        {
            string fresh = Cp1252.GetString(bytes);
            _checked++;

            if (!string.Equals(fresh, result, StringComparison.Ordinal))
            {
                _mismatches++;

                if (_mismatches <= 20)
                {
                    Log.Warn($"[text-cache-check] mismatch: {bytes.Length} bytes, cached \"{result}\", decoded \"{fresh}\"");
                }
            }

            int n = bytes.Length;
            _lengths[n <= 8 ? 0 : n <= 16 ? 1 : n <= 32 ? 2 : n <= 64 ? 3 : n <= 128 ? 4 : n <= 256 ? 5 : 6]++;

            if (bytes.IndexOfAnyInRange((byte)0x80, (byte)0x9F) >= 0)
            {
                _withC1++;
            }

            if (Environment.TickCount64 >= _nextSummary)
            {
                _nextSummary = Environment.TickCount64 + 10_000;
                Log.Info(Summary());
            }
        }
#endif

        public static string Summary()
        {
            Cp1252StringCache c = StringHelper.Cp1252Cache;

            if (c == null)
            {
                return "[text-cache] text_string_cache off";
            }

            long lookups = c.Hits + c.Misses;
            double rate = lookups == 0 ? 0 : 100.0 * c.Hits / lookups;
            // A hit saves the string a decode would allocate: 22 bytes of header and terminator plus 2 per char, 8-aligned.
            long savedBytes = c.Hits * 24 + c.CharsSaved * 2;

            return $"[text-cache] text_string_cache on: hits {c.Hits}, misses {c.Misses} ({rate:F1} % hit), bypassed {c.Bypassed}, " +
                   $"~{savedBytes / 1024} KiB not allocated, {c.Count}/{c.Capacity} entries" +
#if CUO_DEVTOOLS
                   $"; check: {_checked} compared, {_mismatches} mismatches, {_withC1} with 0x80-0x9F, lengths " +
                   $"1-8 {_lengths[0]} 9-16 {_lengths[1]} 17-32 {_lengths[2]} 33-64 {_lengths[3]} 65-128 {_lengths[4]} " +
                   $"129-256 {_lengths[5]} >256 {_lengths[6]}";
#else
                   "";
#endif
        }
    }
}
