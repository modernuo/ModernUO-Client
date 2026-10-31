// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    ///     Managed pre-check for MP3 data before it reaches the native decoder (packs and loose files are untrusted).
    ///     Accepts only: a size within <see cref="MaxBytes" />, an optional ID3v2 tag that fits the data, then a valid
    ///     MPEG-1/2/2.5 Layer III frame header within <see cref="MaxSyncSearch" /> bytes whose next frame header is also
    ///     valid and consistent (or which ends exactly at the end of the data), with a sane rate and channel count.
    /// </summary>
    public static class Mp3Validator
    {
        /// <summary>Largest MP3 accepted. The biggest stock track (elf_city_1.mp3) is 12.9 MB.</summary>
        public const int MaxBytes = 32 * 1024 * 1024;

        /// <summary>How far past the ID3v2 tag the first frame may start.</summary>
        public const int MaxSyncSearch = 64 * 1024;

        public readonly struct Result
        {
            public Result(string error)
            {
                Error = error;
                FirstFrameOffset = -1;
                SampleRate = 0;
                Channels = 0;
            }

            public Result(int offset, int sampleRate, int channels)
            {
                Error = null;
                FirstFrameOffset = offset;
                SampleRate = sampleRate;
                Channels = channels;
            }

            public bool IsValid => Error == null;
            public string Error { get; }
            public int FirstFrameOffset { get; }
            public int SampleRate { get; }
            public int Channels { get; }
        }

        private static readonly int[] s_BitrateV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] s_BitrateV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        private static readonly int[] s_RateV1 = { 44100, 48000, 32000, 0 };

        public static Result Validate(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return new Result("empty");
            }

            if (data.Length > MaxBytes)
            {
                return new Result($"size {data.Length} exceeds {MaxBytes}");
            }

            int start = 0;

            if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            {
                if ((data[6] | data[7] | data[8] | data[9]) >= 0x80)
                {
                    return new Result("bad ID3v2 size");
                }

                long tagSize = 10L + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9]);

                if ((data[5] & 0x10) != 0)
                {
                    tagSize += 10;
                }

                if (tagSize >= data.Length)
                {
                    return new Result("ID3v2 tag runs past the end");
                }

                start = (int)tagSize;
            }

            int end = Math.Min(data.Length - 4, start + MaxSyncSearch);

            for (int i = start; i <= end; i++)
            {
                if (!TryParseHeader(data.Slice(i), out FrameHeader h))
                {
                    continue;
                }

                int next = i + h.Length;

                if (next == data.Length)
                {
                    return new Result(i, h.SampleRate, h.Channels);
                }

                if (next + 4 <= data.Length && TryParseHeader(data.Slice(next), out FrameHeader h2) &&
                    h2.Version == h.Version && h2.SampleRate == h.SampleRate)
                {
                    return new Result(i, h.SampleRate, h.Channels);
                }
            }

            return new Result("no valid Layer III frame header");
        }

        private struct FrameHeader
        {
            public int Version; // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
            public int SampleRate;
            public int Channels;
            public int Length;
        }

        private static bool TryParseHeader(ReadOnlySpan<byte> p, out FrameHeader h)
        {
            h = default;

            if (p.Length < 4 || p[0] != 0xFF || (p[1] & 0xE0) != 0xE0)
            {
                return false;
            }

            int version = (p[1] >> 3) & 3;
            int layer = (p[1] >> 1) & 3;
            int bitrateIndex = p[2] >> 4;
            int rateIndex = (p[2] >> 2) & 3;
            int padding = (p[2] >> 1) & 1;

            if (version == 1 || layer != 1 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3)
            {
                return false; // reserved version, not Layer III, free-format/bad bitrate, reserved rate
            }

            int rate = s_RateV1[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int kbps = (version == 3 ? s_BitrateV1 : s_BitrateV2)[bitrateIndex];
            int length = (version == 3 ? 144 : 72) * kbps * 1000 / rate + padding;

            if (rate < 8000 || rate > 48000 || length < 4)
            {
                return false;
            }

            h.Version = version;
            h.SampleRate = rate;
            h.Channels = (p[3] >> 6) == 3 ? 1 : 2;
            h.Length = length;

            return true;
        }
    }
}
