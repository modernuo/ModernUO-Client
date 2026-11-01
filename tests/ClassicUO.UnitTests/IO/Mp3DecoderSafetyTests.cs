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
        [InlineData("image")]
        [InlineData("zero")]
        public void FailedNativeOpen_FreesTheInputCopy(string kind)
        {
            byte[] mp3 = SilentStream(20);
            long before = SettledLiveInputs();
            Mp3Decoder.OpenMemoryOverride = (p, n) => kind switch
            {
                "dll" => throw new DllNotFoundException("forced: cuo_mp3 missing"),
                "entry" => throw new EntryPointNotFoundException("forced"),
                "image" => throw new BadImageFormatException("forced: wrong architecture"),
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

        // Codex round 2: a frame count read (and cached) before Dispose must not be readable after it.
        [Fact]
        public void TotalFrames_Cached_ThrowsAfterDispose()
        {
            Mp3Decoder.TryOpen(SilentStream(10), out Mp3Decoder d, out string error).Should().BeTrue(error);
            d.TotalFrames.Should().BeGreaterThan(0);
            d.Dispose();

            Func<long> total = () => d.TotalFrames;
            total.Should().Throw<ObjectDisposedException>();
        }

        // Codex round 2: a library that exports open but not close. The release entry point is resolved before any
        // native allocation, so every open path fails with an error, without reaching open and without an input copy.
        [Theory]
        [InlineData("entry")]
        [InlineData("dll")]
        [InlineData("image")]
        public void MissingCloseExport_FailsTheOpenBeforeAllocating(string kind)
        {
            byte[] mp3 = SilentStream(20);
            long before = SettledLiveInputs();
            int opens = 0;
            Mp3Decoder.OpenMemoryOverride = (p, n) =>
            {
                Interlocked.Increment(ref opens);

                return 0;
            };
            Mp3Decoder.CloseOverride = h => throw (kind switch
            {
                "dll" => new DllNotFoundException("forced: no close"),
                "image" => new BadImageFormatException("forced: no close"),
                _ => (Exception)new EntryPointNotFoundException("forced: no close")
            });

            try
            {
                Mp3Decoder.TryOpen(mp3, out Mp3Decoder a, out string e1).Should().BeFalse();
                a.Should().BeNull();
                e1.Should().Contain("release entry point");

                Mp3Decoder.TryOpen(new MemoryStream(mp3), out Mp3Decoder b, out string e2).Should().BeFalse();
                b.Should().BeNull();
                e2.Should().Contain("release entry point");

                Mp3Decoder.TryOpen(new NonSeekableStream(mp3), out Mp3Decoder c, out string e3).Should().BeFalse();
                c.Should().BeNull();
                e3.Should().Contain("release entry point");

                Mp3Decoder.LiveInputAllocations.Should().Be(before, "nothing is allocated before close is resolved");
            }
            finally
            {
                Mp3Decoder.OpenMemoryOverride = null;
                Mp3Decoder.CloseOverride = null;
            }

            opens.Should().Be(0, "the native open is never reached");
        }

        // Codex round 2: release never throws. A close that fails at Dispose or in the finaliser is counted, the input
        // copy is still freed, and nothing escapes (an exception escaping a finaliser would kill the test host).
        [Fact]
        public void FailingCloseAtRelease_NeverThrowsAndFreesTheInput()
        {
            long before = SettledLiveInputs();
            long failures = Mp3Decoder.CloseFailures;

            // Closes the real decoder (no native leak), then fails as a broken export would.
            Action<nint> failingClose = h =>
            {
                if (h != 0)
                {
                    Mp3Native.Close(h);
                }

                throw new EntryPointNotFoundException("forced: close failed");
            };

            Mp3Decoder.TryOpen(SilentStream(10), out Mp3Decoder d, out string error).Should().BeTrue(error);
            Mp3Decoder.CloseOverride = failingClose;

            try
            {
                d.Invoking(x => x.Dispose()).Should().NotThrow();
                d.IsDisposed.Should().BeTrue();
                Mp3Decoder.LiveInputAllocations.Should().Be(before);
                Mp3Decoder.CloseFailures.Should().Be(failures + 1);
            }
            finally
            {
                Mp3Decoder.CloseOverride = null;
            }

            // Finaliser path: open with a working close, then drop the decoder with a failing one installed.
            OpenAndDrop();
            Mp3Decoder.LiveInputAllocations.Should().Be(before + 1);
            Mp3Decoder.CloseOverride = failingClose;

            try
            {
                SettledLiveInputs().Should().Be(before, "the finaliser frees the input even when close fails");
                Mp3Decoder.CloseFailures.Should().BeGreaterThanOrEqualTo(failures + 2);
            }
            finally
            {
                Mp3Decoder.CloseOverride = null;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpenAndDrop()
        {
            Mp3Decoder.TryOpen(SilentStream(10), out Mp3Decoder d, out string error).Should().BeTrue(error);
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            public NonSeekableStream(byte[] data) : base(data)
            {
            }

            public override bool CanSeek => false;
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

                disposer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the disposer thread finishes");
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
