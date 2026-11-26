using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClassicUO.Assets;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Pack revision 1.6: zstd through one decode seam (PackCodec); Brotli files and records are refused with the
    // convert-again message; decodes reuse a per-thread or per-worker context and allocate nothing.
    public class PackCodecTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuo-packcodec-" + Guid.NewGuid().ToString("N"));

        public PackCodecTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static byte[] Sample(int length, int seed)
        {
            // compressible but not trivial: runs of random bytes
            var r = new Random(seed);
            var b = new byte[length];
            for (int i = 0; i < length;)
            {
                byte v = (byte)r.Next(256);
                int run = r.Next(1, 24);
                for (int k = 0; k < run && i < length; k++) b[i++] = v;
            }
            return b;
        }

        private static void ShouldBeConvertAgain(Action a)
        {
            a.Should().Throw<InvalidDataException>().Which.Message.Should().Contain("1.6").And.Contain("Convert the client again");
        }

        // ---- the seam ----

        [Fact]
        public void Checksummed_Frames_Decode_And_A_Corrupt_One_Is_Refused()
        {
            var raw = Sample(300_000, 11);
            var plain = PackCodec.Encode(PackCodec.Zstd, raw, checksum: false);
            var summed = PackCodec.Encode(PackCodec.Zstd, raw, checksum: true);

            // frame header descriptor (after the 4-byte magic): bit 2 = content checksum, bits 6-7 = content size field
            (plain[4] & 0x04).Should().Be(0);
            (summed[4] & 0x04).Should().Be(0x04);
            (summed[4] & 0xC0).Should().NotBe(0, "the frame states its content size, so readers decode in one pass");
            summed.Length.Should().Be(plain.Length + 4);

            var dst = new byte[raw.Length];
            using (var d = PackCodec.Create(PackCodec.Zstd))
            {
                d.Decode(summed, dst);
                dst.Should().Equal(raw);

                var bad = (byte[])summed.Clone();
                bad[^1] ^= 0x5A;
                FluentActions.Invoking(() => d.Decode(bad, dst)).Should().Throw<InvalidDataException>();
                // the context is still usable after a refused frame
                d.Decode(summed, dst);
                dst.Should().Equal(raw);
            }
        }

        [Fact]
        public void File_Kinds_Map_To_Codecs_And_Brotli_Is_Refused()
        {
            PackCodec.OfFileKind(FileKind.Raw, "x").Should().Be(PackCodec.Raw);
            PackCodec.OfFileKind(FileKind.ChunkedZstd, "x").Should().Be(PackCodec.Zstd);
            ShouldBeConvertAgain(() => PackCodec.OfFileKind(FileKind.RetiredBrotli, "x"));
            ShouldBeConvertAgain(() => PackCodec.OfFileKind(FileKind.RetiredChunkedBrotli, "x"));
            FluentActions.Invoking(() => PackCodec.OfFileKind(99, "x")).Should().Throw<InvalidDataException>();
            ShouldBeConvertAgain(() => PackCodec.Check(PackCodec.RetiredBrotli, "x"));
            ShouldBeConvertAgain(() => PackCodec.Create(PackCodec.RetiredBrotli));
            ShouldBeConvertAgain(() => PackCodec.ForThread(PackCodec.RetiredBrotli));
            FluentActions.Invoking(() => PackCodec.Check(7, "x")).Should().Throw<InvalidDataException>();
        }

        [Theory]
        [InlineData(PackCodec.Raw)]
        [InlineData(PackCodec.Zstd)]
        public void Codec_Round_Trips_Every_Size(ushort codec)
        {
            foreach (int len in new[] { 0, 1, 17, 4096, 65536 + 3, 1 << 20 })
            {
                var raw = Sample(len, len);
                var stored = PackCodec.Encode(codec, raw);
                stored.Length.Should().BeLessThanOrEqualTo((int)PackCodec.MaxStoredBytes(codec, len));
                var dst = new byte[len];
                using var dec = PackCodec.Create(codec);
                dec.Codec.Should().Be(codec);
                dec.Decode(stored, dst);
                dst.Should().Equal(raw);
                // the thread's decoder gives the same bytes
                Array.Clear(dst);
                PackCodec.ForThread(codec).Decode(stored, dst);
                dst.Should().Equal(raw);
            }
        }

        [Fact]
        public void Zstd_Decode_Rejects_Wrong_Size_Trailing_Bytes_And_Corruption()
        {
            var raw = Sample(10000, 1);
            var stored = PackCodec.Encode(PackCodec.Zstd, raw);
            var dec = PackCodec.ForThread(PackCodec.Zstd);
            FluentActions.Invoking(() => dec.Decode(stored, new byte[raw.Length - 1])).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => dec.Decode(stored, new byte[raw.Length + 1])).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => dec.Decode(stored.Concat(new byte[] { 1, 2, 3 }).ToArray(), new byte[raw.Length])).Should().Throw<InvalidDataException>();
            // a broken frame header fails; frames carry no checksum (as Brotli streams did not), so a flipped literal
            // byte can decode to other bytes of the right size, which the content type's own checks then see
            var bad = (byte[])stored.Clone();
            bad[0] ^= 0x55;
            FluentActions.Invoking(() => dec.Decode(bad, new byte[raw.Length])).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => dec.Decode(stored.AsSpan(0, stored.Length - 4).ToArray(), new byte[raw.Length])).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => dec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[raw.Length])).Should().Throw<InvalidDataException>();
            // the context recovers after a failure
            var dst = new byte[raw.Length];
            dec.Decode(stored, dst);
            dst.Should().Equal(raw);
        }

        [Fact]
        public void Zstd_Writes_The_Content_Size_And_The_Ship_Level()
        {
            PackCodec.WriteLevel.Should().Be(19);
            var raw = Sample(300000, 2);
            var stored = PackCodec.Encode(PackCodec.Zstd, raw);
            System.IO.Compression.ZstandardDecoder.TryGetMaxDecompressedLength(stored, out long size).Should().BeTrue();
            size.Should().Be(raw.Length, "a one-pass decode needs the frame's content size");
        }

        // ---- allocation-free decodes ----

        [Fact]
        public void Thread_Decoder_Allocates_Nothing_Per_Decode()
        {
            var raws = Enumerable.Range(0, 4).Select(i => Sample(200000 + i * 1000, 10 + i)).ToArray();
            var stored = raws.Select(r => PackCodec.Encode(PackCodec.Zstd, r)).ToArray();
            var dst = new byte[raws.Max(r => r.Length)];
            for (int k = 0; k < 20; k++)
                for (int i = 0; i < raws.Length; i++)
                    PackCodec.ForThread(PackCodec.Zstd).Decode(stored[i], dst.AsSpan(0, raws[i].Length));
            long best = long.MaxValue;
            // Bounded retries allow transient runtime allocations, but never a per-decode allocation.
            for (int attempt = 0; attempt < 40 && best > 0; attempt++)
            {
                if (attempt > 0) Thread.Sleep(50);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int k = 0; k < 10; k++)
                    for (int i = 0; i < raws.Length; i++)
                        PackCodec.ForThread(PackCodec.Zstd).Decode(stored[i], dst.AsSpan(0, raws[i].Length));
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - a0);
            }
            best.Should().Be(0);
        }

        [Fact]
        public void Chunked_Decoder_Round_Trips_And_Allocates_Nothing_In_Steady_State()
        {
            var logical = Sample(5 * 65536 + 123, 3);
            var path = Path.Combine(_dir, "page_0000.bin");
            var chain = logical.AsSpan(16).ToArray();
            AtlasPage.Write(path, 64, 1, PixelFormat.B5G5R5A1, chain, FileKind.ChunkedZstd, 65536);
            var into = new byte[chain.Length];
            using var dec = new ChunkedDecoder(3);
            AtlasPage.Read(path, into, dec).DataBytes.Should().Be((uint)chain.Length);
            into.Should().Equal(chain);
            using var fh = File.OpenHandle(path);
            Span<byte> hdr = stackalloc byte[16];
            for (int k = 0; k < 20; k++) dec.Decode(fh, 16, PackCodec.Zstd, into, 16, hdr);
            // The caller also decodes chunks; count this thread over complete decode batches.
            long best = long.MaxValue;
            for (int attempt = 0; attempt < 40 && best > 0; attempt++)
            {
                if (attempt > 0) Thread.Sleep(50);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int k = 0; k < 5; k++) dec.Decode(fh, 16, PackCodec.Zstd, into, 16, hdr);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - a0);
            }
            best.Should().Be(0, "the workers reuse their contexts and buffers");
            into.Should().Equal(chain);
            // the sequential stream and the header-only read agree
            AtlasPage.Read(path, new byte[chain.Length]).Should().Be(AtlasPage.ReadHeader(path));
        }

        // ---- record stores: per record kind ----

        [Fact]
        public void Record_Store_Round_Trips_Zstd_Raw_And_The_Raw_Fallback()
        {
            var big = Sample(300000, 4);                 // a large gump image's texels
            var small = Sample(7000, 5);                 // a small run-length gump
            var noise = new byte[512];                   // incompressible: kept raw
            new Random(6).NextBytes(noise);
            var w = new RecordStoreWriter(4);
            ushort c0 = StoreConst.CodecZstd, c1 = StoreConst.CodecZstd, c2 = StoreConst.CodecZstd;
            var p0 = RecordStoreWriter.EncodeOrRaw(big, ref c0);
            var p1 = RecordStoreWriter.EncodeOrRaw(small, ref c1);
            var p2 = RecordStoreWriter.EncodeOrRaw(noise, ref c2);
            c0.Should().Be(StoreConst.CodecZstd);
            c1.Should().Be(StoreConst.CodecZstd, "small gump records are compressed too");
            c2.Should().Be(StoreConst.CodecRaw, "a record zstd does not shrink is stored raw");
            p2.Should().Equal(noise);
            w.Slots[0] = new StoreSlot { Record = (uint)w.Add(p0, (uint)big.Length, c0, 0, 0) + 1 };
            w.Slots[1] = new StoreSlot { Record = (uint)w.Add(p1, (uint)small.Length, c1, 0, 0) + 1 };
            w.Slots[3] = new StoreSlot { Record = (uint)w.Add(p2, (uint)noise.Length, c2, 0, 0) + 1 };
            var path = Path.Combine(_dir, "store.bin");
            w.Write(path, StoreConst.MagicGumps, 0);

            using var store = RecordStore.Open(path, StoreConst.MagicGumps);
            foreach (var (id, expected) in new[] { (0, big), (1, small), (3, noise) })
            {
                store.TryGet(id, out _, out var rec).Should().BeTrue();
                var dst = new byte[rec.RawBytes];
                store.Read(rec, dst);
                dst.Should().Equal(expected);
            }
            store.TryGet(2, out _, out _).Should().BeFalse();

            // a small-gump read allocates nothing once the pool and the thread's decoder are warm
            store.TryGet(1, out _, out var r1);
            var buf = new byte[r1.RawBytes];
            long best = long.MaxValue;
            for (int attempt = 0; attempt < 40 && best > 0; attempt++)
            {
                if (attempt > 0) Thread.Sleep(50);
                // A gen2 collection can trim the pool, so warm it immediately before each batch.
                store.Read(r1, buf);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int k = 0; k < 20; k++) store.Read(r1, buf);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - a0);
            }
            best.Should().Be(0);
        }

        // ---- refusal of packs older than 1.6 ----

        [Fact]
        public void Record_Store_With_A_Brotli_Record_Is_Refused_With_The_Convert_Again_Message()
        {
            var w = new RecordStoreWriter(1);
            w.Slots[0] = new StoreSlot { Record = (uint)w.Add(new byte[] { 1, 2, 3 }, 10, PackCodec.RetiredBrotli, 0, 0) + 1 };
            var path = Path.Combine(_dir, "old.bin");
            w.Write(path, StoreConst.MagicGumps, 0);
            ShouldBeConvertAgain(() => RecordStore.Open(path, StoreConst.MagicGumps).Dispose());
        }

        private string WriteOldKindPage(string dir, ushort kind)
        {
            Directory.CreateDirectory(dir);
            var path = PackConst.PagePath(dir, 0);
            AtlasPage.Write(path, 64, 1, PixelFormat.B5G5R5A1, new byte[64 * 64 * 2], FileKind.Raw);
            var bytes = File.ReadAllBytes(path);
            BitConverter.GetBytes(kind).CopyTo(bytes, 6); // FileHeader.Kind
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Theory]
        [InlineData(FileKind.RetiredBrotli)]
        [InlineData(FileKind.RetiredChunkedBrotli)]
        public void Brotli_Pages_And_Regions_Are_Refused_With_The_Convert_Again_Message(ushort kind)
        {
            var page = WriteOldKindPage(Path.Combine(_dir, "art" + kind), kind);
            ShouldBeConvertAgain(() => AtlasPage.ReadHeader(page));
            ShouldBeConvertAgain(() => AtlasPage.OpenChain(page, out _).Dispose());
            using (var dec = new ChunkedDecoder(1))
                ShouldBeConvertAgain(() => AtlasPage.Read(page, new byte[64 * 64 * 2], dec));

            var header = new RegionHeader { BlocksW = 1, BlocksH = 1 };
            var region = Path.Combine(_dir, $"r_{kind}.bin");
            RegionFile.Write(region, header, new[] { default(BlockData) }, false);
            var rb = File.ReadAllBytes(region);
            BitConverter.GetBytes(kind).CopyTo(rb, 6);
            ShouldBeConvertAgain(() => RegionFile.Decompress(rb));
        }

        [Fact]
        public void Pack_With_Brotli_Pages_Is_Refused_At_Open()
        {
            var root = Path.Combine(_dir, "pack15");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"version\":1,\"files\":[]}");
            foreach (var f in new[] { TableConst.TileDataFile, "hues_raw.bin", "radarcol.bin", "multis.bin", "lights.bin", "tileart.bin", "equipconv.bin",
                                      "art/sprites.bin", "tex/texmaps.bin", "anim/anim.bin", "anim/palettes.bin", "anim/animdata.bin", MiscConst.SkillsFile })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, f)));
                File.WriteAllBytes(Path.Combine(root, f), new byte[16]);
            }
            WriteOldKindPage(Path.Combine(root, "art"), FileKind.RetiredChunkedBrotli);
            ShouldBeConvertAgain(() => PackAssets.Open(root).Dispose());

            // the same pack with a 1.6 page opens
            AtlasPage.Write(PackConst.PagePath(Path.Combine(root, "art"), 0), 64, 1, PixelFormat.B5G5R5A1, new byte[64 * 64 * 2], FileKind.ChunkedZstd);
            using var pack = PackAssets.Open(root);
            pack.Root.Should().Be(root);
        }
    }
}
