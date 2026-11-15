// SPDX-License-Identifier: BSD-2-Clause

extern alias frozen;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.IO.Audio;
using Sound = ClassicUO.Renderer.Sounds.Sound;
using SoundCache = ClassicUO.Renderer.Sounds.SoundCache;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;
using FrozenFiles = frozen::ClassicUO.Assets.UOFileManager;

namespace ClassicUO.UnitTests.Assets
{
    // The Codex audit of the audio pack (poc-log "Sounds and music in the pack", "Codex audit and dispositions"): one
    // test per finding, on synthetic data.
    public sealed partial class AudioPackTests
    {
        // ------------------------------------------------------------------ P1: the sound cache

        private static readonly FieldInfo WaveBuffer = typeof(UOSound).GetField("_waveBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static byte[] ArrayOf(UOSound s) => (byte[])WaveBuffer.GetValue(s)!;

        // a sound.mul of the given entries plus Sound.def lines aliasing ids [from, from + count) to id 0
        private string AliasPack(byte[] pcm0, int aliases)
        {
            string uo = Dir("uo-alias");
            var entries = new byte[1 + aliases][];
            entries[0] = Entry("big.wav", pcm0);
            WriteMul(uo, entries);
            File.WriteAllLines(Path.Combine(uo, "Sound.def"), Enumerable.Range(1, aliases).Select(i => $"{i} {{0}} 0"));
            string pack = Dir("pack-alias");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                var stats = AudioImport.WriteSounds(src, pack);
                stats.Records.Should().Be(1);
                stats.Present.Should().Be(1 + aliases);
            }

            return pack;
        }

        [Fact]
        public void Aliases_Of_One_Big_Record_Share_One_Array_Within_The_Cache_Budget()
        {
            // Codex's case scaled down 64x: one large record and 128 aliases. Before the fix every id kept its own copy
            // (129 MiB here, 8 GiB at full size); now they share one array and the cache holds 1 MiB.
            byte[] big = Pcm(1 << 20, 11);
            string pack = AliasPack(big, 128);
            var loader = Client(Dir("empty"), pack);
            var cache = new SoundCache(loader, budget: 3L << 19, maxIds: 1024, isBusy: _ => false);
            byte[] first = null;

            for (int id = 0; id <= 128; id++)
            {
                UOSound s = cache.Get(id);
                s.Should().NotBeNull($"id {id} plays");
                s.PcmBytes.Should().Be(big.Length);
                first ??= ArrayOf(s);
                ArrayOf(s).Should().BeSameAs(first, "aliases share the record's one array");
                cache.Bytes.Should().BeLessOrEqualTo(cache.Budget);
            }

            cache.Count.Should().Be(129);
            cache.Arrays.Should().Be(1);
            cache.Bytes.Should().Be(big.Length);
            cache.Evictions.Should().Be(0);
            first.AsSpan().SequenceEqual(big).Should().BeTrue();
            cache.Get(77).Should().BeSameAs(cache.Get(77), "a cached id is served again without a read");
            loader.Dispose();
        }

        [Fact]
        public void Distinct_Sounds_Are_Evicted_Least_Recently_Played_First_Within_The_Budget()
        {
            string uo = Dir("uo-many");
            var pcm = Enumerable.Range(0, 12).Select(i => Pcm(256 << 10, 100 + i)).ToArray();
            WriteMul(uo, pcm.Select((p, i) => Entry($"s{i}", p)).ToArray());
            string pack = Dir("pack-many");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            var loader = Client(Dir("empty"), pack);
            var cache = new SoundCache(loader, budget: 1 << 20, maxIds: 1024, isBusy: _ => false);

            for (int round = 0; round < 2; round++)
            {
                for (int id = 0; id < 12; id++)
                {
                    UOSound s = cache.Get(id);
                    s.Should().NotBeNull();
                    ArrayOf(s).AsSpan().SequenceEqual(pcm[id]).Should().BeTrue();
                    cache.Bytes.Should().BeLessOrEqualTo(1 << 20, "the budget holds after every play");
                }
            }

            cache.Count.Should().Be(4, "four 256 KiB sounds fit 1 MiB");
            cache.Evictions.Should().Be(20);
            UOSound recent = cache.Get(11);
            cache.Get(11).Should().BeSameAs(recent);
            cache.Get(8).Should().NotBeNull();
            cache.Evictions.Should().Be(20, "ids 8..11 were the most recent: still cached");

            // the id cap bounds the cached ids (and their FAudio voices) on its own
            var capped = new SoundCache(loader, budget: 64L << 20, maxIds: 3, isBusy: _ => false);

            for (int id = 0; id < 12; id++)
            {
                capped.Get(id).Should().NotBeNull();
                capped.Count.Should().BeLessOrEqualTo(3);
            }

            loader.Dispose();
        }

        [Fact]
        public void A_Playing_Sound_Is_Never_Evicted_And_A_Sound_That_Cannot_Fit_Is_Refused()
        {
            string uo = Dir("uo-busy");
            WriteMul(uo, Enumerable.Range(0, 6).Select(i => Entry($"s{i}", Pcm(256 << 10, 200 + i))).ToArray());
            string pack = Dir("pack-busy");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            var loader = Client(Dir("empty"), pack);
            var playing = new HashSet<UOSound>();
            var cache = new SoundCache(loader, budget: 1 << 20, maxIds: 1024, isBusy: playing.Contains);

            for (int id = 0; id < 4; id++)
            {
                playing.Add(cache.Get(id));
            }

            cache.Get(4).Should().BeNull("the budget is full of playing sounds");
            cache.Refusals.Should().Be(1);
            cache.Bytes.Should().Be(1 << 20);
            cache.Evictions.Should().Be(0);

            playing.Remove(cache.Get(2)); // id 2 stops (and becomes the most recently played)
            cache.Get(4).Should().NotBeNull("an idle sound makes room");
            cache.Evictions.Should().Be(1);
            cache.Bytes.Should().BeLessOrEqualTo(1 << 20);
            playing.Should().OnlyContain(s => cache.Get(s.Index) == s, "no playing sound was evicted");
            loader.Dispose();
        }

        [Fact]
        public void Simultaneous_Sounds_Are_Bounded_By_Count_And_Bytes()
        {
            var playing = new LinkedList<UOSound>();
            int stopped = 0;

            for (int i = 0; i < 40; i++)
            {
                var s = new Probe($"s{i}", i, new byte[100]);
                stopped += Sound.LimitPlaying(playing, s, maxCount: 32, maxBytes: 1L << 20);
                playing.AddLast(s);
                playing.Count.Should().BeLessOrEqualTo(32);
            }

            stopped.Should().Be(8);
            playing.First!.Value.Index.Should().Be(8, "the oldest were stopped");

            // a replay restarts its sound: it is not counted twice
            UOSound again = playing.Last!.Value;
            Sound.LimitPlaying(playing, again, 32, 1L << 20).Should().Be(0, "the replay leaves the list first, so the 31 others stay");
            playing.Should().NotContain(again);

            // the byte cap bounds FNA's native copies: aliases of one array each get their own copy while playing
            var byBytes = new LinkedList<UOSound>();
            byte[] shared = new byte[300];

            for (int i = 0; i < 10; i++)
            {
                var s = new Probe("alias", i, shared);
                Sound.LimitPlaying(byBytes, s, maxCount: 32, maxBytes: 1000);
                byBytes.AddLast(s);
                byBytes.Sum(x => (long)x.PcmBytes).Should().BeLessOrEqualTo(1000);
            }

            byBytes.Should().HaveCount(3);
        }

        // ------------------------------------------------------------------ P2: loose WAV overflow

        // Codex's case: valid RIFF/WAVE and fmt fields, then a data chunk declaring 0x7FFFFFF0 bytes, 44 bytes in all
        private static byte[] OverflowingWav()
        {
            byte[] w = Wav(22050, 1, 16, Array.Empty<byte>());
            w.Length.Should().Be(44);
            BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(40), 0x7FFFFFF0);

            return w;
        }

        [Fact]
        public void A_Wave_Whose_Data_Size_Would_Overflow_Is_Refused_Cleanly()
        {
            byte[] w = OverflowingWav();
            Action parse = () => AudioConst.ParseWave(w, out _, out _);
            parse.Should().NotThrow();
            AudioConst.ParseWave(w, out int off, out int len).Should().BeNull();
            (off, len).Should().Be((44, 0), "the size is clamped to the bytes present, by subtraction");
            AudioConst.ReadWave(w, out string err).Should().BeNull();
            err.Should().Be("no PCM");

            // a chunk walk past a huge non-data chunk ends cleanly too
            byte[] junk = Wav(22050, 1, 16, new byte[8]);
            "LIST"u8.CopyTo(junk.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(junk.AsSpan(40), int.MaxValue - 3);
            AudioConst.ReadWave(junk, out err).Should().BeNull();
            err.Should().Be("no data chunk");

            // the client: the bad override is ignored and the pack's sound plays
            string pack = GoodSoundStore(out byte[] pcm0);
            string client = Dir("client-ovf");
            Directory.CreateDirectory(Path.Combine(client, "Sounds"));
            File.WriteAllBytes(Path.Combine(client, "Sounds", "0.wav"), w);
            var loader = Client(client, pack);
            Expect(loader, 0, pcm0, "a");
            loader.Dispose();

            // a file over the cap is refused before it is read
            string huge = Path.Combine(client, "Sounds", "1.wav");

            using (var fs = File.Create(huge))
            {
                fs.Write(Wav(22050, 1, 16, new byte[2]));
                fs.SetLength(AudioConst.MaxWaveFileBytes + 1);
            }

            AudioConst.ReadWaveFile(huge, out err).Should().BeNull();
            err.Should().Contain("at most");
        }

        // ------------------------------------------------------------------ P2: invalid override over a valid archive sound

        [Fact]
        public void An_Invalid_Loose_Override_Falls_Back_To_The_Archive_In_The_Converter_And_In_Verify()
        {
            string uo = Dir("uo-override");
            var arch = Enumerable.Range(0, 5).Select(i => Pcm(64 + 2 * i, 300 + i)).ToArray();
            WriteMul(uo, arch.Select((p, i) => Entry($"arch{i}.wav", p)).Concat(new byte[][] { null }).ToArray());
            string sounds = Dir("uo-override/Sounds");
            File.WriteAllBytes(Path.Combine(sounds, "0.wav"), Wav(22050, 1, 16, Pcm(81, 1)));            // odd PCM
            File.WriteAllBytes(Path.Combine(sounds, "1.wav"), Wav(22050, 1, 16, Array.Empty<byte>()));  // empty data chunk
            File.WriteAllBytes(Path.Combine(sounds, "2.wav"), OverflowingWav());                          // the overflow case
            File.WriteAllBytes(Path.Combine(sounds, "3.wav"), Wav(44100, 1, 16, Pcm(80, 3)));            // wrong rate (fell back before too)
            byte[] mine = Pcm(90, 4);
            File.WriteAllBytes(Path.Combine(sounds, "4.wav"), Wav(22050, 1, 16, mine));                 // valid: it still wins
            File.WriteAllBytes(Path.Combine(sounds, "5.wav"), Wav(22050, 1, 16, Pcm(7, 5)));             // odd, no archive entry

            // the converter
            string pack = Dir("pack-override");
            var warnings = new List<string>();

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                var stats = AudioImport.WriteSounds(src, pack, warnings.Add);
                stats.Present.Should().Be(5, "ids 0..3 fall back to the archive, 4 is the loose file, 5 has nothing playable");
                stats.LooseRefused.Should().Be(5);
                stats.Ours.Should().Be(1);
            }

            warnings.Should().HaveCount(5).And.OnlyContain(w => w.Contains("refused"));

            // the client reads the same back, from the pack alone and with the same loose files present
            foreach (string basePath in new[] { Dir("empty"), uo })
            {
                var loader = Client(basePath, pack);

                for (int id = 0; id < 4; id++)
                {
                    Expect(loader, id, arch[id], $"arch{id}.wav");
                }

                Expect(loader, 4, mine, "4");
                loader.TryGetSound(5, out _, out _).Should().BeFalse();
                loader.Dispose();
            }

            // verify's model on the frozen pre-pack loader: the old loader alone answered with the invalid override
            // (which verify then accepted as "absent"); the model falls back to the archive as the client does
            var fm = new FrozenFiles(default, uo);
            fm.Sounds.Load();
            fm.Sounds.TryGetSound(0, out byte[] old0, out _).Should().BeTrue();
            old0.Length.Should().Be(81, "the old loader returned the odd override");

            for (int id = 0; id <= 5; id++)
            {
                int sid = id;
                var (data, name) = AudioImport.ExpectedClientSound(
                    fm.Sounds.LoosePath(sid),
                    () => fm.Sounds.TryGetSound(sid, out var d, out var n) ? (d, n) : (null, null),
                    () => fm.Sounds.TryGetArchiveSound(sid, out var d, out var n) ? (d, n) : (null, null));

                if (id < 4)
                {
                    data.Should().Equal(arch[id], $"id {id}: the archive's sound");
                    name!.Split('\0')[0].Should().Be($"arch{id}.wav");
                }
                else if (id == 4)
                {
                    data.Should().Equal(mine);
                }
                else
                {
                    data.Should().BeNull("an odd override with no archive entry is absent");
                }
            }

            fm.Sounds.ClearResources();
        }

        // ------------------------------------------------------------------ P2: empty records and the overlap sweep

        private static StoreRecord Rec(ulong offset, uint bytes) => new StoreRecord { Offset = offset, StoredBytes = bytes, RawBytes = bytes };

        [Fact]
        public void An_Empty_Record_Does_Not_Hide_An_Overlap()
        {
            // Codex's case: [128,228), [138,138), [148,168) in a 228-byte file whose tables end at 128
            var codex = new[] { Rec(128, 100), Rec(138, 0), Rec(148, 20) };
            AudioConst.CheckNoOverlap(codex).Should().Be("records 0 and 2 overlap");
            AudioConst.CheckNoOverlap(codex.Reverse().ToArray()).Should().NotBeNull("table order does not matter");
            AudioConst.CheckNoOverlap(new[] { Rec(128, 100), Rec(140, 10), Rec(160, 10) }).Should().NotBeNull("two records inside a long one");
            AudioConst.CheckNoOverlap(new[] { Rec(128, 10), Rec(138, 0), Rec(138, 10), Rec(148, 0), Rec(500, 0) }).Should().BeNull("adjacent records and empty ones");

            // the same layout in real stores, sounds and music: the client rejects both
            string pack = Dir("pack-overlap");
            WriteThreeRecordStore(Path.Combine(pack, AudioConst.SoundsFile), AudioConst.MagicSounds, AudioConst.SoundRate, AudioConst.SoundChannels);
            WriteThreeRecordStore(Path.Combine(pack, AudioConst.MusicFile), AudioConst.MagicMusic, AudioConst.MusicFormatMp3, 0);
            var loader = Client(Dir("empty"), pack);
            loader.HasPackSounds.Should().BeFalse("overlapping sound payloads behind an empty record");
            loader.HasPackMusic.Should().BeFalse("overlapping music payloads behind an empty record");
            loader.Dispose();
        }

        // three records (100, 0 and 20 bytes of payload) laid out as Codex's case: [P, P+100), [P+10, P+10), [P+20, P+40)
        private static void WriteThreeRecordStore(string path, uint magic, ushort meta0, ushort meta1)
        {
            var w = new RecordStoreWriter(3);

            foreach (int size in new[] { 100, 10, 20 })
            {
                w.Add(new byte[size], (uint)size, StoreConst.CodecRaw, meta0, meta1);
            }

            for (int i = 0; i < 3; i++)
            {
                w.Slots[i] = new StoreSlot { Record = (uint)i + 1 };
            }

            w.Write(path, magic, 0);
            byte[] f = File.ReadAllBytes(path);
            int rec = 32 + 3 * 8;
            ulong p = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(rec));
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(rec + 24), p + 10);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 24 + 8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 24 + 12), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(rec + 48), p + 20);
            File.WriteAllBytes(path, f.AsSpan(0, (int)p + 100).ToArray()); // the file ends where the long record does
            using var store = RecordStore.Open(path, magic);
            AudioConst.CheckNoOverlap(store.Records).Should().NotBeNull();
        }

        // ------------------------------------------------------------------ P3: the frozen loader's archive

        [Fact]
        public void The_Frozen_Loader_Releases_Its_Archive_And_Owns_Its_Music_Table()
        {
            string uo = Dir("uo-frozen");
            WriteMul(uo, Entry("a", Pcm(64, 1)));

            var first = new FrozenFiles(default, uo);
            first.Sounds.Load();
            first.Sounds.TryGetMusicData(8, out string stones, out _).Should().BeTrue("the built-in table (no Config.txt)");
            stones.Should().Be("stones2");

            // a second loader loads its own table (the static one made the built-in table's Add throw)
            var second = new FrozenFiles(default, uo);
            Action load = () => second.Sounds.Load();
            load.Should().NotThrow();

            first.Sounds.ClearResources();
            second.Sounds.ClearResources();
            second.Sounds.TryGetMusicData(8, out _, out _).Should().BeFalse();

            // with both archives released (handle and mapping), the file can be deleted
            Action delete = () => File.Delete(Path.Combine(uo, "sound.mul"));
            delete.Should().NotThrow();
        }

        // ------------------------------------------------------------------ P3: slot fields

        [Fact]
        public void Audio_Slots_With_Nonzero_Arg_Or_Reserved_Bits_Are_Left_Out()
        {
            AudioConst.CheckSoundSlot(new StoreSlot { Record = 1 }).Should().BeNull();
            AudioConst.CheckSoundSlot(new StoreSlot { Record = 1, Arg = 0xFFFF }).Should().NotBeNull("sounds have no arg");
            AudioConst.CheckSoundSlot(new StoreSlot { Record = 1, Reserved = 1 }).Should().NotBeNull();
            AudioConst.CheckMusicSlot(new StoreSlot { Record = 1, Arg = AudioConst.MusicArgLoop }).Should().BeNull();
            AudioConst.CheckMusicSlot(new StoreSlot { Record = 1, Reserved = 0x8000 }).Should().NotBeNull();

            // sounds: slot 1's arg = 0xFFFF (Codex's case) and, in a second pack, its reserved field; slot 0 still plays
            foreach (int field in new[] { 4, 6 })
            {
                string pack = GoodSoundStore(out byte[] pcm0);
                string p = Path.Combine(pack, AudioConst.SoundsFile);
                byte[] f = File.ReadAllBytes(p);
                BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(32 + 8 + field), 0xFFFF);
                File.WriteAllBytes(p, f);
                var loader = Client(Dir("e-slot" + field), pack);
                loader.TryGetSound(1, out _, out _).Should().BeFalse($"slot field at +{field} is not 0");
                Expect(loader, 0, pcm0, "a");
                loader.Dispose();
            }

            // music: slot 0's reserved field; slot 1 (the same record) still plays
            string uo = Dir("uo-mslot");
            string digital = Dir("uo-mslot/Music/Digital");
            File.WriteAllBytes(Path.Combine(digital, "t.mp3"), Mp3(20));
            File.WriteAllText(Path.Combine(digital, "Config.txt"), "0 t,loop\n1 t\n");
            string mpack = Dir("pack-mslot");
            AudioImport.WriteMusic(AudioImport.ReadMusicTable(x => Path.Combine(uo, x), uo, false, out bool b), b, mpack);
            string mp = Path.Combine(mpack, AudioConst.MusicFile);
            byte[] m = File.ReadAllBytes(mp);
            BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(32 + 6), 1);
            File.WriteAllBytes(mp, m);
            var ml = Client(Dir("e-mslot"), mpack);
            ml.TryGetMusic(0, out _, out _, out _).Should().BeFalse("reserved bits set");
            ml.TryGetMusic(1, out _, out _, out _).Should().BeTrue();
            ml.Dispose();
        }
    }
}
