using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Gumps in the pack (pack-format.md "Gumps", poc-log "Gumps in the pack"): the pack path must give exactly what the
    // client's MUL/UOP GumpsLoader gives, for every gump id: presence, size, RGBA pixels, the gump.def hue.
    //
    // The pack with a gump section: CUO_TEST_GUMP_PACK, else C:\Repositories\uo-packs\classic-1.0 once it has one.
    // CUO_TEST_GUMP_DEF + CUO_TEST_GUMP_DEF_PACK: a gump.def with hued aliases and a pack converted with --gump-def it.
    // Tests that need data they don't find return early.
    public class GumpPackTests
    {
        private const string UoDir = @"C:\Ultima Online Classic";
        private const string DefaultPack = @"C:\Repositories\uo-packs\classic-1.0";

        private static string GumpPackDir()
        {
            var env = Environment.GetEnvironmentVariable("CUO_TEST_GUMP_PACK");
            var dir = string.IsNullOrEmpty(env) ? DefaultPack : env;

            return File.Exists(Path.Combine(dir, StoreConst.GumpsFile)) && Directory.Exists(UoDir) ? dir : null;
        }

        private static (UOFileManager Mul, UOFileManager Pack, PackAssets Assets) Open(string packDir, UOFilesOverrideMap overrides = null)
        {
            ClientVersionHelper.TryParseFromFile(Path.Combine(UoDir, "client.exe"), out var text);
            ClientVersionHelper.IsClientVersionValid(text, out var cv);
            var assets = PackAssets.Open(packDir);
            var mul = new UOFileManager(cv, UoDir, overrides ?? new UOFilesOverrideMap(), assets);
            mul.Hues.Load();
            mul.Gumps.UsePack = false;
            mul.Gumps.Load();
            var pack = new UOFileManager(cv, UoDir, assets);
            pack.Hues.Load();
            pack.Gumps.UsePack = true;
            pack.Gumps.Load();

            return (mul, pack, assets);
        }

        private static int CompareAll(GumpsLoader mul, GumpsLoader pack, out int present)
        {
            pack.FromPack.Should().BeTrue();
            mul.FromPack.Should().BeFalse();
            pack.UseUOPGumps.Should().Be(mul.UseUOPGumps);
            present = 0;
            int bad = 0;
            var rng = new Random(7);

            for (uint id = 0; id < (uint)Math.Max(mul.Count, pack.Count); id++)
            {
                var m = mul.GetGump(id);
                int count = pack.PixelCount(id);
                bool sized = pack.TryGetSize(id, out int w, out int h);

                if (m.Pixels.IsEmpty)
                {
                    if (count != 0 || sized)
                    {
                        bad++;
                    }

                    continue;
                }

                present++;

                if (!sized || w != m.Width || h != m.Height || count != w * h)
                {
                    bad++;

                    continue;
                }

                // a dirty pooled buffer, as the client's decodes use: every texel must be written
                var buffer = ArrayPool<uint>.Shared.Rent(count + rng.Next(64));
                buffer.AsSpan().Fill(0xDEADBEEF);
                var p = pack.GetGump(id, buffer);

                if (p.Width != m.Width || p.Height != m.Height || !p.Pixels.SequenceEqual(m.Pixels))
                {
                    bad++;
                }

                ArrayPool<uint>.Shared.Return(buffer);
            }

            return bad;
        }

        [Fact]
        public void Every_Gump_Id_Matches_The_Mul_Loader()
        {
            var dir = GumpPackDir();

            if (dir == null)
            {
                return;
            }

            var (mul, pack, assets) = Open(dir);

            using (assets)
            {
                int bad = CompareAll(mul.Gumps, pack.Gumps, out int present);
                bad.Should().Be(0);
                present.Should().BeGreaterThan(1000);
                pack.Gumps.Count.Should().BeLessThanOrEqualTo(mul.Gumps.Count);
                pack.Gumps.Dispose();
            }
        }

        [Fact]
        public void Gump_Def_Hues_Match_The_Mul_Loader_For_Every_Hue_Kind()
        {
            var dir = GumpPackDir();

            if (dir == null)
            {
                return;
            }

            var (mul, pack, assets) = Open(dir);

            using (assets)
            {
                int hueCount = mul.Hues.HuesCount;
                var hues = new ushort[] { 1, 2, 0x21, 0x0481, 1000, (ushort)(hueCount - 1), (ushort)hueCount, 0x8000, 0xFFFF };
                var slots = pack.Gumps.PackStore.Slots;
                var ids = Enumerable.Range(0, slots.Length).Where(i => slots[i].Present).Where((_, n) => n % 97 == 0).Take(40).ToArray();
                ids.Length.Should().BeGreaterThan(20);

                foreach (int id in ids)
                {
                    foreach (var hue in hues)
                    {
                        mul.Gumps.File.Entries[id].Hue = hue;
                        slots[id].Arg = hue;
                        var m = mul.Gumps.GetGump((uint)id);
                        var p = pack.Gumps.GetGump((uint)id);
                        p.Pixels.SequenceEqual(m.Pixels).Should().BeTrue($"gump {id} hue {hue}");
                    }
                }

                pack.Gumps.Dispose();
            }
        }

        [Fact]
        public void Hued_Gump_Def_Aliases_Converted_From_A_Custom_Def_Match()
        {
            var def = Environment.GetEnvironmentVariable("CUO_TEST_GUMP_DEF");
            var dir = Environment.GetEnvironmentVariable("CUO_TEST_GUMP_DEF_PACK");

            if (string.IsNullOrEmpty(def) || string.IsNullOrEmpty(dir) || !File.Exists(def) || !Directory.Exists(UoDir))
            {
                return;
            }

            var overrides = new UOFilesOverrideMap { ["gump.def"] = def };
            var (mul, pack, assets) = Open(dir, overrides);

            using (assets)
            {
                int bad = CompareAll(mul.Gumps, pack.Gumps, out _);
                bad.Should().Be(0);
                pack.Gumps.PackStore.Slots.Count(s => s.Present && s.Arg != 0).Should().BeGreaterThan(0, "the custom gump.def has hued aliases");
                pack.Gumps.Dispose();
            }
        }

        [Fact]
        public void Picker_Mask_From_SetTrimmed_Matches_Set_On_Pack_Gumps()
        {
            var dir = GumpPackDir();

            if (dir == null)
            {
                return;
            }

            var (_, pack, assets) = Open(dir);

            using (assets)
            {
                var slots = pack.Gumps.PackStore.Slots;
                var a = new PixelPicker();
                var b = new PixelPicker();
                int n = 0;

                for (uint id = 0; id < slots.Length && n < 400; id++)
                {
                    if (!slots[id].Present || id % 7 != 0)
                    {
                        continue;
                    }

                    var g = pack.Gumps.GetGump(id);
                    a.Set(id, g.Width, g.Height, g.Pixels);
                    b.SetTrimmed(id, g.Width, g.Height, 0, 0, g.Width, g.Height, g.Pixels);

                    for (int y = -1; y <= g.Height; y += Math.Max(1, g.Height / 23))
                    {
                        for (int x = -1; x <= g.Width; x++)
                        {
                            b.Get(id, x, y).Should().Be(a.Get(id, x, y));
                        }
                    }

                    n++;
                }

                n.Should().BeGreaterThan(100);
                pack.Gumps.Dispose();
            }
        }

        [Fact]
        public void Pack_Decodes_From_Many_Threads_Match_Sequential()
        {
            var dir = GumpPackDir();

            if (dir == null)
            {
                return;
            }

            var (_, pack, assets) = Open(dir);

            using (assets)
            {
                var slots = pack.Gumps.PackStore.Slots;
                var ids = Enumerable.Range(0, slots.Length).Where(i => slots[i].Present).ToArray();
                var expected = ids.ToDictionary(i => i, i => pack.Gumps.GetGump((uint)i).Pixels.ToArray());
                int bad = 0;

                Parallel.ForEach(ids.Concat(ids.Reverse()), new ParallelOptions { MaxDegreeOfParallelism = 8 }, id =>
                {
                    int count = pack.Gumps.PixelCount((uint)id);
                    var buffer = ArrayPool<uint>.Shared.Rent(count);
                    var g = pack.Gumps.GetGump((uint)id, buffer);

                    if (!g.Pixels.SequenceEqual(expected[id]))
                    {
                        System.Threading.Interlocked.Increment(ref bad);
                    }

                    ArrayPool<uint>.Shared.Return(buffer);
                });

                bad.Should().Be(0);
                pack.Gumps.Dispose();
            }
        }

        [Fact]
        public void GumpCodec_Round_Trips_Random_Images()
        {
            var rng = new Random(11);
            var expand = Enumerable.Range(0, 32).Select(c => (uint)((c << 3) | (c >> 2))).ToArray();

            for (int n = 0; n < 300; n++)
            {
                int w = n == 0 ? 70000 : rng.Next(1, 300), h = n == 0 ? 1 : rng.Next(1, 200);
                var t = new ushort[w * h];
                int mode = n % 4;

                for (int i = 0; i < t.Length; i++)
                {
                    t[i] = mode switch
                    {
                        0 => 0,
                        1 => (ushort)(0x8000 | 0x1234),
                        2 => rng.Next(4) == 0 ? (ushort)0 : (ushort)(0x8000 | rng.Next(3)),
                        _ => rng.Next(3) == 0 ? (ushort)0 : (ushort)(0x8000 | rng.Next(0x8000)),
                    };
                }

                var enc = GumpCodec.EncodeRle(t, w, h);
                var back = new ushort[w * h + 5];
                back.AsSpan().Fill(0xBEEF);
                GumpCodec.DecodeRle(enc, w, h, back).Should().BeTrue();
                back.AsSpan(0, w * h).SequenceEqual(t).Should().BeTrue();

                var rgba = new uint[w * h];
                GumpCodec.DecodeRle(enc, w, h, rgba, expand, default).Should().BeTrue();

                for (int i = 0; i < t.Length; i += 13)
                {
                    uint want = t[i] == 0 ? 0 : expand[(t[i] >> 10) & 31] | expand[(t[i] >> 5) & 31] << 8 | expand[t[i] & 31] << 16 | 0xFF000000;
                    rgba[i].Should().Be(want);
                }
            }

            // malformed: row table past the end, rows out of order
            GumpCodec.DecodeRle(new byte[4], 1, 2, new ushort[2]).Should().BeFalse();
            var bad = GumpCodec.EncodeRle(new ushort[] { 0x8001, 0x8002 }, 1, 2);
            BitConverter.GetBytes(99u).CopyTo(bad, 0);
            GumpCodec.DecodeRle(bad, 1, 2, new ushort[2]).Should().BeFalse();
        }

        [Fact]
        public void RecordStore_Round_Trips_Raw_And_Brotli_And_Rejects_Bad_Files()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cuo-recordstore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var path = Path.Combine(dir, "s.bin");
                var w = new RecordStoreWriter(10);
                var a = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
                var b = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
                int ra = w.Add(RecordStoreWriter.Encode(a, StoreConst.CodecBrotli), (uint)a.Length, StoreConst.CodecBrotli, 50, 100);
                int rb = w.Add(RecordStoreWriter.Encode(b, StoreConst.CodecRaw), (uint)b.Length, StoreConst.CodecRaw, 15, 20);
                w.Slots[1] = new StoreSlot { Record = (uint)ra + 1 };
                w.Slots[9] = new StoreSlot { Record = (uint)rb + 1, Arg = 33 };
                w.Slots[4] = new StoreSlot { Record = (uint)ra + 1, Arg = 5 };
                w.Write(path, StoreConst.MagicGumps, StoreConst.GumpFlagUop);

                using (var s = RecordStore.Open(path, StoreConst.MagicGumps))
                {
                    s.Header.Flags.Should().Be(StoreConst.GumpFlagUop);
                    s.TryGet(0, out _, out _).Should().BeFalse();
                    s.TryGet(10, out _, out _).Should().BeFalse();
                    s.TryGet(-1, out _, out _).Should().BeFalse();
                    s.TryGet(4, out var slot4, out var rec4).Should().BeTrue();
                    slot4.Arg.Should().Be(5);
                    rec4.Meta0.Should().Be(50);
                    var buf = new byte[6000];
                    s.Read(rec4, buf);
                    buf.AsSpan(0, a.Length).SequenceEqual(a).Should().BeTrue();
                    s.TryGet(9, out _, out var rec9).Should().BeTrue();
                    s.Read(rec9, buf);
                    buf.AsSpan(0, b.Length).SequenceEqual(b).Should().BeTrue();
                }

                var wrongMagic = () => RecordStore.Open(path, 0x12345678);
                wrongMagic.Should().Throw<InvalidDataException>();

                var truncated = Path.Combine(dir, "t.bin");
                File.WriteAllBytes(truncated, File.ReadAllBytes(path).AsSpan(0, (int)new FileInfo(path).Length - 10).ToArray());
                var bad = () => RecordStore.Open(truncated, StoreConst.MagicGumps);
                bad.Should().Throw<InvalidDataException>();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
