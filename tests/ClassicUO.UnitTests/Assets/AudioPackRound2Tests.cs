// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.IO.Audio;
using FluentAssertions;
using Microsoft.Xna.Framework.Audio;
using Uo.Converter;
using Uo.Pack;
using Xunit;
using Sound = ClassicUO.Renderer.Sounds.Sound;
using SoundCache = ClassicUO.Renderer.Sounds.SoundCache;

namespace ClassicUO.UnitTests.Assets
{
    // Codex round 2 on the audio pack (poc-log "Audio pack: Codex round 2"): one test per finding, on synthetic data.
    public sealed partial class AudioPackTests
    {
        // ------------------------------------------------------------------ 1. BufferNeeded subscriptions

        private static readonly FieldInfo SoundInstanceField =
            typeof(ClassicUO.IO.Audio.Sound).GetField("SoundInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly FieldInfo BufferNeededField =
            typeof(DynamicSoundEffectInstance).GetField("BufferNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // an FNA instance without a native voice (the tests have no audio device); never finalized
        private static DynamicSoundEffectInstance FakeInstance()
        {
            var i = (DynamicSoundEffectInstance)RuntimeHelpers.GetUninitializedObject(typeof(DynamicSoundEffectInstance));
            GC.SuppressFinalize(i);

            return i;
        }

        private static int Subscriptions(DynamicSoundEffectInstance i) =>
            ((Delegate)BufferNeededField.GetValue(i))?.GetInvocationList().Length ?? 0;

        [Fact]
        public void Overlapping_Replays_Leave_Exactly_One_BufferNeeded_Subscription()
        {
            BufferNeededField.Should().NotBeNull("FNA's field-like event keeps its delegate in a field of the same name");
            var s = new Probe("replayed", 1, new byte[100]);
            var instance = FakeInstance();
            SoundInstanceField.SetValue(s, instance);
            var playing = new LinkedList<UOSound>();

            // what AudioManager does for each overlapping replay: the limiter drops the previous list entry WITHOUT Stop,
            // then Play subscribes again (Sound.Play calls SubscribeBufferNeeded before SubmitBuffer)
            for (int replay = 0; replay < 10; replay++)
            {
                Sound.LimitPlaying(playing, s).Should().BeGreaterOrEqualTo(0);
                s.SubscribeBufferNeeded();
                playing.AddLast(s);
                Subscriptions(instance).Should().Be(1, $"replay {replay}: one subscription, however often it plays");
            }

            playing.Should().ContainSingle();

            // the manager's single cleanup (Stop, which unsubscribes first) leaves none
            s.UnsubscribeBufferNeeded();
            Subscriptions(instance).Should().Be(0);
            s.UnsubscribeBufferNeeded();
            Subscriptions(instance).Should().Be(0, "idempotent both ways");

            // a new instance (the old one was disposed) moves the subscription, never duplicates it
            s.SubscribeBufferNeeded();
            var second = FakeInstance();
            SoundInstanceField.SetValue(s, second);
            s.SubscribeBufferNeeded();
            Subscriptions(instance).Should().Be(0);
            Subscriptions(second).Should().Be(1);
            s.UnsubscribeBufferNeeded();
            Subscriptions(second).Should().Be(0);
            SoundInstanceField.SetValue(s, null);
        }

        // ------------------------------------------------------------------ 2. verify and music: size before the read

        [Fact]
        public void Verify_Checks_A_Loose_Wave_Size_Before_Asking_The_Frozen_Loader()
        {
            string dir = Dir("verify-size");
            byte[] arch = Pcm(64, 41);
            (byte[], string) Archive() => (arch, "arch.wav");
            int loaderCalls = 0;
            (byte[], string) Loader()
            {
                loaderCalls++;

                return (Pcm(90, 42), "0");
            }

            // over the cap: the frozen loader (an unbounded ReadAllBytes) is never asked
            string huge = Path.Combine(dir, "0.wav");

            using (var fs = File.Create(huge))
            {
                fs.Write(Wav(22050, 1, 16, new byte[2]));
                fs.SetLength(AudioConst.MaxWaveFileBytes + 1);
            }

            AudioImport.ExpectedClientSound(huge, Loader, Archive).Data.Should().Equal(arch);
            loaderCalls.Should().Be(0, "an oversized loose file is refused from its handle's length");

            // indexed but gone: the archive, no exception
            AudioImport.ExpectedClientSound(Path.Combine(dir, "gone.wav"), Loader, Archive).Data.Should().Equal(arch);
            loaderCalls.Should().Be(0);

            // locked by another process: the client cannot open it either
            string locked = Path.Combine(dir, "1.wav");
            File.WriteAllBytes(locked, Wav(22050, 1, 16, Pcm(90, 43)));

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AudioImport.ExpectedClientSound(locked, Loader, Archive).Data.Should().Equal(arch);
            }

            loaderCalls.Should().Be(0);

            // within the cap but gone by the time the old loader opens it
            AudioImport.ExpectedClientSound(locked, () => throw new FileNotFoundException("raced"), Archive).Data.Should().Equal(arch);

            // a normal override still comes from the old loader
            AudioImport.ExpectedClientSound(locked, Loader, Archive).Data.Should().HaveCount(90);
            loaderCalls.Should().Be(1);
        }

        [Fact]
        public void An_Oversized_Mp3_Is_Refused_Without_Being_Read()
        {
            string uo = Dir("uo-bigmp3");
            string digital = Dir("uo-bigmp3/Music/Digital");
            string big = Path.Combine(digital, "big.mp3");

            using (var fs = File.Create(big))
            {
                fs.Write(Mp3(20));
                fs.SetLength(Mp3Validator.MaxBytes + 1L);
            }

            File.WriteAllBytes(Path.Combine(digital, "ok.mp3"), Mp3(20));
            File.WriteAllText(Path.Combine(digital, "Config.txt"), "0 big\n1 ok,loop\n");

            long before = GC.GetAllocatedBytesForCurrentThread();
            AudioConst.ReadFileBounded(big, Mp3Validator.MaxBytes, out string err).Should().BeNull();
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1 << 20, "nothing the size of the file is allocated");
            err.Should().Contain("exceeds");

            var warnings = new List<string>();
            before = GC.GetAllocatedBytesForCurrentThread();
            var stats = AudioImport.WriteMusic(AudioImport.ReadMusicTable(f => Path.Combine(uo, f), uo, false, out bool b), b, Dir("pack-bigmp3"), warnings.Add);
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(8 << 20, "the 32 MiB file is never read");
            stats.Invalid.Should().Be(1);
            stats.Present.Should().Be(1);
            warnings.Should().ContainSingle().Which.Should().Contain("exceeds");

            AudioConst.ReadFileBounded(Path.Combine(digital, "gone.mp3"), Mp3Validator.MaxBytes, out err).Should().BeNull();
            err.Should().NotBeNull();
        }

        // ------------------------------------------------------------------ 3. a refused loose sound is not reread

        [Fact]
        public void A_Sound_The_Cache_Cannot_Hold_Yet_Is_Refused_Before_Its_Payload_Is_Read()
        {
            // pack: id 0 (768 KiB, will be playing), id 1 (512 KiB); loose: 5.wav (512 KiB, valid), 1.wav (odd: invalid)
            string uo = Dir("uo-refuse");
            byte[] p0 = Pcm(768 << 10, 51), p1 = Pcm(512 << 10, 52), mine = Pcm(512 << 10, 53);
            WriteMul(uo, Entry("p0", p0), Entry("p1", p1));
            string pack = Dir("pack-refuse");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            string client = Dir("client-refuse");
            string sounds = Dir("client-refuse/Sounds");
            File.WriteAllBytes(Path.Combine(sounds, "5.wav"), Wav(22050, 1, 16, mine));
            File.WriteAllBytes(Path.Combine(sounds, "1.wav"), Wav(22050, 1, 16, Pcm(81, 54)));
            var loader = Client(client, pack);
            var playing = new HashSet<UOSound>();
            var cache = new SoundCache(loader, budget: 1 << 20, maxIds: 1024, isBusy: playing.Contains);
            playing.Add(cache.Get(0));

            for (int attempt = 0; attempt < 20; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                cache.Get(5).Should().BeNull("768 KiB of the 1 MiB budget is playing");
                cache.Get(1).Should().BeNull("the invalid override falls back to the pack's 512 KiB record, which does not fit either");
                (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(64 << 10, $"attempt {attempt}: no payload is read for a refusal");
            }

            cache.Evictions.Should().Be(0, "a refusal evicts nothing");
            cache.Bytes.Should().Be(768 << 10);

            // capacity changes (the sound finishes): the refused sounds play, read once each
            playing.Clear();
            UOSound five = cache.Get(5);
            five.Should().NotBeNull();
            ArrayOf(five).Should().Equal(mine);
            cache.Evictions.Should().Be(1, "the finished id 0 made room");
            UOSound one = cache.Get(1);
            one.Should().NotBeNull();
            ArrayOf(one).Should().Equal(p1, "the pack's record, not the invalid override");
            cache.Bytes.Should().BeLessOrEqualTo(cache.Budget);
            loader.Dispose();
        }

        // ------------------------------------------------------------------ 4. inaudible sounds and the playing limits

        [Fact]
        public void Silent_Sounds_Never_Stop_Audible_Ones_And_Give_Way_First()
        {
            var playing = new LinkedList<UOSound>();
            var s = Enumerable.Range(0, 4).Select(i => new Probe($"s{i}", i, new byte[100])).ToArray();
            s[1].Volume = 0f;
            s[3].Volume = 0f;
            s[1].IsAudible.Should().BeFalse();
            s[0].IsAudible.Should().BeTrue();

            foreach (Probe p in s)
            {
                Sound.LimitPlaying(playing, p, maxCount: 4, maxBytes: 1L << 20, audible: p.IsAudible).Should().Be(0);
                playing.AddLast(p);
            }

            // a silent newcomer when full: not admitted, nothing stopped
            var quiet = new Probe("quiet", 10, new byte[100]);
            Sound.LimitPlaying(playing, quiet, 4, 1L << 20, audible: false).Should().Be(-1);
            playing.Should().Equal(s.Cast<UOSound>(), "the list is untouched");

            // a silent replay of a listed sound takes no more room: admitted
            Sound.LimitPlaying(playing, s[3], 4, 1L << 20, audible: false).Should().Be(0);
            playing.AddLast(s[3]);

            // an audible newcomer stops the oldest SILENT sound, not the oldest sound
            var loud = new Probe("loud", 11, new byte[100]);
            Sound.LimitPlaying(playing, loud, 4, 1L << 20).Should().Be(1);
            playing.Should().NotContain(s[1]).And.Contain(s[0]);
            playing.AddLast(loud);

            // the byte cap: a large silent sound never evicts audible ones; a large audible one takes silent ones first
            var bigQuiet = new Probe("bigq", 12, new byte[1000]);
            Sound.LimitPlaying(playing, bigQuiet, 8, 1000, audible: false).Should().Be(-1);
            playing.Should().HaveCount(4);
            var bigLoud = new Probe("bigl", 13, new byte[700]);
            Sound.LimitPlaying(playing, bigLoud, 8, 1000).Should().Be(1, "dropping silent s3 (100) leaves 300 + 700");
            playing.Should().NotContain(s[3]);

            Sound.MaxPlayingSounds.Should().Be(256, "the count cap only guards against pathological shards (poc-log)");
        }

        // ------------------------------------------------------------------ 5. converter: one PCM copy per archive entry

        [Fact]
        public void Converting_Many_Aliases_Of_One_Big_Entry_Reads_And_Holds_It_Once()
        {
            string uo = Dir("uo-alias2");
            byte[] big = Pcm(4 << 20, 61);
            var entries = new byte[129][]; // ids 1..128 have no entry of their own: Sound.def aliases them to 0
            entries[0] = Entry("big.wav", big);
            WriteMul(uo, entries);
            File.WriteAllLines(Path.Combine(uo, "Sound.def"), Enumerable.Range(1, 128).Select(i => $"{i} {{0}} 0"));
            string pack = Dir("pack-alias2");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var stats = AudioImport.WriteSounds(src, pack);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                src.ArchiveReads.Should().Be(1, "129 ids, one entry: read once");
                allocated.Should().BeLessThan(32L << 20, "about three 4 MiB copies (entry, PCM, payload); 129 copies were 1 GiB");
                stats.Records.Should().Be(1);
                stats.Present.Should().Be(129);
                stats.Aliases.Should().Be(128);
            }

            var loader = Client(Dir("empty"), pack);
            Expect(loader, 0, big, "big.wav");
            Expect(loader, 128, big, "big.wav");
            loader.Dispose();
        }

        // ------------------------------------------------------------------ 6. reload keeps counting playing sounds

        [Fact]
        public void Sounds_Playing_Across_A_Reload_Stay_Counted_Until_They_Finish()
        {
            string uo = Dir("uo-reload");
            WriteMul(uo, Enumerable.Range(0, 6).Select(i => Entry($"s{i}", Pcm(256 << 10, 70 + i))).ToArray());
            string pack = Dir("pack-reload");

            using (var src = AudioImport.SoundSource.Open(f => Path.Combine(uo, f), uo, false))
            {
                AudioImport.WriteSounds(src, pack);
            }

            var loader = Client(Dir("empty"), pack);
            var playing = new HashSet<UOSound>();
            var cache = new SoundCache(loader, budget: 1 << 20, maxIds: 1024, isBusy: playing.Contains);
            UOSound survivor = cache.Get(0);
            playing.Add(survivor);
            cache.Get(1).Should().NotBeNull();

            cache.Clear();
            cache.Count.Should().Be(0);
            cache.Retired.Should().Be(1);
            cache.Bytes.Should().Be(256 << 10, "the playing sound's array is still held and counted");

            for (int id = 1; id <= 3; id++)
            {
                UOSound s = cache.Get(id);
                s.Should().NotBeNull();
                playing.Add(s);
            }

            cache.Bytes.Should().Be(1 << 20);
            cache.Get(4).Should().BeNull("the survivor still holds a quarter of the budget (it used to be forgotten: 1.25 MiB held)");

            playing.Remove(survivor); // it finishes
            cache.Get(4).Should().NotBeNull("the finished survivor is released");
            cache.Retired.Should().Be(0);
            cache.Evictions.Should().Be(0, "its quarter was enough");
            cache.Bytes.Should().Be(1 << 20);
            loader.Dispose();
        }

        // ------------------------------------------------------------------ 7. one handle for a loose WAV

        [Fact]
        public void A_Loose_Wave_Is_Checked_And_Read_Through_One_Handle_With_The_Same_Rules()
        {
            string dir = Dir("wav-handle");
            var cases = new Dictionary<string, byte[]>
            {
                ["good"] = Wav(22050, 1, 16, Pcm(90, 81)),
                ["odd"] = Wav(22050, 1, 16, Pcm(81, 82)),
                ["empty"] = Wav(22050, 1, 16, Array.Empty<byte>()),
                ["rate"] = Wav(44100, 1, 16, Pcm(80, 83)),
                ["short"] = new byte[20],
            };
            byte[] padded = Wav(22050, 1, 16, Pcm(64, 84)); // a LIST chunk of odd size (padded) before the data
            byte[] list = new byte[] { (byte)'L', (byte)'I', (byte)'S', (byte)'T', 3, 0, 0, 0, 1, 2, 3, 0 };
            cases["list"] = padded.Take(36).Concat(list).Concat(padded.Skip(36)).ToArray();
            byte[] trunc = Wav(22050, 1, 16, Pcm(100, 85));
            cases["clamped"] = trunc.Take(trunc.Length - 10).ToArray(); // data size runs past the end: clamped

            foreach (var (name, bytes) in cases)
            {
                string path = Path.Combine(dir, name + ".wav");
                File.WriteAllBytes(path, bytes);
                byte[] mem = AudioConst.ReadWave(bytes, out string memErr);
                byte[] file = AudioConst.ReadWaveFile(path, out string fileErr);
                (file == null).Should().Be(mem == null, name);
                fileErr.Should().Be(memErr, name);

                if (mem != null)
                {
                    file.Should().Equal(mem, name);
                    AudioConst.ProbeWaveFile(path, out int probed).Should().BeNull(name);
                    probed.Should().Be(mem.Length, name);
                }
            }

            // a PCM length the caller cannot hold: refused from the header, the payload not read
            string bigPath = Path.Combine(dir, "big.wav");
            File.WriteAllBytes(bigPath, Wav(22050, 1, 16, Pcm(4 << 20, 86)));
            long before = GC.GetAllocatedBytesForCurrentThread();
            AudioConst.ReadWaveFile(bigPath, out string err, out int declared, maxPcmBytes: 1 << 20).Should().BeNull();
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(64 << 10);
            declared.Should().Be(4 << 20);
            err.Should().Contain("at most");

            // gone, or held exclusively by another process: an error, never an exception
            AudioConst.ReadWaveFile(Path.Combine(dir, "gone.wav"), out err).Should().BeNull();
            err.Should().NotBeNull();

            using (new FileStream(bigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AudioConst.ReadWaveFile(bigPath, out err).Should().BeNull();
                err.Should().NotBeNull();
                AudioConst.ProbeWaveFile(bigPath, out _).Should().NotBeNull();
            }
        }
    }
}
