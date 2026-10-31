// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.IO.Audio;
using FluentAssertions;
using Microsoft.Xna.Framework.Audio;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Sounds and music in the pack (pack-format.md, "Audio"): the converter's importer on synthetic sound.mul /
    // soundidx.mul / Sound.def / Sounds/*.wav / Music/Digital files, the stores read back by the client's SoundsLoader,
    // hostile stores rejected before allocation or record by record, and a looping track played gaplessly from a pack
    // record through MusicStream. No UO data needed.
    public sealed class AudioPackTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cuo-audiopack-" + Guid.NewGuid().ToString("N"));

        public AudioPackTests() => Directory.CreateDirectory(_tmp);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tmp, true);
            }
            catch (IOException)
            {
            }
        }

        private string Dir(string name)
        {
            string d = Path.Combine(_tmp, name);
            Directory.CreateDirectory(d);

            return d;
        }

        private static byte[] Pcm(int bytes, int seed)
        {
            byte[] b = new byte[bytes];
            new Random(seed).NextBytes(b);

            return b;
        }

        // soundidx.mul (offset, length, extra) + sound.mul (40-byte name, PCM). null = no entry (offset 0xFFFFFFFF).
        private static void WriteMul(string dir, params byte[][] entries)
        {
            using var mul = new MemoryStream();
            using var idx = new MemoryStream();

            foreach (byte[] e in entries)
            {
                Span<byte> rec = stackalloc byte[12];

                if (e == null)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(rec, 0xFFFFFFFF);
                    BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(4), -1);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)mul.Length);
                    BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(4), e.Length);
                    mul.Write(e);
                }

                idx.Write(rec);
            }

            File.WriteAllBytes(Path.Combine(dir, "sound.mul"), mul.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "soundidx.mul"), idx.ToArray());
        }

        private static byte[] Entry(string name, byte[] pcm)
        {
            byte[] e = new byte[40 + pcm.Length];
            Encoding.ASCII.GetBytes(name).CopyTo(e, 0);
            pcm.CopyTo(e, 40);

            return e;
        }

        private static byte[] Wav(int rate, int channels, int bits, byte[] pcm)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * bits / 8);
            w.Write((short)(channels * bits / 8));
            w.Write((short)bits);
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);

            return ms.ToArray();
        }

        private static SoundsLoader Client(string basePath, string packRoot)
        {
            var loader = new SoundsLoader(new UOFileManager(default, basePath, null));
            loader.LoadFrom(packRoot);

            return loader;
        }

        // A silent MPEG-1 Layer III stream (128 kbps, 44.1 kHz, joint stereo; 417-byte frames).
        private static byte[] Mp3(int frames)
        {
            byte[] d = new byte[frames * 417];

            for (int i = 0; i < frames; i++)
            {
                d[i * 417] = 0xFF;
                d[i * 417 + 1] = 0xFB;
                d[i * 417 + 2] = 0x90;
                d[i * 417 + 3] = 0x44;
            }

            return d;
        }

        // ------------------------------------------------------------------ converter on a synthetic MUL

        [Fact]
        public void Converter_Imports_A_Synthetic_Mul_With_SoundDef_Aliases_And_Loose_Waves()
        {
            string uo = Dir("uo");
            byte[] a = Pcm(100, 1), b = Pcm(52, 2), c = Pcm(2000, 3), d = Pcm(64, 4);
            WriteMul(
                uo,
                Entry("zero.wav", a), // 0
                null,                 // 1: no data; Sound.def aliases it to 3
                new byte[30],         // 2: shorter than the 40-byte name: no PCM, absent
                Entry("three.wav", b), // 3
                null,                 // 4: Sound.def -1 keeps it empty
                Entry("five.wav", c)  // 5: has data, so its Sound.def line is skipped
            );
            File.WriteAllText(Path.Combine(uo, "Sound.def"), "1 {3} 0\n4 {-1} 0\n5 {0} 0\n");
            string sounds = Dir("uo/Sounds");
            File.WriteAllBytes(Path.Combine(sounds, "0.wav"), Wav(44100, 1, 16, Pcm(10, 9))); // wrong rate: the archive's 0 stays
            File.WriteAllBytes(Path.Combine(sounds, "7.wav"), Wav(22050, 1, 16, d));          // a new id

            string pack = Dir("pack");
            AudioImport.SoundStats stats;

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, uopInstallation: false))
            {
                stats = AudioImport.WriteSounds(src, pack);
            }

            stats.Uop.Should().BeFalse();
            stats.Slots.Should().Be(8, "slots end at the highest present id");
            stats.Present.Should().Be(5, "ids 0, 1, 3, 5, 7");
            stats.Records.Should().Be(4);
            stats.Aliases.Should().Be(1);
            stats.Ours.Should().Be(1);
            stats.Empty.Should().Be(1, "id 2 has an entry without PCM");

            using (var store = AudioConst.OpenSoundStore(Path.Combine(pack, AudioConst.SoundsFile)))
            {
                store.Header.Flags.Should().Be(0u, "converted from sound.mul");
                store.Slots[1].Record.Should().Be(store.Slots[3].Record, "the Sound.def alias shares the record");
                AudioConst.CheckNoOverlap(store.Records).Should().BeNull();
                store.Records.Should().OnlyContain(r => AudioConst.CheckSoundRecord(r) == null);
            }

            // the client reads it back (a base folder without Sounds/, so only the pack serves)
            var loader = Client(Dir("empty"), pack);
            loader.HasPackSounds.Should().BeTrue();
            Expect(loader, 0, a, "zero.wav");
            Expect(loader, 1, b, "three.wav");
            Expect(loader, 3, b, "three.wav");
            Expect(loader, 5, c, "five.wav");
            Expect(loader, 7, d, "7");

            foreach (int absent in new[] { 2, 4, 6, 8, 0xFFFE, 0xFFFF, -1 })
            {
                loader.TryGetSound(absent, out _, out _).Should().BeFalse($"id {absent} has no sound");
            }

            loader.Dispose();
        }

        private static void Expect(SoundsLoader loader, int id, byte[] pcm, string name)
        {
            loader.TryGetSound(id, out byte[] data, out string n).Should().BeTrue($"id {id} is present");
            data.Length.Should().Be(pcm.Length, $"id {id}: the array is exactly the PCM (FNA checks the whole array)");
            data.AsSpan().SequenceEqual(pcm).Should().BeTrue($"id {id}'s PCM is byte-exact");
            n.Should().Be(name);
        }

        [Fact]
        public void A_Loose_Wave_Still_Wins_Over_The_Pack_At_Runtime()
        {
            string uo = Dir("uo");
            byte[] a = Pcm(100, 1), mine = Pcm(80, 5);
            WriteMul(uo, Entry("zero", a));
            string pack = Dir("pack");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            string client = Dir("client");
            Directory.CreateDirectory(Path.Combine(client, "Sounds"));
            File.WriteAllBytes(Path.Combine(client, "Sounds", "0.wav"), Wav(22050, 1, 16, mine));
            var loader = Client(client, pack);
            Expect(loader, 0, mine, "0");
            loader.Dispose();
        }

        // ------------------------------------------------------------------ music

        [Fact]
        public void Music_Store_Round_Trip_Keeps_The_Mp3_Bytes_The_Loop_Flag_And_Shares_Files()
        {
            string uo = Dir("uo");
            string digital = Dir("uo/Music/Digital");
            byte[] track = Mp3(20);
            File.WriteAllBytes(Path.Combine(digital, "Track.mp3"), track);
            File.WriteAllBytes(Path.Combine(digital, "bad.mp3"), Enumerable.Repeat((byte)0x5A, 5000).ToArray());
            File.WriteAllText(Path.Combine(digital, "Config.txt"), "0 track,loop\n1 TRACK\n2 missing,loop\n3 bad,loop\nnot a line with many words here\n5 track\n");

            var table = AudioImport.ReadMusicTable(f => Path.Combine(uo, f), uo, legacyConfig: false, out bool builtin);
            builtin.Should().BeFalse();
            string pack = Dir("pack");
            var stats = AudioImport.WriteMusic(table, builtin, pack);
            stats.Present.Should().Be(3, "ids 0, 1 and 5 resolve to Track.mp3");
            stats.Records.Should().Be(1, "one file, one record");
            stats.Missing.Should().Be(1);
            stats.Invalid.Should().Be(1, "bad.mp3 fails Mp3Validator");
            stats.Looping.Should().Be(1);

            var loader = Client(Dir("empty"), pack);
            loader.HasPackMusic.Should().BeTrue();
            loader.TryGetMusic(0, out string name, out bool loop, out Func<Stream> open).Should().BeTrue();
            name.Should().Be("Track.mp3");
            loop.Should().BeTrue();

            using (Stream s = open())
            {
                s.Length.Should().Be(track.Length, "the stream covers the MP3 bytes, not the trailing name");
                byte[] read = new byte[s.Length];
                s.ReadExactly(read);
                read.Should().Equal(track);
            }

            loader.TryGetMusic(1, out _, out bool loop1, out _).Should().BeTrue();
            loop1.Should().BeFalse("the loop flag belongs to the id, not the file");
            loader.TryGetMusic(2, out _, out _, out _).Should().BeFalse("no file");
            loader.TryGetMusic(3, out _, out _, out _).Should().BeFalse("rejected by Mp3Validator at conversion");
            loader.TryGetMusic(4, out _, out _, out _).Should().BeFalse("never configured");
            loader.Dispose();
        }

        [Fact]
        public void Old_Clients_Without_Config_Get_The_Builtin_Table()
        {
            string uo = Dir("uo");
            string music = Dir("uo/Music");
            File.WriteAllBytes(Path.Combine(music, "britain1.mp3"), Mp3(5));
            var table = AudioImport.ReadMusicTable(f => Path.Combine(uo, f), uo, legacyConfig: true, out bool builtin);
            builtin.Should().BeTrue();
            table.Should().HaveCount(67);
            var stats = AudioImport.WriteMusic(table, builtin, Dir("pack"));
            stats.Present.Should().Be(1);
            stats.Looping.Should().Be(1, "britain1 loops in the built-in table");
        }

        // Gapless: a looping track streamed from a pack record wraps sample-exactly, as from a file.
        [Fact]
        public void A_Track_From_The_Pack_Loops_Gaplessly_Through_MusicStream()
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "IO", "Fixtures", "seam-noise.mp3");
            string uo = Dir("uo");
            string digital = Dir("uo/Music/Digital");
            File.Copy(fixture, Path.Combine(digital, "seam.mp3"));
            File.WriteAllText(Path.Combine(digital, "Config.txt"), "9 seam,loop\n");
            string pack = Dir("pack");
            AudioImport.WriteMusic(AudioImport.ReadMusicTable(f => Path.Combine(uo, f), uo, false, out bool b), b, pack);

            Mp3Decoder.TryOpenFile(fixture, out Mp3Decoder d, out string error).Should().BeTrue(error);
            short[] once;

            using (d)
            {
                once = new short[d.TotalFrames * d.Channels];
                int got = 0, n;

                while ((n = d.Read(once.AsSpan(got))) > 0)
                {
                    got += n * d.Channels;
                }
            }

            var loader = Client(Dir("empty"), pack);
            loader.TryGetMusic(9, out string name, out bool loop, out Func<Stream> open).Should().BeTrue();
            loop.Should().BeTrue();
            MusicStream s = new MusicStream(open, "pack music 9", name, loop);
            List<short> played = new List<short>();

            while (played.Count < once.Length + 50000)
            {
                s.Pump().Should().BeTrue(s.Error);

                while (s.TryDequeue(out byte[] buf, out int bytes))
                {
                    played.AddRange(MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, bytes)).ToArray());
                    s.Recycle(buf);
                }
            }

            s.Cancel();
            s.Pump();
            loader.Dispose();
            s.Loops.Should().BeGreaterThan(0);
            played.Take(once.Length).SequenceEqual(once).Should().BeTrue("the first lap is the whole track");
            played.Skip(once.Length).Take(40000).SequenceEqual(once.Take(40000)).Should().BeTrue("after the seam the track starts again");
        }

        // ------------------------------------------------------------------ hostile stores

        private string GoodSoundStore(out byte[] pcm0)
        {
            string uo = Dir("uo-" + Guid.NewGuid().ToString("N"));
            pcm0 = Pcm(200, 7);
            WriteMul(uo, Entry("a", pcm0), Entry("b", Pcm(300, 8)));
            string pack = Dir("pack-" + Guid.NewGuid().ToString("N"));

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            return pack;
        }

        // Offsets in the file: FileHeader 16, StoreHeader 16 (slotCount @16, recordCount @20), slots 8 each, records 24 each.
        private static void Patch(string pack, string file, Action<byte[], int> patch)
        {
            string p = Path.Combine(pack, file);
            byte[] f = File.ReadAllBytes(p);
            int slots = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(16));
            patch(f, 32 + slots * 8); // record table start
            File.WriteAllBytes(p, f);
        }

        [Fact]
        public void A_Truncated_Store_Is_Rejected_As_A_Whole()
        {
            string pack = GoodSoundStore(out _);
            string p = Path.Combine(pack, AudioConst.SoundsFile);
            byte[] f = File.ReadAllBytes(p);
            File.WriteAllBytes(p, f.AsSpan(0, 40).ToArray()); // the slot and record tables run past the end

            var loader = Client(Dir("empty"), pack);
            loader.HasPackSounds.Should().BeFalse();
            loader.TryGetSound(0, out _, out _).Should().BeFalse();
            loader.Dispose();

            File.WriteAllBytes(p, f.AsSpan(0, f.Length - 10).ToArray()); // the last payload runs past the end
            Client(Dir("empty2"), pack).HasPackSounds.Should().BeFalse();
        }

        [Fact]
        public void A_Header_Asking_For_Too_Many_Slots_Is_Rejected_Before_Allocation()
        {
            string pack = GoodSoundStore(out _);
            Patch(pack, AudioConst.SoundsFile, (f, _) => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), AudioConst.MaxIds + 1));
            Action open = () => AudioConst.OpenSoundStore(Path.Combine(pack, AudioConst.SoundsFile)).Dispose();
            open.Should().Throw<InvalidDataException>().WithMessage("*slots*");

            Patch(pack, AudioConst.SoundsFile, (f, _) => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 1u << 24));
            open.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Overlapping_Or_Out_Of_Range_Payloads_Reject_The_Store()
        {
            string pack = GoodSoundStore(out _);
            // record 1 starts inside record 0
            Patch(pack, AudioConst.SoundsFile, (f, rec) =>
            {
                ulong off0 = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(rec));
                BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(rec + 24), off0 + 10);
            });
            Client(Dir("e1"), pack).HasPackSounds.Should().BeFalse("overlapping payloads");

            pack = GoodSoundStore(out _);
            // record 0 points into the tables
            Patch(pack, AudioConst.SoundsFile, (f, rec) => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(rec), 16));
            Client(Dir("e2"), pack).HasPackSounds.Should().BeFalse("a payload inside the tables");

            pack = GoodSoundStore(out _);
            // record 0 claims more bytes than the file has (stored = raw, so the codec check passes)
            Patch(pack, AudioConst.SoundsFile, (f, rec) =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 8), 0x7FFF0000);
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 12), 0x7FFF0000);
            });
            Client(Dir("e3"), pack).HasPackSounds.Should().BeFalse("an oversized record");
        }

        [Fact]
        public void A_Bad_Record_Is_Left_Out_And_The_Others_Still_Play()
        {
            string pack = GoodSoundStore(out byte[] pcm0);
            // record 1: 44.1 kHz (the client plays 22,050 Hz only)
            Patch(pack, AudioConst.SoundsFile, (f, rec) => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(rec + 24 + 18), 44100));
            var loader = Client(Dir("e"), pack);
            loader.HasPackSounds.Should().BeTrue();
            Expect(loader, 0, pcm0, "a");
            loader.TryGetSound(1, out _, out _).Should().BeFalse();
            loader.Dispose();

            // the type checks one by one
            var ok = new StoreRecord { RawBytes = 110, StoredBytes = 110, Meta0 = 22050, Meta1 = 1, Meta2 = 10 };
            AudioConst.CheckSoundRecord(ok).Should().BeNull();
            AudioConst.CheckSoundRecord(ok with { Codec = StoreConst.CodecBrotli }).Should().NotBeNull("sounds are never compressed");
            AudioConst.CheckSoundRecord(ok with { Meta1 = 2 }).Should().NotBeNull("stereo");
            AudioConst.CheckSoundRecord(ok with { Meta2 = 41 }).Should().NotBeNull("name longer than 40");
            AudioConst.CheckSoundRecord(ok with { RawBytes = 5, StoredBytes = 5 }).Should().NotBeNull("shorter than its name");
            AudioConst.CheckSoundRecord(ok with { RawBytes = AudioConst.MaxSoundBytes + 11u }).Should().NotBeNull("too large");
            AudioConst.CheckSoundRecord(ok with { RawBytes = 111, StoredBytes = 111 }).Should().NotBeNull("odd PCM length");
            AudioConst.CheckSoundRecord(ok with { RawBytes = 10, StoredBytes = 10 }).Should().NotBeNull("no PCM");

            var m = new StoreRecord { RawBytes = 1000, StoredBytes = 1000, Meta0 = AudioConst.MusicFormatMp3, Meta2 = 8 };
            AudioConst.CheckMusicRecord(m).Should().BeNull();
            AudioConst.CheckMusicRecord(m with { Meta0 = 2 }).Should().NotBeNull("unknown format");
            AudioConst.CheckMusicRecord(m with { Meta1 = 1 }).Should().NotBeNull("reserved");
            AudioConst.CheckMusicRecord(m with { RawBytes = 8 }).Should().NotBeNull("no MP3 bytes");
            AudioConst.CheckMusicRecord(m with { RawBytes = AudioConst.MaxMusicBytes + 9u }).Should().NotBeNull("too large");
            AudioConst.CheckMusicSlot(new StoreSlot { Record = 1, Arg = 2 }).Should().NotBeNull("unknown slot flag");
        }

        [Fact]
        public void A_Music_Record_With_A_Bad_Mp3_Header_Fails_The_Stream_Cleanly()
        {
            // a store whose record passes the store checks but whose bytes are not MP3 (a pack the converter did not write)
            byte[] junk = Enumerable.Repeat((byte)0x5A, 10000).ToArray();
            var w = new RecordStoreWriter(3);
            byte[] payload = AudioConst.Payload(junk, "x.mp3"u8);
            w.Slots[2] = new StoreSlot { Record = (uint)w.Add(payload, (uint)payload.Length, StoreConst.CodecRaw, AudioConst.MusicFormatMp3, 0, 5) + 1, Arg = AudioConst.MusicArgLoop };
            w.Slots[1] = new StoreSlot { Record = 1, Arg = 0x8000 }; // unknown slot flag: left out
            string pack = Dir("pack");
            w.Write(Path.Combine(pack, AudioConst.MusicFile), AudioConst.MagicMusic, 0);

            var loader = Client(Dir("empty"), pack);
            loader.TryGetMusic(1, out _, out _, out _).Should().BeFalse();
            loader.TryGetMusic(2, out string name, out bool loop, out Func<Stream> open).Should().BeTrue();
            var s = new MusicStream(open, "pack music 2", name, loop);
            s.Pump().Should().BeFalse();
            s.IsFailed.Should().BeTrue();
            s.Error.Should().NotBeNullOrEmpty("Mp3Validator refuses it before dr_mp3 sees it");
            loader.Dispose();
        }

        [Fact]
        public void No_Audio_In_The_Pack_Means_Silence_Not_A_Crash()
        {
            var loader = Client(Dir("empty"), Dir("pack"));
            loader.HasPackSounds.Should().BeFalse();
            loader.HasPackMusic.Should().BeFalse();
            loader.TryGetSound(0, out _, out _).Should().BeFalse();
            loader.TryGetMusic(0, out _, out _, out _).Should().BeFalse();
            Client(Dir("empty2"), null).HasPackSounds.Should().BeFalse();
        }

        // ------------------------------------------------------------------ what reaches FNA (bench-000049)

        // UOSound's buffer as Sound.Play hands it to DynamicSoundEffectInstance.SubmitBuffer(array, offset, count).
        private sealed class Probe : UOSound
        {
            public Probe(string name, int index, byte[] pcm) : base(name, index, pcm)
            {
            }

            public ArraySegment<byte> Submitted => GetBuffer();
        }

        [Fact]
        public void A_Real_Shaped_Record_Reaches_FNA_As_Exactly_Its_Pcm()
        {
            // bench-000049's crash: sound 299 "feet12a.wav" (an 11-byte name, so PCM + name is odd), 17,980 bytes of PCM.
            // The client handed FNA the record's whole array (PCM then name) with count = PCM; FNA checks the array's
            // length against the block alignment and threw.
            string uo = Dir("uo");
            byte[] pcm = Pcm(17980, 299);
            WriteMul(uo, Entry("feet12a.wav", pcm));
            string pack = Dir("pack");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack).Unplayable.Should().Be(0);
            }

            var loader = Client(Dir("empty"), pack);
            loader.TryGetSound(0, out byte[] data, out string name).Should().BeTrue();
            name.Should().Be("feet12a.wav");

            var probe = new Probe(name, 0, data);
            ArraySegment<byte> sent = probe.Submitted;
            sent.Offset.Should().Be(0);
            sent.Count.Should().Be(17980);
            sent.Array!.Length.Should().Be(17980, "nothing after the samples shares the array FNA checks");
            sent.AsSpan().SequenceEqual(pcm).Should().BeTrue();
            ClassicUO.IO.Audio.Sound.CanSubmit(sent.Array, sent.Offset, sent.Count, AudioChannels.Mono).Should().BeTrue();
            AudioConst.SubmitError(sent.Array, sent.Offset, sent.Count).Should().BeNull();

            // the old shape (the record's payload array: PCM + 11 name bytes) is what FNA refused; both mirrors refuse it
            byte[] old = AudioConst.Payload(pcm, Encoding.UTF8.GetBytes("feet12a.wav"));
            ClassicUO.IO.Audio.Sound.CanSubmit(old, 0, 17980, AudioChannels.Mono).Should().BeFalse();
            AudioConst.SubmitError(old, 0, 17980).Should().NotBeNull();
            loader.Dispose();
        }

        [Fact]
        public void The_Two_Mirrors_Of_FNAs_Submit_Rules_Agree()
        {
            var cases = new (byte[] a, int off, int count)[]
            {
                (new byte[100], 0, 100), (new byte[101], 0, 100), (new byte[100], 0, 99), (new byte[100], 1, 98),
                (new byte[100], 2, 98), (new byte[100], 2, 100), (new byte[0], 0, 0), (null, 0, 0), (new byte[100], 100, 0),
            };

            foreach (var (a, off, count) in cases)
            {
                ClassicUO.IO.Audio.Sound.CanSubmit(a, off, count, AudioChannels.Mono).Should().Be(
                    AudioConst.SubmitError(a, off, count) == null, $"array {a?.Length}, offset {off}, count {count}");
            }

            ClassicUO.IO.Audio.Sound.CanSubmit(new byte[100], 0, 100, AudioChannels.Mono).Should().BeTrue();
            ClassicUO.IO.Audio.Sound.CanSubmit(new byte[101], 0, 100, AudioChannels.Mono).Should().BeFalse("FNA checks the array's length");
            ClassicUO.IO.Audio.Sound.CanSubmit(new byte[102], 0, 102, AudioChannels.Stereo).Should().BeFalse("stereo frames are 4 bytes");
        }

        [Fact]
        public void Bad_Sound_Records_Are_Refused_Before_FNA_And_The_Rest_Play()
        {
            string pack = GoodSoundStore(out _);
            // record 0 one byte shorter (stored and raw): its PCM is now odd
            Patch(pack, AudioConst.SoundsFile, (f, rec) =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 8), BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(rec + 8)) - 1);
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 12), BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(rec + 12)) - 1);
            });
            var loader = Client(Dir("e"), pack);
            loader.HasPackSounds.Should().BeTrue("a bad record leaves the store open");
            Action get0 = () => loader.TryGetSound(0, out _, out _).Should().BeFalse("odd PCM");
            get0.Should().NotThrow();
            loader.TryGetSound(1, out byte[] one, out _).Should().BeTrue("the other record still plays");
            one.Length.Should().Be(300);
            loader.Dispose();

            // record 1: no PCM at all (raw = the name's length)
            pack = GoodSoundStore(out byte[] pcm0);
            Patch(pack, AudioConst.SoundsFile, (f, rec) =>
            {
                ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(rec + 24 + 22));
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 24 + 8), nameLen);
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(rec + 24 + 12), nameLen);
            });
            loader = Client(Dir("e1"), pack);
            Action get1 = () => loader.TryGetSound(1, out _, out _).Should().BeFalse("no PCM");
            get1.Should().NotThrow();
            Expect(loader, 0, pcm0, "a");
            loader.Dispose();

            // the converter leaves out what the client would refuse, and the rest still play
            string uo = Dir("uo-odd");
            byte[] good = Pcm(64, 1);
            WriteMul(uo, Entry("odd", Pcm(201, 2)), Entry("good", good));
            string pack2 = Dir("pack-odd");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                var warnings = new List<string>();
                var stats = AudioImport.WriteSounds(src, pack2, warnings.Add);
                stats.Unplayable.Should().Be(1);
                stats.Present.Should().Be(1);
                warnings.Should().ContainSingle().Which.Should().Contain("sound 0");
            }

            loader = Client(Dir("e2"), pack2);
            loader.TryGetSound(0, out _, out _).Should().BeFalse();
            Expect(loader, 1, good, "good");
            loader.Dispose();
        }

        [Fact]
        public void An_Odd_Length_Loose_Wave_Is_Ignored_And_The_Pack_Sound_Plays()
        {
            string pack = GoodSoundStore(out byte[] pcm0);
            string client = Dir("client");
            Directory.CreateDirectory(Path.Combine(client, "Sounds"));
            File.WriteAllBytes(Path.Combine(client, "Sounds", "0.wav"), Wav(22050, 1, 16, Pcm(81, 5)));
            var loader = Client(client, pack);
            Expect(loader, 0, pcm0, "a");
            Expect(loader, 0, pcm0, "a");
            loader.Dispose();
        }
    }
}
