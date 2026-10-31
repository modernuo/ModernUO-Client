// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ClassicUO.IO.Audio;
using ClassicUO.UnitTests.Assets;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.IO
{
    // dr_mp3 (native cuo_mp3) behind Mp3Decoder, the managed Mp3Validator guard, and MusicStream's loop fill.
    // Inputs are synthetic MP3s built here (silent Layer III frames, optionally led by a Xing/Info + LAME frame), so no
    // UO data is committed. Tests that also use real tracks from C:\Ultima Online Classic\Music\Digital skip without it.
    public partial class Mp3DecoderTests
    {
        private const string MusicDir = @"C:\Ultima Online Classic\Music\Digital";
        private const int FrameBytes = 417; // MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding bit
        private const int FrameSamples = 1152;

        // A silent stream: frames of header + zero side info/main data (part2_3_length 0) decode to zeros.
        private static byte[] SilentStream(int frames, bool mono = false, int lameDelay = -1, int lamePadding = 0, int xingFrames = -1)
        {
            List<byte> data = new List<byte>();

            if (lameDelay >= 0)
            {
                byte[] info = Frame(mono);
                int at = 4 + (mono ? 17 : 32);
                "Info"u8.ToArray().CopyTo(info, at);
                info[at + 7] = 0x01; // FRAMES flag
                int count = xingFrames >= 0 ? xingFrames : frames;
                info[at + 8] = (byte)(count >> 24);
                info[at + 9] = (byte)(count >> 16);
                info[at + 10] = (byte)(count >> 8);
                info[at + 11] = (byte)count;
                int lame = at + 12;
                "LAME3.100"u8.ToArray().CopyTo(info, lame);
                info[lame + 21] = (byte)(lameDelay >> 4);
                info[lame + 22] = (byte)(((lameDelay & 0xF) << 4) | ((lamePadding >> 8) & 0xF));
                info[lame + 23] = (byte)lamePadding;
                data.AddRange(info);
            }

            for (int i = 0; i < frames; i++)
            {
                data.AddRange(Frame(mono));
            }

            return data.ToArray();
        }

        private static byte[] Frame(bool mono)
        {
            byte[] f = new byte[FrameBytes];
            f[0] = 0xFF;
            f[1] = 0xFB;                       // MPEG-1, Layer III, no CRC
            f[2] = 0x90;                       // 128 kbps, 44.1 kHz, no padding
            f[3] = (byte)(mono ? 0xC4 : 0x44); // mono / joint stereo

            return f;
        }

        private static long ReadAll(Mp3Decoder d, long cap = 1 << 22)
        {
            short[] buf = new short[4096 * d.Channels];
            long total = 0;
            int n;

            while (total < cap && (n = d.Read(buf)) > 0)
            {
                total += n;
            }

            return total;
        }

        [Fact]
        public void NativeVersion_IsPinnedDrMp3()
        {
            Mp3Decoder.NativeVersion.Should().Be("0.7.4");
        }

        [Fact]
        public void SilentStream_DecodesEveryFrame()
        {
            Mp3Decoder.TryOpen(SilentStream(40), out Mp3Decoder d, out string error).Should().BeTrue(error);

            using (d)
            {
                d.SampleRate.Should().Be(44100);
                d.Channels.Should().Be(2);
                d.HasXingFrameCount.Should().BeFalse();
                ReadAll(d).Should().Be(40 * FrameSamples);
                d.TotalFrames.Should().Be(40 * FrameSamples);
            }
        }

        [Fact]
        public void LameTag_DelayAndPaddingAreTrimmedOnce()
        {
            // delay 576 -> skip 576 + 529; padding 1000 -> trim 1000 - 529; the Info frame itself is not audio.
            Mp3Decoder.TryOpen(SilentStream(40, lameDelay: 576, lamePadding: 1000), out Mp3Decoder d, out string error).Should().BeTrue(error);

            using (d)
            {
                long expected = 40L * FrameSamples - (576 + 529) - (1000 - 529);
                d.HasXingFrameCount.Should().BeTrue();
                d.DelayFrames.Should().Be(576 + 529);
                d.PaddingFrames.Should().Be(1000 - 529);
                d.TotalFrames.Should().Be(expected);
                ReadAll(d).Should().Be(expected);

                d.Seek(0).Should().BeTrue();
                ReadAll(d).Should().Be(expected, "seeking to 0 must skip the delay again");
            }
        }

        [Fact]
        public void MonoStream_ReportsOneChannel()
        {
            Mp3Decoder.TryOpen(SilentStream(20, mono: true), out Mp3Decoder d, out string error).Should().BeTrue(error);

            using (d)
            {
                d.Channels.Should().Be(1);
                ReadAll(d).Should().Be(20 * FrameSamples);
            }
        }

        [Theory]
        [InlineData("empty")]
        [InlineData("garbage")]
        [InlineData("id3-past-end")]
        [InlineData("layer1")]
        [InlineData("reserved-rate")]
        [InlineData("bad-bitrate")]
        [InlineData("lone-header")]
        public void Validator_RejectsBadInput(string kind)
        {
            byte[] data = kind switch
            {
                "empty" => Array.Empty<byte>(),
                "garbage" => Enumerable.Range(0, 5000).Select(i => (byte)(i * 37 + 11)).ToArray(),
                "id3-past-end" => new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F }.Concat(SilentStream(3)).ToArray(),
                "layer1" => SilentStream(5).Select((b, i) => i % FrameBytes == 1 ? (byte)0xFF : b).ToArray(),
                "reserved-rate" => SilentStream(5).Select((b, i) => i % FrameBytes == 2 ? (byte)0x9C : b).ToArray(),
                "bad-bitrate" => SilentStream(5).Select((b, i) => i % FrameBytes == 2 ? (byte)0xF0 : b).ToArray(),
                _ => Frame(false).Concat(new byte[100]).ToArray() // a header whose "next frame" is zeros
            };

            Mp3Validator.Validate(data).IsValid.Should().BeFalse();
            Mp3Decoder.TryOpen(data, out Mp3Decoder d, out string error).Should().BeFalse();
            d.Should().BeNull();
            error.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void Validator_RejectsOversizedInput()
        {
            byte[] big = new byte[Mp3Validator.MaxBytes + 1];
            SilentStream(4).CopyTo(big, 0);

            Mp3Validator.Validate(big).IsValid.Should().BeFalse();
            Mp3Decoder.TryOpen(new MemoryStream(big), out _, out string error).Should().BeFalse();
            error.Should().Contain("exceeds");
        }

        [Fact]
        public void Validator_SkipsId3v2Tag()
        {
            byte[] tag = { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 20 };
            byte[] data = tag.Concat(new byte[20]).Concat(SilentStream(5)).ToArray();

            Mp3Validator.Result r = Mp3Validator.Validate(data);
            r.IsValid.Should().BeTrue(r.Error);
            r.FirstFrameOffset.Should().Be(30);
            r.SampleRate.Should().Be(44100);
            r.Channels.Should().Be(2);
        }

        // The regression corpus: deterministic mutations of synthetic streams (and, when the UO folder exists, the
        // start of every real track) run through the native decoder. Passing means no crash and no managed exception
        // other than a clean rejection. The fuzzer (src/native/cuo_mp3/fuzz) explores the same surface under ASan.
        [Fact]
        public void MutatedInputs_DecodeOrRejectCleanly()
        {
            List<byte[]> seeds = new List<byte[]>
            {
                SilentStream(30),
                SilentStream(30, mono: true),
                SilentStream(30, lameDelay: 576, lamePadding: 1200),
                SilentStream(30, lameDelay: 0xFFF, lamePadding: 0xFFF),
                SilentStream(3, lameDelay: 576, lamePadding: 0, xingFrames: int.MaxValue),
                SilentStream(3, lameDelay: 576, lamePadding: 0, xingFrames: 0),
            };

            if (Directory.Exists(MusicDir))
            {
                foreach (string f in Directory.GetFiles(MusicDir, "*.mp3").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(12))
                {
                    using FileStream fs = File.OpenRead(f);
                    byte[] head = new byte[Math.Min(48 * 1024, (int)fs.Length)];
                    fs.ReadExactly(head);
                    seeds.Add(head);
                }
            }

            Random rng = new Random(20261031);
            int opened = 0, rejected = 0;

            foreach (byte[] seed in seeds)
            {
                for (int m = 0; m < 60; m++)
                {
                    byte[] data = Mutate(seed, rng);

                    if (Mp3Decoder.TryOpen(data, out Mp3Decoder d, out _))
                    {
                        using (d)
                        {
                            d.Channels.Should().BeInRange(1, 2);
                            d.SampleRate.Should().BeInRange(8000, 48000);
                            long n = ReadAll(d, 1 << 20);
                            d.Seek(0);
                            ReadAll(d, 20000);
                            d.Seek(n / 2);
                            ReadAll(d, 20000);
                            d.Seek(n + 50000);
                            ReadAll(d, 20000);
                            _ = d.TotalFrames;
                        }

                        opened++;
                    }
                    else
                    {
                        rejected++;
                    }
                }
            }

            (opened + rejected).Should().Be(seeds.Count * 60);
            opened.Should().BeGreaterThan(0, "most single mutations keep a decodable stream");
        }

        private static byte[] Mutate(byte[] seed, Random rng)
        {
            byte[] d = (byte[])seed.Clone();

            switch (rng.Next(7))
            {
                case 0: // bit flips
                    for (int i = 0, n = 1 + rng.Next(16); i < n; i++)
                    {
                        d[rng.Next(d.Length)] ^= (byte)(1 << rng.Next(8));
                    }

                    return d;
                case 1: // truncate
                    return d.AsSpan(0, rng.Next(1, d.Length)).ToArray();
                case 2: // a run of 0xFF (fake syncs) or zeros
                {
                    int at = rng.Next(d.Length), len = Math.Min(d.Length - at, 1 + rng.Next(64));
                    d.AsSpan(at, len).Fill(rng.Next(2) == 0 ? (byte)0xFF : (byte)0);

                    return d;
                }
                case 3: // duplicate a chunk into another place
                {
                    int len = Math.Min(d.Length / 2, 1 + rng.Next(2048));
                    int from = rng.Next(d.Length - len + 1), to = rng.Next(d.Length - len + 1);
                    d.AsSpan(from, len).ToArray().CopyTo(d, to);

                    return d;
                }
                case 4: // corrupt the first 200 bytes (ID3, Xing/Info/LAME fields)
                    for (int i = 0, n = 1 + rng.Next(8); i < n; i++)
                    {
                        d[rng.Next(Math.Min(200, d.Length))] = (byte)rng.Next(256);
                    }

                    return d;
                case 5: // random header fields on every frame start that still syncs
                    for (int i = 0; i + 3 < d.Length; i++)
                    {
                        if (d[i] == 0xFF && (d[i + 1] & 0xE0) == 0xE0 && rng.Next(4) == 0)
                        {
                            d[i + 2] = (byte)rng.Next(256);
                            d[i + 3] = (byte)rng.Next(256);
                        }
                    }

                    return d;
                default: // splice random bytes in the middle
                {
                    byte[] junk = new byte[1 + rng.Next(512)];
                    rng.NextBytes(junk);
                    int at = rng.Next(d.Length);

                    return d.AsSpan(0, at).ToArray().Concat(junk).Concat(d.AsSpan(at).ToArray()).ToArray();
                }
            }
        }

        [Fact]
        public void MusicStream_LoopFillsEveryBufferAcrossTheSeam()
        {
            // 7 frames of 1152 do not divide 8192, so a seam falls inside buffers; the old code returned short buffers
            // there and dropped the wrapped samples.
            string path = Path.Combine(Path.GetTempPath(), $"cuo-mp3-loop-{Environment.ProcessId}.mp3");
            File.WriteAllBytes(path, SilentStream(7, lameDelay: 576, lamePadding: 700));

            try
            {
                MusicStream s = new MusicStream(path, "loop", loop: true);
                List<int> sizes = new List<int>();

                for (int i = 0; i < 20; i++)
                {
                    s.Pump().Should().BeTrue();

                    while (s.TryDequeue(out byte[] b, out int bytes))
                    {
                        sizes.Add(bytes);
                        s.Recycle(b);
                    }
                }

                s.Channels.Should().Be(2);
                sizes.Should().HaveCountGreaterThan(10);
                sizes.Should().OnlyContain(b => b == MusicStream.FramesPerBuffer * 2 * sizeof(short));
                s.Cancel();
                s.Pump().Should().BeFalse();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void MusicStream_NonLoopingTrackEndsWithItsLastSamples()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-mp3-once-{Environment.ProcessId}.mp3");
            File.WriteAllBytes(path, SilentStream(10));

            try
            {
                MusicStream s = new MusicStream(path, "once", loop: false);
                long bytesTotal = 0;

                for (int i = 0; i < 10 && !s.IsEnded; i++)
                {
                    s.Pump();

                    while (s.TryDequeue(out byte[] b, out int bytes))
                    {
                        bytesTotal += bytes;
                        s.Recycle(b);
                    }
                }

                s.IsEnded.Should().BeTrue();
                bytesTotal.Should().Be(10L * FrameSamples * 2 * sizeof(short));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void MusicStream_BadFileFailsWithoutThrowing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-mp3-bad-{Environment.ProcessId}.mp3");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 10000).ToArray());

            try
            {
                MusicStream s = new MusicStream(path, "bad", loop: true);
                s.Pump().Should().BeFalse();
                s.IsFailed.Should().BeTrue();
                s.Error.Should().NotBeNullOrEmpty();

                MusicStream missing = new MusicStream(path + ".missing", "missing", loop: true);
                missing.Pump().Should().BeFalse();
                missing.IsFailed.Should().BeTrue();
            }
            finally
            {
                File.Delete(path);
            }
        }

        // Real track (reported as skipped without UO data): looping must continue with exactly the track's first
        // samples, i.e. a gapless seam: the PCM after the wrap equals the PCM at the start.
        [FilesFact(MusicDir, "Boattravel.mp3")]
        public void RealTrack_LoopSeamIsSampleExact()
        {
            AssertLoopSeamIsSampleExact(Path.Combine(MusicDir, "Boattravel.mp3"), "Boattravel");
        }

        // The committed synthetic fixture (IO/Fixtures/seam-noise.mp3, made by make_mp3_fixture.py): non-silent noise
        // that differs per frame, behind an Info + LAME frame, so a wrap at the wrong sample can't go unnoticed.
        [Fact]
        public void Fixture_LoopSeamIsSampleExact()
        {
            Mp3Decoder.TryOpenFile(FixturePath, out Mp3Decoder d, out string error).Should().BeTrue(error);

            using (d)
            {
                d.Channels.Should().Be(2);
                d.DelayFrames.Should().Be(576 + 529);
                short[] pcm = new short[d.TotalFrames * 2];
                d.Read(pcm).Should().Be((int)d.TotalFrames);
                pcm.Max(v => Math.Abs((int)v)).Should().BeGreaterThan(1000, "the fixture is not silent");
                pcm.AsSpan(0, FrameSamples * 2).SequenceEqual(pcm.AsSpan(FrameSamples * 2, FrameSamples * 2)).Should().BeFalse("frames differ");
                (d.TotalFrames % MusicStream.FramesPerBuffer).Should().NotBe(0, "the seam must fall inside a buffer");
            }

            AssertLoopSeamIsSampleExact(FixturePath, "fixture");
        }

        private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "IO", "Fixtures", "seam-noise.mp3");

        private static void AssertLoopSeamIsSampleExact(string path, string name)
        {
            Mp3Decoder.TryOpenFile(path, out Mp3Decoder d, out string error).Should().BeTrue(error);
            short[] once;

            using (d)
            {
                once = new short[d.TotalFrames * d.Channels];
                int got = 0, n;

                while ((n = d.Read(once.AsSpan(got))) > 0)
                {
                    got += n * d.Channels;
                }

                got.Should().Be(once.Length);
            }

            MusicStream s = new MusicStream(path, name, loop: true);
            List<short> played = new List<short>(once.Length + 100000);

            while (played.Count < once.Length + 50000)
            {
                s.Pump().Should().BeTrue();

                while (s.TryDequeue(out byte[] b, out int bytes))
                {
                    played.AddRange(MemoryMarshal.Cast<byte, short>(b.AsSpan(0, bytes)).ToArray());
                    s.Recycle(b);
                }
            }

            s.Cancel();
            s.Pump();

            played.Take(once.Length).SequenceEqual(once).Should().BeTrue("the first lap is the whole track");
            played.Skip(once.Length).Take(40000).SequenceEqual(once.Take(40000)).Should().BeTrue("after the seam the track starts again");
        }
    }
}
