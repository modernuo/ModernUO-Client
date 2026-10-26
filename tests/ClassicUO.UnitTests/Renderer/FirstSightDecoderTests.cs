using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        private static void WaitReady(FirstSightDecoder d, int count, Func<int> uploaded)
        {
            var sw = Stopwatch.StartNew();

            while (uploaded() < count && sw.ElapsedMilliseconds < 10000)
            {
                d.Drain(long.MaxValue);
                Thread.Sleep(1);
            }
        }

        [Fact]
        public void Each_Request_Decodes_Once_And_Uploads_Only_In_Drain()
        {
            var uploads = new List<uint>();
            int decodes = 0;
            int mainThread = Environment.CurrentManagedThreadId;
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
            });

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
            int uploads = 0;
            var d = new FirstSightDecoder("test", 16, FillPattern, _ => uploads++);

            for (uint i = 0; i < 8; i++)
            {
                d.Request(i);
            }

            var sw = Stopwatch.StartNew();

            while (d.ReadyCount < 8 && sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(1);
            }

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
        public void A_Failed_Decode_Reaches_Upload_Without_Pixels()
        {
            var seen = new List<(uint, bool)>();
            var d = new FirstSightDecoder("test", 8, job =>
            {
                if (job.Index == 3)
                {
                    job.Pixels = ArrayPool<uint>.Shared.Rent(4); // rented before the failure: returned by the decoder
                    throw new InvalidDataException("corrupt");
                }

                return job.Index != 5 && FillPattern(job); // 5: nothing to upload
            }, job => seen.Add((job.Index, job.Pixels != null)));

            d.Request(2);
            d.Request(3);
            d.Request(5);
            WaitReady(d, 3, () => seen.Count);

            seen.Should().BeEquivalentTo(new[] { (2u, true), (3u, false), (5u, false) });
            d.Pending.Should().Be(0);
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
            });

            foreach (int i in ids)
            {
                d.Request((uint)i);
            }

            int done = 0;
            WaitReady(d, ids.Count, () => done = results.Count(r => r.Pixels != null));
            done.Should().Be(ids.Count);

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
