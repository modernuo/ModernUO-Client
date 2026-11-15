// SPDX-License-Identifier: BSD-2-Clause

extern alias frozen;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicUO.Game.Managers;
using ClassicUO.IO.Audio;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;
using FrozenFiles = frozen::ClassicUO.Assets.UOFileManager;
using FrozenSounds = frozen::ClassicUO.Assets.SoundsLoader;
using Sound = ClassicUO.Renderer.Sounds.Sound;
using SoundCache = ClassicUO.Renderer.Sounds.SoundCache;

namespace ClassicUO.UnitTests.Assets
{
    // Codex round 3 on the audio pack (poc-log "Audio pack: Codex round 3"): one test per finding, on synthetic data.
    public sealed partial class AudioPackTests
    {
        // a pack of the given PCM entries (ids 0..n-1), and a client on it whose Sounds folder is returned
        private (ClassicUO.Assets.SoundsLoader Loader, string Sounds) PackClient(string name, params byte[][] pcm)
        {
            string uo = Dir("uo-" + name);
            WriteMul(uo, pcm.Select((p, i) => p == null ? null : Entry($"s{i}", p)).ToArray());
            string pack = Dir("pack-" + name);

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            string client = Dir("client-" + name);
            string sounds = Dir($"client-{name}/Sounds");

            return (Client(client, pack), sounds);
        }

        // ------------------------------------------------------------------ 1. silent sounds and cache admission

        [Fact]
        public void An_Audible_Request_Stops_Silent_Sounds_When_The_Cache_Is_Full_Of_Them()
        {
            const int K = 1024;
            var (loader, _) = PackClient("admit", Pcm(256 * K, 1), Pcm(256 * K, 2), Pcm(256 * K, 3), Pcm(256 * K, 4), Pcm(256 * K, 5), Pcm(512 * K, 6));
            var playing = new LinkedList<UOSound>();
            var cache = new SoundCache(loader, budget: 1 << 20, maxIds: 1024, isBusy: playing.Contains);

            // 0 and 1 silent (created in the background), 2 and 3 audible: the whole 1 MiB budget is playing
            for (int id = 0; id < 4; id++)
            {
                UOSound s = Sound.GetSoundToPlay(cache, id, playing, audible: true);
                s.Should().NotBeNull();

                if (id < 2)
                {
                    s.Volume = 0f;
                }

                playing.AddLast(s);
            }

            cache.Get(4).Should().BeNull("the cache alone refuses: everything is playing");

            // a silent request never stops anything
            Sound.GetSoundToPlay(cache, 4, playing, audible: false).Should().BeNull();
            playing.Should().HaveCount(4);

            // an audible one stops just the oldest silent sound its 256 KiB need, then is admitted
            UOSound four = Sound.GetSoundToPlay(cache, 4, playing, audible: true);
            four.Should().NotBeNull();
            playing.Select(p => p.Index).Should().Equal(1, 2, 3);
            playing.AddLast(four);
            cache.Bytes.Should().Be(1 << 20);

            // 512 KiB: stopping the one silent sound left (256 KiB) would not be enough, so nothing is stopped
            Sound.GetSoundToPlay(cache, 5, playing, audible: true).Should().BeNull();
            playing.Select(p => p.Index).Should().Equal(1, 2, 3, 4);

            // unknown ids stop nothing either
            Sound.GetSoundToPlay(cache, 77, playing, audible: true).Should().BeNull();
            playing.Should().HaveCount(4);
            loader.Dispose();
        }

        // ------------------------------------------------------------------ 2. verify's frozen loader: one bounded handle

        [Fact]
        public void The_Frozen_Loader_Reads_A_Loose_Wave_Through_One_Bounded_Handle()
        {
            FrozenSounds.MaxWaveFileBytes.Should().Be(AudioConst.MaxWaveFileBytes, "the frozen loader repeats the client's cap");

            string uo = Dir("uo-frozen-bound");
            byte[] arch = Pcm(64, 91);
            WriteMul(uo, Entry("arch", arch));
            string sounds = Dir("uo-frozen-bound/Sounds");
            string loose = Path.Combine(sounds, "0.wav");
            File.WriteAllBytes(loose, Wav(22050, 1, 16, Pcm(90, 92)));

            var fm = new FrozenFiles(default, uo);
            fm.Sounds.Load();

            // verify's size check passes, then the file grows before the frozen loader opens it again
            (byte[], string) GrowThenAsk()
            {
                using (var fs = new FileStream(loose, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    fs.SetLength(AudioConst.MaxWaveFileBytes + 1);
                }

                return fm.Sounds.TryGetSound(0, out byte[] d, out string n) ? (d, n) : (null, null);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            var (data, _) = AudioImport.ExpectedClientSound(
                loose,
                GrowThenAsk,
                () => fm.Sounds.TryGetArchiveSound(0, out byte[] d, out string n) ? (d, n) : (null, null));
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(8 << 20, "the 65 MiB file is never allocated");
            data.Should().Equal(arch, "the enlarged file is refused and the archive answers");

            // within the cap it still reads as before (the parsing is unchanged)
            File.WriteAllBytes(loose, Wav(22050, 1, 16, Pcm(90, 93)));
            fm.Sounds.TryGetSound(0, out byte[] ok, out _).Should().BeTrue();
            ok.Should().Equal(Pcm(90, 93));
            fm.Sounds.ClearResources();
        }

        // ------------------------------------------------------------------ 3. a source that grows between probe and read

        // a WAV whose data chunk (pcm) is followed by a JUNK chunk of junk bytes: growing the data chunk over it keeps the
        // file's length, so (with its write time put back) the client's stat cannot see the change
        private static byte[] WavWithJunk(byte[] pcm, int junk)
        {
            byte[] head = Wav(22050, 1, 16, pcm);
            byte[] tail = new byte[8 + junk];
            "JUNK"u8.CopyTo(tail);
            BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(4), junk);

            return head.Concat(tail).ToArray();
        }

        // rewrites the data chunk's size in place, keeping the file's length and last-write time
        private static void GrowDataChunk(string path, int newSize)
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            byte[] b = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), newSize);
            File.WriteAllBytes(path, b);
            File.SetLastWriteTimeUtc(path, written);
        }

        [Fact]
        public void A_Source_That_Grew_Since_Its_Probe_Is_Not_Read_Past_The_Admitted_Size_And_Evicts_Nothing()
        {
            const int K = 1024;
            // pack: 0 (1 MiB, playing), 1 (960 KiB, idle), 2 (64 KiB), 6 (512 KiB); budget 2 MiB
            byte[] p6 = Pcm(512 * K, 104);
            var (loader, sounds) = PackClient("grow", Pcm(1024 * K, 101), Pcm(960 * K, 102), Pcm(64 * K, 103), null, null, null, p6);
            var playing = new HashSet<UOSound>();
            var cache = new SoundCache(loader, budget: 2 << 20, maxIds: 1024, isBusy: playing.Contains);
            playing.Add(cache.Get(0));
            cache.Get(1).Should().NotBeNull();
            cache.Bytes.Should().Be(1984 * K);

            // 5.wav: 128 KiB of PCM, probed (fits only by evicting the idle id 1), then grown to 384 KiB + 8 in place
            string five = Path.Combine(sounds, "5.wav");
            File.WriteAllBytes(five, WavWithJunk(Pcm(128 * K, 105), 256 * K));
            loader.LoadOurs();
            loader.TryGetSoundSource(5, out _, out long probed).Should().BeTrue();
            probed.Should().Be(128 * K);
            int grown = 128 * K + 8 + 256 * K;
            GrowDataChunk(five, grown);

            long before = GC.GetAllocatedBytesForCurrentThread();
            cache.Get(5).Should().BeNull("the file is larger than its admitted size: not this play");
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(128 * K, "nothing is read past the cap");
            cache.Evictions.Should().Be(0, "a refused request evicts nothing");
            cache.Bytes.Should().Be(1984 * K);

            // the probe was refreshed: the next request is budgeted at the real size and plays
            loader.TryGetSoundSource(5, out _, out long refreshed).Should().BeTrue();
            refreshed.Should().Be(grown);
            UOSound s5 = cache.Get(5);
            s5.Should().NotBeNull();
            ArrayOf(s5).Should().Equal(AudioConst.ReadWaveFile(five, out _));
            cache.Evictions.Should().Be(1, "id 1 made room this time");

            // 6.wav: valid and small when probed, unreadable when read; the pack's 512 KiB record is larger than what was
            // admitted, so it is not read either, and the next request budgets the record itself
            string six = Path.Combine(sounds, "6.wav");
            File.WriteAllBytes(six, Wav(22050, 1, 16, Pcm(64 * K, 106)));
            loader.LoadOurs();
            loader.TryGetSoundSource(6, out _, out probed).Should().BeTrue();
            probed.Should().Be(64 * K);
            DateTime written = File.GetLastWriteTimeUtc(six);
            byte[] bad = File.ReadAllBytes(six);
            bad[8] = (byte)'X'; // not "WAVE"
            File.WriteAllBytes(six, bad);
            File.SetLastWriteTimeUtc(six, written);
            playing.Add(s5);
            long evictions = cache.Evictions;
            long held = cache.Bytes;

            before = GC.GetAllocatedBytesForCurrentThread();
            cache.Get(6).Should().BeNull();
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(256 * K, "the 512 KiB record is not read");
            cache.Evictions.Should().Be(evictions);
            cache.Bytes.Should().Be(held);

            UOSound s6 = cache.Get(6);
            s6.Should().NotBeNull("the pack's record, budgeted at its own size");
            ArrayOf(s6).Should().Equal(p6);
            cache.Bytes.Should().BeLessOrEqualTo(cache.Budget);
            loader.Dispose();
        }

        // ------------------------------------------------------------------ 4. a stale probe

        [Fact]
        public void A_Changed_Or_Vanished_Loose_File_Is_Probed_Again()
        {
            const int K = 1024;
            byte[] p8 = Pcm(64 * K, 112);
            var (loader, sounds) = PackClient("stale", null, null, null, null, null, null, null, Pcm(64 * K, 111), p8);
            var cache = new SoundCache(loader, budget: 1 << 20, maxIds: 1024, isBusy: _ => false);

            // too large for the cache: refused, and its size remembered
            string seven = Path.Combine(sounds, "7.wav");
            string eight = Path.Combine(sounds, "8.wav");
            File.WriteAllBytes(seven, Wav(22050, 1, 16, Pcm(1536 * K, 113)));
            File.WriteAllBytes(eight, Wav(22050, 1, 16, Pcm(1536 * K, 114)));
            loader.LoadOurs();
            cache.Get(7).Should().BeNull();
            cache.Get(8).Should().BeNull();
            cache.Refusals.Should().Be(2);

            // 7.wav replaced by a small one: the stat differs, so it is probed again and plays
            byte[] small = Pcm(32 * K, 115);
            File.WriteAllBytes(seven, Wav(22050, 1, 16, small));
            UOSound s7 = cache.Get(7);
            s7.Should().NotBeNull();
            ArrayOf(s7).Should().Equal(small);

            // 8.wav rewritten to the same length (its data chunk now declares 32 KiB) with a new write time: probed again
            byte[] b8 = File.ReadAllBytes(eight);
            BinaryPrimitives.WriteInt32LittleEndian(b8.AsSpan(40), 32 * K);
            File.WriteAllBytes(eight, b8);
            File.SetLastWriteTimeUtc(eight, DateTime.UtcNow.AddMinutes(1));
            loader.TryGetSoundSource(8, out _, out long again).Should().BeTrue();
            again.Should().Be(32 * K, "the length is the same, the write time is not");

            // 8.wav deleted: the pack's record answers
            File.Delete(eight);
            loader.TryGetSoundSource(8, out long key, out long size).Should().BeTrue();
            key.Should().BePositive("a pack record");
            size.Should().Be(p8.Length);
            UOSound s8 = cache.Get(8);
            s8.Should().NotBeNull();
            ArrayOf(s8).Should().Equal(p8);
            loader.Dispose();
        }

        // ------------------------------------------------------------------ 5. NaN and extreme arguments

        [Fact]
        public void Non_Finite_Volumes_Are_Refused_Before_Playback_State_Changes_And_Distances_Do_Not_Overflow()
        {
            var s = new Probe("nan", 1, new byte[100]);

            foreach (var (volume, factor) in new[] { (float.NaN, 0f), (1f, float.NaN), (float.PositiveInfinity, 0f), (1f, float.NegativeInfinity) })
            {
                s.Play(0, volume, factor).Should().BeFalse($"{volume}, {factor}");
                SoundInstanceField.GetValue(s).Should().BeNull("no FNA instance was created");
                s.CanPlayAt(0).Should().BeTrue("the replay delay did not start");
            }

            var music = new UOMusic(1, "m", false, () => new MemoryStream(), "test");
            music.Play(0, float.NaN).Should().BeFalse();

            AudioManager.Distance(int.MinValue, 0, 0, 0).Should().Be(2147483648L);
            AudioManager.Distance(int.MaxValue, 5, int.MinValue, 5).Should().Be(uint.MaxValue);
            AudioManager.Distance(10, 3, 7, 9).Should().Be(6);
        }
    }
}
