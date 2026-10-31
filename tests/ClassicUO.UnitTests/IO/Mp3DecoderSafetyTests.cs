// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClassicUO.IO.Audio;
using ClassicUO.UnitTests.Assets;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.IO
{
    // Fixes from the Codex audit of poc/dr-mp3: handle ownership and thread safety, the input leak on a failed native
    // load, EOF published only after the last buffer, the 3-header prefilter, and the synthetic fuzz regressions.
    // Same class as Mp3DecoderTests (partial), so these never run in parallel with the other decoder tests: they use
    // the static OpenMemoryOverride hook and the global LiveInputAllocations count.
    public partial class Mp3DecoderTests
    {
        private static long SettledLiveInputs()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            return Mp3Decoder.LiveInputAllocations;
        }

        [Theory]
        [InlineData("dll")]
        [InlineData("entry")]
        [InlineData("zero")]
        public void FailedNativeOpen_FreesTheInputCopy(string kind)
        {
            byte[] mp3 = SilentStream(20);
            long before = SettledLiveInputs();
            Mp3Decoder.OpenMemoryOverride = (p, n) => kind switch
            {
                "dll" => throw new DllNotFoundException("forced: cuo_mp3 missing"),
                "entry" => throw new EntryPointNotFoundException("forced"),
                _ => 0
            };

            try
            {
                for (int i = 0; i < 50; i++)
                {
                    if (kind == "zero")
                    {
                        Mp3Decoder.TryOpen(mp3, out Mp3Decoder d, out string error).Should().BeFalse();
                        d.Should().BeNull();
                        error.Should().Contain("rejected");
                    }
                    else
                    {
                        Action open = () => Mp3Decoder.TryOpen(new MemoryStream(mp3), out _, out _);
                        open.Should().Throw<Exception>().Which.Message.Should().StartWith("forced");
                    }
                }
            }
            finally
            {
                Mp3Decoder.OpenMemoryOverride = null;
            }

            Mp3Decoder.LiveInputAllocations.Should().Be(before, "every input copy is freed when the native open fails");
        }

        [Fact]
        public void FailedNativeOpen_FailsTheMusicStreamCleanly()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-mp3-dllfail-{Environment.ProcessId}.mp3");
            File.WriteAllBytes(path, SilentStream(20));
            long before = SettledLiveInputs();
            Mp3Decoder.OpenMemoryOverride = (p, n) => throw new DllNotFoundException("forced: cuo_mp3 missing");

            try
            {
                MusicStream s = new MusicStream(path, "dllfail", loop: true);
                s.Pump().Should().BeFalse();
                s.IsFailed.Should().BeTrue();
                s.Error.Should().Contain("forced");
            }
            finally
            {
                Mp3Decoder.OpenMemoryOverride = null;
                File.Delete(path);
            }

            Mp3Decoder.LiveInputAllocations.Should().Be(before);
        }

        [Fact]
        public void Dispose_IsIdempotentAndFreesOnce()
        {
            long before = SettledLiveInputs();
            Mp3Decoder.TryOpen(SilentStream(10), out Mp3Decoder d, out string error).Should().BeTrue(error);
            Mp3Decoder.LiveInputAllocations.Should().Be(before + 1);

            d.Dispose();
            d.Dispose();
            d.IsDisposed.Should().BeTrue();
            Mp3Decoder.LiveInputAllocations.Should().Be(before);

            Action read = () => d.Read(new short[4096]);
            read.Should().Throw<ObjectDisposedException>();
            Action seek = () => d.Seek(0);
            seek.Should().Throw<ObjectDisposedException>();
            Func<long> total = () => d.TotalFrames;
            total.Should().Throw<ObjectDisposedException>();
        }

        // Dispose (from two threads at once) racing a reader that reads and seeks in a loop: the reader must only ever
        // see frames or ObjectDisposedException, never a crash (use after free) or another exception, and the input
        // copy is freed exactly once.
        [Fact]
        public void Dispose_RacingRead_IsSafe()
        {
            byte[] mp3 = File.ReadAllBytes(FixturePath);
            long before = SettledLiveInputs();
            int disposedSeen = 0;

            for (int iter = 0; iter < 200; iter++)
            {
                Mp3Decoder.TryOpen(mp3, out Mp3Decoder d, out string error).Should().BeTrue(error);
                Exception unexpected = null;
                using Barrier start = new Barrier(3);

                Thread reader = new Thread(() =>
                {
                    short[] buf = new short[1152 * 2];
                    start.SignalAndWait();

                    try
                    {
                        while (true)
                        {
                            if (d.Read(buf) == 0)
                            {
                                d.Seek(0);
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        Interlocked.Increment(ref disposedSeen);
                    }
                    catch (Exception ex)
                    {
                        unexpected = ex;
                    }
                });

                Thread disposer = new Thread(() =>
                {
                    start.SignalAndWait();
                    Thread.SpinWait(iter * 50);
                    d.Dispose();
                });

                reader.Start();
                disposer.Start();
                start.SignalAndWait();
                Thread.SpinWait(iter * 50);
                d.Dispose();

                disposer.Join();
                reader.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the reader stops once the decoder is disposed");
                unexpected.Should().BeNull();
                d.IsDisposed.Should().BeTrue();
            }

            disposedSeen.Should().Be(200);
            Mp3Decoder.LiveInputAllocations.Should().Be(before);
        }

        // Controlled interleaving of the worker and the game thread: the hook runs on the "worker" after the final
        // fill and before that buffer is queued; there the "game thread" drains every ready buffer and checks the
        // condition UOMusic.Update stops on. Before the fix, IsEnded was already true there with nothing ready, so the
        // game thread would stop the track and the last buffer (here: the whole tail of the track) was lost.
        [Theory]
        [InlineData(10)]
        [InlineData(3)]
        public void MusicStream_EndIsPublishedAfterTheLastBuffer(int frames)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-mp3-eof-{frames}-{Environment.ProcessId}.mp3");
            File.WriteAllBytes(path, SilentStream(frames));

            try
            {
                MusicStream s = new MusicStream(path, "eof", loop: false);
                long bytesTotal = 0;
                int fills = 0;
                bool stoppedEarly = false;

                void Drain()
                {
                    while (s.TryDequeue(out byte[] b, out int bytes))
                    {
                        bytesTotal += bytes;
                        s.Recycle(b);
                    }
                }

                s.TestAfterFill = stream =>
                {
                    fills++;
                    Drain();

                    if (stream.IsEnded && stream.ReadyCount == 0)
                    {
                        stoppedEarly = true; // UOMusic.Update would Stop() here (nothing pending in FAudio either)
                    }
                };

                for (int i = 0; i < 20 && !s.IsEnded; i++)
                {
                    s.Pump();
                }

                s.IsEnded.Should().BeTrue();
                stoppedEarly.Should().BeFalse("IsEnded must not be visible before the last buffer is queued");
                s.ReadyCount.Should().BeGreaterThan(0, "the last buffer is still waiting for the game thread");
                Drain();
                fills.Should().BeGreaterThan(0);
                bytesTotal.Should().Be((long)frames * FrameSamples * 2 * sizeof(short));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Validator_NeedsThreeConsistentHeaders()
        {
            byte[] three = SilentStream(3);
            Mp3Validator.Validate(three).IsValid.Should().BeTrue("a complete 3-frame stream");
            Mp3Validator.Validate(SilentStream(3).Concat(new byte[100]).ToArray()).IsValid.Should().BeTrue("3 headers, then junk");
            Mp3Validator.Validate(SilentStream(1)).IsValid.Should().BeTrue("one complete frame that ends the data");

            // Two good frames followed by junk: the old two-header check accepted this.
            Mp3Validator.Validate(SilentStream(2).Concat(new byte[100]).ToArray()).IsValid.Should().BeFalse();

            // Third header at another sample rate (48 kHz): inconsistent chain.
            byte[] mixed = SilentStream(5);
            mixed[2 * FrameBytes + 2] = 0x94;
            Mp3Validator.Validate(mixed.AsSpan(0, 3 * FrameBytes + 50).ToArray()).IsValid.Should().BeFalse();

            // Third header of another MPEG version (MPEG-2): inconsistent chain.
            byte[] version = SilentStream(5);
            version[2 * FrameBytes + 1] = 0xF3;
            Mp3Validator.Validate(version.AsSpan(0, 3 * FrameBytes + 50).ToArray()).IsValid.Should().BeFalse();
        }

        // Every stock track must still pass the tightened prefilter (reported as skipped without UO data).
        [FilesFact(MusicDir, "Boattravel.mp3")]
        public void Validator_AcceptsEveryStockTrack()
        {
            string[] files = Directory.GetFiles(MusicDir, "*.mp3");
            List<string> rejected = new List<string>();

            foreach (string f in files)
            {
                Mp3Validator.Result r = Mp3Validator.Validate(File.ReadAllBytes(f));

                if (!r.IsValid)
                {
                    rejected.Add($"{Path.GetFileName(f)}: {r.Error}");
                }
            }

            files.Length.Should().BeGreaterThan(0);
            rejected.Should().BeEmpty();
        }

        // Inputs the fuzzer added to its corpus from synthetic seeds only (the fixture and slices of it), merged to a
        // minimal set (IO/Fixtures/mp3-regressions). Each must open or be rejected cleanly and survive read/seek.
        [Fact]
        public void FuzzRegressions_DecodeOrRejectCleanly()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "IO", "Fixtures", "mp3-regressions");
            string[] files = Directory.GetFiles(dir);
            files.Should().NotBeEmpty();

            foreach (string f in files)
            {
                if (Mp3Decoder.TryOpen(File.ReadAllBytes(f), out Mp3Decoder d, out _))
                {
                    using (d)
                    {
                        long n = ReadAll(d, 1 << 20);
                        d.Seek(0);
                        ReadAll(d, 20000);
                        d.Seek(n / 2);
                        ReadAll(d, 20000);
                        d.Seek(n + 50000);
                        ReadAll(d, 20000);
                        _ = d.TotalFrames;
                    }
                }
            }
        }
    }
}
