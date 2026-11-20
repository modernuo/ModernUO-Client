using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // shared_cliloc (SharedTableCache, ClilocLoader.Shared): the cache file is built once, reused, rebuilt when damaged,
    // stale folders are deleted unless in use, racing builders agree, and lookups are the same with the switch on and off.
    public sealed class SharedClilocTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cuo-shared-cliloc-" + Guid.NewGuid().ToString("N"));
        private readonly List<IDisposable> _dispose = new List<IDisposable>();

        public SharedClilocTests() => Directory.CreateDirectory(_tmp);

        public void Dispose()
        {
            foreach (var d in _dispose)
            {
                d.Dispose();
            }

            try
            {
                Directory.Delete(_tmp, true);
            }
            catch (IOException)
            {
            }
        }

        private string CacheRoot => Path.Combine(_tmp, "cache");

        private SharedTableCache NewCache(string key = "aaaa", long cap = SharedTableCache.DefaultCapBytes)
        {
            var c = new SharedTableCache(CacheRoot, key, cap);
            _dispose.Add(c);
            c.Open().Should().BeTrue();

            return c;
        }

        private static byte[] Payload(int n, int seed)
        {
            var b = new byte[n];
            new Random(seed).NextBytes(b);

            return b;
        }

        private static SharedTableCache.CacheSource Source(int raw) => new SharedTableCache.CacheSource { FileBytes = 1000, Offset = 64, StoredBytes = 10, RawBytes = (uint)raw, Codec = 2 };

        private static unsafe byte[] Read(SharedTableCache.Mapping m, int raw) =>
            new ReadOnlySpan<byte>(m.Pointer + SharedTableCache.CacheHeader.Size, raw).ToArray();

        [Fact]
        public void A_Cache_File_Is_Built_Once_Then_Reused()
        {
            var data = Payload(100_000, 1);
            int fills = 0;
            var a = NewCache();
            var m = a.GetOrBuild("t.bin", Source(data.Length), data.Length, d => { fills++; data.CopyTo(d); });

            m.Should().NotBeNull();
            Read(m, data.Length).Should().Equal(data);
            a.Built.Should().Be(1);

            var b = NewCache();
            var m2 = b.GetOrBuild("t.bin", Source(data.Length), data.Length, d => { fills++; data.CopyTo(d); });

            Read(m2, data.Length).Should().Equal(data);
            b.Reused.Should().Be(1);
            b.Built.Should().Be(0);
            fills.Should().Be(1);
            a.GetOrBuild("t.bin", Source(data.Length), data.Length, d => fills++).Should().BeSameAs(m, "the same file asked again is the same mapping");
        }

        [Fact]
        public void Prefault_Is_Safe_Before_During_And_After_Dispose()
        {
            // the loader queues Prefault on a worker; the table can be disposed first (this crashed the test host)
            var data = Payload(1 << 20, 7);
            var path = Path.Combine(_tmp, "raw.bin");
            File.WriteAllBytes(path, data);
            var c = NewCache();

            for (int i = 0; i < 50; i++)
            {
                var m = c.MapFileRange(path, i * 4096, data.Length - i * 4096); // a new range each time: the cache returns a range's mapping again
                var t = Task.Run(() => m.Prefault());
                m.Dispose();
                t.Wait();
                m.Prefault().Should().Be(0);
            }
        }

        [Theory]
        [InlineData("flip")]
        [InlineData("truncate")]
        [InlineData("empty")]
        [InlineData("other-source")]
        public void A_Damaged_Or_Partial_File_Is_Rebuilt_Never_Used(string damage)
        {
            var data = Payload(50_000, 2);
            var a = new SharedTableCache(CacheRoot, "bbbb");
            a.Open();
            a.GetOrBuild("t.bin", Source(data.Length), data.Length, d => data.CopyTo(d)).Should().NotBeNull();
            a.Dispose(); // unmapped, so the file can be damaged
            string file = Path.Combine(CacheRoot, "bbbb", "t.bin");
            var source = Source(data.Length);

            switch (damage)
            {
                case "flip":
                    var bytes = File.ReadAllBytes(file);
                    bytes[SharedTableCache.CacheHeader.Size + 1234] ^= 0x40;
                    File.WriteAllBytes(file, bytes);

                    break;
                case "truncate":
                    using (var fs = new FileStream(file, FileMode.Open))
                    {
                        fs.SetLength(fs.Length / 2);
                    }

                    break;
                case "empty":
                    File.WriteAllBytes(file, Array.Empty<byte>());

                    break;
                case "other-source":
                    source.StoredBytes = 11;

                    break;
            }

            var b = NewCache("bbbb");
            int fills = 0;
            var m = b.GetOrBuild("t.bin", source, data.Length, d => { fills++; data.CopyTo(d); });

            m.Should().NotBeNull();
            Read(m, data.Length).Should().Equal(data);
            fills.Should().Be(1);
            b.Rebuilt.Should().Be(1);
        }

        [Fact]
        public void Stale_Folders_Are_Deleted_Unless_In_Use()
        {
            var inUse = NewCache("old1");
            inUse.GetOrBuild("t.bin", Source(10), 10, d => d.Fill(1)).Should().NotBeNull();

            // a crashed instance's folder: no lock held, a cache file and a leftover temp
            string crashed = Path.Combine(CacheRoot, "old2");
            Directory.CreateDirectory(crashed);
            File.WriteAllBytes(Path.Combine(crashed, "t.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(crashed, "t.bin.1.abc.tmp"), new byte[10]);
            File.WriteAllBytes(Path.Combine(crashed, SharedTableCache.LockFileName), Array.Empty<byte>());

            var current = NewCache("new");

            current.StaleFoldersDeleted.Should().Be(1);
            current.StaleFoldersInUse.Should().Be(1);
            Directory.Exists(crashed).Should().BeFalse();
            Directory.Exists(Path.Combine(CacheRoot, "old1")).Should().BeTrue("an instance holds it");

            inUse.Dispose();
            var later = NewCache("new");

            later.StaleFoldersDeleted.Should().Be(1, "once nobody uses it, the next launch deletes it");
            Directory.Exists(Path.Combine(CacheRoot, "old1")).Should().BeFalse();
        }

        [Fact]
        public void Old_Temp_Files_In_The_Current_Folder_Are_Deleted()
        {
            string folder = Path.Combine(CacheRoot, "cccc");
            Directory.CreateDirectory(folder);
            string oldTemp = Path.Combine(folder, "t.bin.9.dead.tmp");
            string newTemp = Path.Combine(folder, "t.bin.9.live.tmp");
            File.WriteAllBytes(oldTemp, new byte[4]);
            File.WriteAllBytes(newTemp, new byte[4]);
            File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow - SharedTableCache.StaleTempAge - TimeSpan.FromMinutes(1));

            NewCache("cccc");

            File.Exists(oldTemp).Should().BeFalse();
            File.Exists(newTemp).Should().BeTrue("a builder may still be writing it");
        }

        [Fact]
        public void Two_Builders_Racing_Both_Map_One_Valid_File()
        {
            var data = Payload(200_000, 3);
            var a = NewCache("dddd");
            var b = NewCache("dddd");
            using var bothFilling = new Barrier(2);

            SharedTableCache.Mapping Build(SharedTableCache c) =>
                c.GetOrBuild("t.bin", Source(data.Length), data.Length, d =>
                {
                    data.CopyTo(d);
                    bothFilling.SignalAndWait(TimeSpan.FromSeconds(10));
                });

            var ta = Task.Run(() => Build(a));
            var tb = Task.Run(() => Build(b));
            Task.WaitAll(ta, tb);

            ta.Result.Should().NotBeNull();
            tb.Result.Should().NotBeNull();
            Read(ta.Result, data.Length).Should().Equal(data);
            Read(tb.Result, data.Length).Should().Equal(data);
            Directory.EnumerateFiles(Path.Combine(CacheRoot, "dddd"), "*.tmp").Should().BeEmpty("the loser deletes its temp");
        }

        [Fact]
        public void A_File_Past_The_Cap_Is_Not_Written()
        {
            var c = NewCache("eeee", cap: 1000);

            c.GetOrBuild("t.bin", Source(5000), 5000, d => d.Fill(7)).Should().BeNull();
            c.Fallbacks.Should().Be(1);
            File.Exists(Path.Combine(CacheRoot, "eeee", "t.bin")).Should().BeFalse();
        }

        [Fact]
        public void Without_The_Disk_Cache_Nothing_Is_Written()
        {
            var c = new SharedTableCache(CacheRoot, "ffff", allowDiskCache: false);
            _dispose.Add(c);
            c.Open();

            c.GetOrBuild("t.bin", Source(10), 10, d => d.Fill(7)).Should().BeNull();
            File.Exists(Path.Combine(CacheRoot, "ffff", "t.bin")).Should().BeFalse();
        }

        // ------------------------------------------------------------------ the loader, both switch states

        private static readonly (int Id, string Text)[] Enu =
        {
            (500000, "Hello"), (500001, "World ~1_NAME~"), (1000000, "a sword"), (1060000, "Ünïcødé ☃"), (3000000, ""),
        };

        private static readonly (int Id, string Text)[] Deu = { (500000, "Hallo"), (1000000, "ein Schwert") };

        private string WritePack(bool compress)
        {
            string pack = Path.Combine(_tmp, compress ? "pack-z" : "pack-raw");
            var w = new RecordStoreWriter(3);

            void AddTable((int Id, string Text)[] rows, string lang, ushort kind)
            {
                var raw = ClilocTable.Build(rows.Select(r => (r.Id, Encoding.UTF8.GetBytes(r.Text))).ToList());
                ushort codec = compress ? StoreConst.CodecZstd : StoreConst.CodecRaw;
                var stored = compress ? RecordStoreWriter.Encode(raw, codec) : raw;
                var (m0, m1) = lang == null ? ((ushort)0, (ushort)0) : ClilocTable.PackLanguage(lang);
                int rec = w.Add(stored, (uint)raw.Length, codec, m0, m1, kind);
                w.Slots[rec] = new StoreSlot { Record = (uint)rec + 1 };
            }

            AddTable(Enu, "enu", ClilocStore.KindLanguage);
            AddTable(Deu, "deu", ClilocStore.KindLanguage);
            AddTable(new[] { (3100001, "Breed") }, null, ClilocStore.KindOverrides);
            w.Write(Path.Combine(pack, TextConst.ClilocFile), TextConst.MagicCliloc, 0);

            return pack;
        }

        private ClilocLoader Loader(string pack, SharedTableCache shared, bool check, string lang)
        {
            string uo = Path.Combine(_tmp, "uo");
            Directory.CreateDirectory(uo);
            var fm = new UOFileManager(ClientVersion.CV_7090, uo, PackAssets.OpenForTests(pack));
            _dispose.Add(fm);
            var c = fm.Clilocs;
            c.Shared = shared;
            c.CheckShared = check;
            c.Load(lang);

            return c;
        }

        [Theory]
        [InlineData(false, "enu")]
        [InlineData(false, "deu")]
        [InlineData(true, "enu")]
        [InlineData(true, "deu")]
        public void Lookups_Are_The_Same_With_The_Switch_On_And_Off(bool compress, string lang)
        {
            string pack = WritePack(compress);
            var off = Loader(pack, null, false, lang);
            var cache = NewCache("gggg");
            var on = Loader(pack, cache, true, lang);

            off.LanguageTable.IsMapped.Should().BeFalse();
            on.LanguageTable.IsMapped.Should().BeTrue();
            on.PackOverridesTable.IsMapped.Should().BeTrue();

            if (lang != "enu")
            {
                on.EnglishTable.IsMapped.Should().BeTrue();
            }

            if (compress)
            {
                cache.Built.Should().Be(lang == "enu" ? 2 : 3);
                cache.DirectMaps.Should().Be(0);
            }
            else
            {
                cache.Built.Should().Be(0, "raw records are mapped straight from the pack");
                cache.DirectMaps.Should().Be(lang == "enu" ? 2 : 3);
            }

            foreach (int id in Enu.Select(e => e.Id).Concat(Deu.Select(e => e.Id)).Concat(new[] { 3100001, 1, 999999, int.MaxValue }))
            {
                off.TryGetString(id, out string a).Should().Be(on.TryGetString(id, out string b));
                b.Should().Be(a);
                on.Translate(id, "x").Should().Be(off.Translate(id, "x"));
            }

            on.GetString(1060000).Should().BeSameAs(on.GetString(1060000), "a mapped table caches its strings too");
#if CUO_DEVTOOLS
            on.CheckCounts.Mismatches.Should().Be(0);
            on.CheckCounts.Lookups.Should().BeGreaterThan(0);
#endif
        }

        [Fact]
        public void A_Second_Instance_Reuses_The_Cache_Files()
        {
            string pack = WritePack(true);
            var first = NewCache("iiii");
            Loader(pack, first, false, "deu");
            var second = NewCache("iiii");
            var c = Loader(pack, second, false, "deu");

            first.Built.Should().Be(3);
            second.Reused.Should().Be(3);
            second.Built.Should().Be(0);
            c.GetString(500000).Should().Be("Hallo");
            c.GetString(500001).Should().Be("World ~1_NAME~");
        }

        [Fact]
        public void Mapped_Table_Parses_Like_The_Heap_One_And_Rejects_The_Same_Payloads()
        {
            var good = ClilocTable.Build(new List<(int, byte[])> { (5, Encoding.UTF8.GetBytes("five")), (1, Encoding.UTF8.GetBytes("one")) });
            Mapped(good).Should().NotBeNull();
            Mapped(good.AsSpan(0, good.Length - 1).ToArray()).Should().BeNull();
            var unsorted = (byte[])good.Clone();
            (unsorted[8], unsorted[12]) = (unsorted[12], unsorted[8]);
            Mapped(unsorted).Should().BeNull();

            var t = Mapped(good);
            t.TryGetString(5, out string s).Should().BeTrue();
            s.Should().Be("five");
            t.CacheBlocks.Should().Be(1);
            t.Materialized.Should().Be(1);
        }

        private static readonly List<System.Runtime.InteropServices.GCHandle> Pins = new List<System.Runtime.InteropServices.GCHandle>();

        private static unsafe ClilocTable Mapped(byte[] payload)
        {
            var h = System.Runtime.InteropServices.GCHandle.Alloc(payload, System.Runtime.InteropServices.GCHandleType.Pinned);

            lock (Pins)
            {
                Pins.Add(h);
            }

            return ClilocTable.TryParseMapped((byte*)h.AddrOfPinnedObject(), payload.Length, payload);
        }
    }
}
