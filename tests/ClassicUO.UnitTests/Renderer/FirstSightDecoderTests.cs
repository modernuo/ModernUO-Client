using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // First-sight art on worker threads (FirstSightDecoder, used by Art and Texmap for the chunk loader's requests): each
    // request decodes once, uploads happen only in Drain (the main thread), within its deadline, and a failed decode still
    // reaches the upload callback (which then decodes synchronously).
    public class FirstSightDecoderTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        private static bool FillPattern(FirstSightDecoder.Job job)
        {
            job.Width = 4;
            job.Height = 3;
            job.Pixels = ArrayPool<uint>.Shared.Rent(12);

            for (int i = 0; i < 12; i++)
            {
                job.Pixels[i] = job.Index * 100 + (uint)i;
            }

            return true;
        }

        private static void WaitReady(FirstSightDecoder d, int count, Func<int> uploaded) =>
            DecodeWait.Until(d, () => uploaded() >= count, drain: true);

        // the thread pool replaced by a list the test runs by hand, on its own thread: no scheduling involved
        private sealed class ManualStarter
        {
            private readonly List<IThreadPoolWorkItem> _started = new List<IThreadPoolWorkItem>();
            private int _run;

            public int Count => _started.Count;

            public void Start(IThreadPoolWorkItem item) => _started.Add(item);

            public void RunAll()
            {
                while (_run < _started.Count)
                {
                    _started[_run++].Execute();
                }
            }
        }

        [Fact]
        public void Each_Request_Decodes_Once_And_Uploads_Only_In_Drain()
        {
            var uploads = new List<uint>();
            int decodes = 0;
            int mainThread = Environment.CurrentManagedThreadId;
            using var workers = new DecodeTestWorkers(4);
            FirstSightDecoder d = null;
            d = new FirstSightDecoder("test", 64, job =>
            {
                Interlocked.Increment(ref decodes);

                return FillPattern(job);
            }, job =>
            {
                Environment.CurrentManagedThreadId.Should().Be(mainThread);
                job.Pixels.Should().NotBeNull();

                for (int i = 0; i < 12; i++)
                {
                    job.Pixels[i].Should().Be(job.Index * 100 + (uint)i);
                }

                uploads.Add(job.Index);
            }, budget: workers.Budget());

            for (int round = 0; round < 3; round++)
            {
                for (uint i = 0; i < 40; i++)
                {
                    d.Request(i);
                }
            }

            d.Pending.Should().Be(40);
            d.IsRequested(7).Should().BeTrue();
            uploads.Should().BeEmpty("nothing uploads outside Drain");

            WaitReady(d, 40, () => uploads.Count);

            uploads.Should().BeEquivalentTo(Enumerable.Range(0, 40).Select(i => (uint)i));
            decodes.Should().Be(40);
            d.Pending.Should().Be(0);
            d.Completed.Should().Be(40);
            d.IsRequested(7).Should().BeFalse();

            // a sprite can be requested again after its upload (e.g. after an atlas reload)
            d.Request(7);
            WaitReady(d, 41, () => uploads.Count);
            uploads.Count(i => i == 7).Should().Be(2);
            d.Request(64); // out of range: ignored
            d.Pending.Should().Be(0);
        }

        [Fact]
        public void Drain_Past_Its_Deadline_Uploads_One()
        {
            // the decodes run by hand, so all 8 are ready before the first drain whatever the machine does; the deadline
            // (0) has passed for every drain below
            int uploads = 0;
            var starter = new ManualStarter();
            var d = new FirstSightDecoder("test", 16, FillPattern, _ => uploads++, budget: new DecodeBudget(0, starter.Start));

            for (uint i = 0; i < 8; i++)
            {
                d.Request(i);
            }

            starter.RunAll();
            d.ReadyCount.Should().Be(8);

            // each drain whose deadline has passed still uploads exactly one, so the queue always moves
            for (int i = 1; i <= 8; i++)
            {
                d.Drain(0);
                uploads.Should().Be(i);
            }

            d.Drain(0);
            uploads.Should().Be(8);
            d.Pending.Should().Be(0);
        }

        [Fact]
        public void A_Failed_Speculative_Decode_Is_Terminal_And_Never_Uploads_Or_Throws()
        {
            var seen = new List<uint>();
            int decodes = 0;
            var starter = new ManualStarter();
            var d = new FirstSightDecoder("test", 8, job =>
            {
                Interlocked.Increment(ref decodes);

                if (job.Index == 3)
                {
                    job.Pixels = ArrayPool<uint>.Shared.Rent(4); // rented before the failure: returned by the decoder
                    throw new InvalidDataException("corrupt page");
                }

                return job.Index != 5 && FillPattern(job); // 5: nothing to upload
            }, job =>
            {
                job.Pixels.Should().NotBeNull("only decoded sprites reach the upload");
                seen.Add(job.Index);
            }, budget: new DecodeBudget(0, starter.Start));

            d.Request(2);
            d.Request(3);
            d.Request(5);
            starter.RunAll(); // the decode's exception stays in the worker
            d.ReadyCount.Should().Be(3);

            for (int i = 0; i < 3; i++)
            {
                d.Drain(long.MaxValue); // no exception escapes Drain
            }

            d.Pending.Should().Be(0);

            seen.Should().Equal(2u);
            d.IsFailed(3).Should().BeTrue();
            d.IsFailed(5).Should().BeTrue();
            d.IsFailed(2).Should().BeFalse();

            // failed sprites are never requested again (readiness accepts them; a real lookup handles them as before)
            d.Request(3);
            d.Request(5);
            d.Pending.Should().Be(0);
            starter.Count.Should().Be(3, "nothing was started again");
            starter.RunAll();
            decodes.Should().Be(3);
        }

        [Fact]
        public void Cancel_Drops_Finished_And_Running_Decodes_Without_Uploading()
        {
            int uploads = 0;
            using var gate = new ManualResetEventSlim(false);
            using var workers = new DecodeTestWorkers(4); // two block on the gate, the others decode 0..7
            var d = new FirstSightDecoder("test", 16, job =>
            {
                if (job.Index >= 8)
                {
                    gate.Wait(); // still decoding when the scene goes
                }

                return FillPattern(job);
            }, _ => uploads++, budget: workers.Budget());

            for (uint i = 0; i < 10; i++)
            {
                d.Request(i);
            }

            DecodeWait.Until(d, () => d.ReadyCount == 8, drain: false);

            // logout: finished decodes are dropped (buffers returned) without an upload
            d.Cancel();
            d.ReadyCount.Should().Be(0);
            d.Pending.Should().Be(2);
            d.IsRequested(3).Should().BeFalse("a cancelled sprite may be requested again");

            // the two running workers finish after the cancel: they return their buffers themselves, and the next drain
            // only clears their state
            gate.Set();
            DecodeWait.Until(d, () => d.ReadyCount == 2, drain: false);
            d.Drain(long.MaxValue);
            d.Drain(long.MaxValue);
            uploads.Should().Be(0);
            d.Pending.Should().Be(0);
            d.IsRequested(9).Should().BeFalse();
            d.IsFailed(9).Should().BeFalse();

            // and the decoder works normally afterwards
            d.Request(3);
            DecodeWait.Until(d, () => uploads == 1, drain: true);
        }

        [Fact]
        public void Cancel_At_The_Publish_Boundary_Leaves_No_Buffer_Queued()
        {
            // the audit's logout race: a worker past its epoch check but not yet queued, while Cancel bumps the epoch and
            // drains. The check and the publish are one step against Cancel, so either Cancel drains the published job
            // (returning its buffer) or the worker sees the new epoch and returns the buffer itself
            int uploads = 0;
            using var atBoundary = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var workers = new DecodeTestWorkers(1);
            var d = new FirstSightDecoder("test", 4, FillPattern, _ => uploads++, budget: workers.Budget());
            d.BeforePublishForTests = () =>
            {
                atBoundary.Set();
                release.Wait();
            };

            d.Request(1);
            atBoundary.Wait(DecodeWait.HangGuard).Should().BeTrue();

            // logout while the worker is publishing, on a thread of its own (a pool task may not start in time under load)
            using var cancelDone = new ManualResetEventSlim(false);
            var cancel = new Thread(() =>
            {
                d.Cancel();
                cancelDone.Set();
            }) { IsBackground = true };
            cancel.Start();

            // Cancel cannot finish while the worker holds the publish step, whether its thread has got there yet or not; a
            // pause cut short by the scheduler only weakens this check, it cannot fail it
            cancelDone.Wait(50).Should().BeFalse("Cancel waits for the publish step");
            release.Set();
            cancelDone.Wait(DecodeWait.HangGuard).Should().BeTrue();

            d.ReadyCount.Should().Be(0, "the published job was drained, buffer returned, by Cancel");
            d.Pending.Should().Be(0);
            d.IsRequested(1).Should().BeFalse();

            d.BeforePublishForTests = null;
            d.Request(1);
            DecodeWait.Until(d, () => uploads == 1, drain: true);
        }

        [Fact]
        public void Worker_Art_Decodes_Match_The_Main_Thread_Decode()
        {
            // the worker path is ArtLoader.GetArt (PackSprites.DecodeColor into a pooled buffer) plus VisibleBounds, on
            // several threads at once against a small chunk cache (constant eviction), compared with a sequential decode
            if (!File.Exists(Path.Combine(PackDir, "manifest.json")))
            {
                return;
            }

            var sprites = SpriteTable.Read(Path.Combine(PackDir, "art", "sprites.bin"));
            var ids = new List<int>();

            for (int i = 0; i < sprites.Length && ids.Count < 1500; i += 31)
            {
                if (sprites[i].OrigW != 0 && sprites[i].OrigH != 0)
                {
                    ids.Add(i);
                }
            }

            var reference = new PackPages(Path.Combine(PackDir, "art"), 256 << 20);
            var expected = ids.Select(i => PackSprites.DecodeColor(reference, in sprites[i])).ToArray();

            var shared = new PackPages(Path.Combine(PackDir, "art"), 3 << 20);
            var results = new (uint[] Pixels, (int, int, int, int) Bounds)[ids.Count];
            int done = 0; // uploads (main thread)
            using var workers = new DecodeTestWorkers(4);
            var d = new FirstSightDecoder("art", sprites.Length, job =>
            {
                ref readonly var e = ref sprites[job.Index];
                job.Pixels = ArrayPool<uint>.Shared.Rent(e.OrigW * e.OrigH);
                PackSprites.DecodeColor(shared, in e, job.Pixels);
                job.Width = e.OrigW;
                job.Height = e.OrigH;
                var t = PackSprites.Trim(in e);
                PixelPicker.VisibleBounds(job.Pixels.AsSpan(0, e.OrigW * e.OrigH), e.OrigW, e.OrigH, t.X, t.Y, t.W, t.H, out int a, out int b, out int c, out int dd);
                job.Bounds = new Microsoft.Xna.Framework.Rectangle(a, b, c - a, dd - b);

                return true;
            }, job =>
            {
                int k = ids.IndexOf((int)job.Index);
                results[k] = (job.Pixels.AsSpan(0, job.Width * job.Height).ToArray(), (job.Bounds.X, job.Bounds.Y, job.Bounds.Width, job.Bounds.Height));
                done++;
            }, budget: workers.Budget());

            foreach (int i in ids)
            {
                d.Request((uint)i);
            }

            WaitReady(d, ids.Count, () => done);
            done.Should().Be(ids.Count);
            results.Should().OnlyContain(r => r.Pixels != null);

            for (int k = 0; k < ids.Count; k++)
            {
                results[k].Pixels.Should().Equal(expected[k], $"art {ids[k]}");
                ref readonly var e = ref sprites[ids[k]];
                var t = PackSprites.Trim(in e);
                PixelPicker.VisibleBounds(expected[k], e.OrigW, e.OrigH, t.X, t.Y, t.W, t.H, out int a, out int b, out int c, out int dd);
                results[k].Bounds.Should().Be((a, b, c - a, dd - b));
            }
        }
    }
}
