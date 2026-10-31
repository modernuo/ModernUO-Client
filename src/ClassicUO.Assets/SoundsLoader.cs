// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Uo.Pack;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Sound effects and music from the pack (docs/modern-client/pack-format.md, "Audio"): audio/sounds.bin holds every
    /// sound as raw 22,050 Hz mono 16-bit PCM (one positional read on first play, no decode), audio/music.bin every
    /// music id's original MP3 bytes and loop flag (streamed to dr_mp3 on the music worker). sound.mul,
    /// soundLegacyMUL.uop, Sound.def, Config.txt and the loose Music MP3s are the converter's input and are no longer
    /// read here. Loose Sounds/&lt;id&gt;.wav files still win over the pack (below).
    /// </summary>
    public sealed class SoundsLoader : UOFileLoader
    {
        /// <summary>
        /// Sounds of the shard's own, as loose files, looked at before the pack.
        ///
        /// The converter folds the client folder's Sounds/&lt;id&gt;.wav files into audio/sounds.bin, so a shipped pack
        /// needs no loose files. The folder is still read at runtime so a shard can try a sound without rebuilding the
        /// pack: anything in Sounds/&lt;id&gt;.wav wins over the pack, and any id the pack does not have becomes a sound
        /// the shard can ask for (ids up to 0xFFFE).
        /// </summary>
        private readonly Dictionary<int, string> _ours = new Dictionary<int, string>();

        public const int MAX_SOUND_DATA_INDEX_COUNT = 0xFFFF;

        private RecordStore _sounds, _music;
        // per record: false once the record failed its checks or its read (absent for the session, logged once)
        private bool[] _soundOk = Array.Empty<bool>(), _musicOk = Array.Empty<bool>();

        public SoundsLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        /// <summary>True when audio/sounds.bin is open.</summary>
        public bool HasPackSounds => _sounds != null;

        /// <summary>True when audio/music.bin is open.</summary>
        public bool HasPackMusic => _music != null;

        public override void Load() => LoadFrom(FileManager.Pack?.Root);

        /// <summary>Opens audio/sounds.bin and audio/music.bin under <paramref name="packRoot"/> (null: none) and indexes the loose sounds.</summary>
        public void LoadFrom(string packRoot)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            CloseStores();
            _sounds = OpenStore(packRoot, AudioConst.SoundsFile, AudioConst.OpenSoundStore, "only loose Sounds/*.wav files play");
            _music = OpenStore(packRoot, AudioConst.MusicFile, AudioConst.OpenMusicStore, "no music plays");
            _soundOk = CheckRecords(_sounds, "sound", static (in StoreRecord r) => AudioConst.CheckSoundRecord(r));
            _musicOk = CheckRecords(_music, "music", static (in StoreRecord r) => AudioConst.CheckMusicRecord(r));

            CheckSlots(_sounds, "sound", static (in StoreSlot s) => AudioConst.CheckSoundSlot(s));
            CheckSlots(_music, "music", static (in StoreSlot s) => AudioConst.CheckMusicSlot(s));

            LoadOurs();

            Log.Trace(
                $"Audio: pack, {(_sounds == null ? 0 : _sounds.Records.Length)} sound records, {(_music == null ? 0 : _music.Records.Length)} music records " +
                $"in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.00} ms");
        }

        private static RecordStore OpenStore(string packRoot, string file, Func<string, RecordStore> open, string without)
        {
            string path = packRoot == null ? null : Path.Combine(packRoot, file);

            if (path == null || !File.Exists(path))
            {
                Log.Warn($"Audio: the pack has no {file}; {without}");

                return null;
            }

            try
            {
                RecordStore store = open(path);

                if (AudioConst.CheckNoOverlap(store.Records) is { } overlap)
                {
                    store.Dispose();

                    throw new InvalidDataException(overlap);
                }

                return store;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is OverflowException)
            {
                Log.Error($"Audio: '{path}' is invalid ({e.Message}); {without}");

                return null;
            }
        }

        private delegate string RecordCheck(in StoreRecord r);

        private delegate string SlotCheck(in StoreSlot s);

        // a slot that breaks its type's rules (pack-format.md, "Audio") is made absent; its record still serves other ids
        private static void CheckSlots(RecordStore store, string what, SlotCheck check)
        {
            if (store == null)
            {
                return;
            }

            for (int i = 0; i < store.Slots.Length; i++)
            {
                if (store.Slots[i].Present && check(store.Slots[i]) is { } err)
                {
                    Log.Error($"Audio: {what} {i}: {err}; the id is left out");
                    store.Slots[i] = default;
                }
            }
        }

        private static bool[] CheckRecords(RecordStore store, string what, RecordCheck check)
        {
            if (store == null)
            {
                return Array.Empty<bool>();
            }

            bool[] ok = new bool[store.Records.Length];

            for (int i = 0; i < ok.Length; i++)
            {
                string err = check(store.Records[i]);
                ok[i] = err == null;

                if (err != null)
                {
                    Log.Error($"Audio: {what} record {i}: {err}; its ids are left out");
                }
            }

            return ok;
        }

        /// <summary>
        /// Index Sounds/&lt;id&gt;.wav, if the folder is there at all. One listing, at load.
        ///
        /// A File.Exists per sound played would be a disk hit every time somebody swings a sword.
        /// </summary>
        public void LoadOurs()
        {
            _ours.Clear();

            string folder = Path.Combine(FileManager.BasePath, "Sounds");

            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.wav"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)
                    && id >= 0 && id < MAX_SOUND_DATA_INDEX_COUNT)
                {
                    _ours[id] = path;
                }
            }

            if (_ours.Count > 0)
            {
                Log.Trace($"{_ours.Count} sound(s) of our own in {folder}");
            }
        }

        /// <summary>
        /// The PCM of a sound (22,050 Hz mono 16-bit) and its name: a loose Sounds/&lt;id&gt;.wav first, else the pack's
        /// record. <paramref name="data"/> is an array of exactly the PCM, in whole samples: FNA's SubmitBuffer checks the
        /// whole array's length, so a record's trailing name must not share it (bench-000049 crashed on that). A sound
        /// that fails the check is logged once and left out; it never reaches FNA.
        /// </summary>
        public bool TryGetSound(int sound, out byte[] data, out string name) => TryGetSound(sound, out data, out name, out _);

        /// <summary>
        /// Where <paramref name="sound"/> would come from, without reading it: <paramref name="key"/> names the source (a
        /// pack record, shared by its aliases, or the id's loose file) and <paramref name="pcmBytes"/> is its PCM size (-1
        /// for a loose file, known only once read). The renderer's sound cache uses it to share one array per record and
        /// to make room before a read. False when the id has no playable source.
        /// </summary>
        public bool TryGetSoundSource(int sound, out long key, out long pcmBytes)
        {
            key = 0;
            pcmBytes = -1;

            if (sound < 0)
            {
                return false;
            }

            if (_ours.ContainsKey(sound))
            {
                key = LooseKey(sound);

                return true;
            }

            RecordStore store = _sounds;

            if (store == null || !store.TryGet(sound, out StoreSlot slot, out StoreRecord rec) || !_soundOk[slot.Record - 1])
            {
                return false;
            }

            key = slot.Record;
            pcmBytes = AudioConst.AudioBytes(rec);

            return true;
        }

        // pack records are keyed 1..n (slot.Record), loose files below 0
        private static long LooseKey(int sound) => -1L - sound;

        /// <summary>
        /// <see cref="TryGetSound(int, out byte[], out string)"/>, also naming the source that served it
        /// (<see cref="TryGetSoundSource"/>'s key): ids that share a record get the same key.
        /// </summary>
        public bool TryGetSound(int sound, out byte[] data, out string name, out long key)
        {
            data = null;
            name = null;
            key = 0;

            if (sound < 0)
            {
                return false;
            }

            // Ours first, so a replacement replaces and a new id works at all.
            if (_ours.TryGetValue(sound, out string mine))
            {
                // size-capped read, overflow-safe chunk walk, playable length (the converter applies the same rules)
                byte[] wave = AudioConst.ReadWaveFile(mine, out string bad);

                if (wave == null)
                {
                    Log.Warn($"{Path.GetFileName(mine)}: {bad}; it is being ignored");
                }
                else
                {
                    data = wave;
                    name = Path.GetFileNameWithoutExtension(mine);
                    key = LooseKey(sound);

                    return true;
                }

                // unreadable or unplayable: once is enough, the pack's sound (if any) plays from now on
                _ours.Remove(sound);
            }

            RecordStore store = _sounds;

            if (store == null || !store.TryGet(sound, out StoreSlot slot, out StoreRecord rec) || !_soundOk[slot.Record - 1])
            {
                return false;
            }

            byte[] pcm;

            try
            {
                pcm = AudioConst.ReadSound(store, rec, out name);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ObjectDisposedException)
            {
                _soundOk[slot.Record - 1] = false;
                name = null;
                Log.Error($"Audio: sound {sound}: {e.Message}; left out for this session");

                return false;
            }

            // the record passed CheckSoundRecord at load; this is the last gate before FNA sees the bytes
            if (AudioConst.SubmitError(pcm, 0, pcm.Length) is { } err)
            {
                _soundOk[slot.Record - 1] = false;
                name = null;
                Log.Error($"Audio: sound {sound}: {err}; left out for this session");

                return false;
            }

            data = pcm;
            key = slot.Record;

            return true;
        }

        /// <summary>
        /// A music id's name, loop flag and an opener for its MP3 bytes: a positional stream over the pack record, opened
        /// on the music worker; Mp3Validator checks the bytes before dr_mp3 sees them.
        /// </summary>
        public bool TryGetMusic(int index, out string name, out bool loop, out Func<Stream> open)
        {
            name = null;
            loop = false;
            open = null;

            RecordStore store = _music;

            if (store == null || !store.TryGet(index, out StoreSlot slot, out StoreRecord rec) || !_musicOk[slot.Record - 1])
            {
                return false;
            }

            loop = (slot.Arg & AudioConst.MusicArgLoop) != 0;
            int mp3 = AudioConst.AudioBytes(rec);

            try
            {
                byte[] nameBytes = new byte[rec.Meta2];

                using (Stream s = store.OpenRawStream(rec, mp3, rec.Meta2))
                {
                    s.ReadExactly(nameBytes);
                }

                name = Encoding.UTF8.GetString(nameBytes);
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException)
            {
                _musicOk[slot.Record - 1] = false;
                Log.Error($"Audio: music {index}: {e.Message}; left out for this session");

                return false;
            }

            if (name.Length == 0)
            {
                name = $"music {index}";
            }

            open = () => store.OpenRawStream(rec, 0, mp3);

            return true;
        }

        private void CloseStores()
        {
            _sounds?.Dispose();
            _music?.Dispose();
            _sounds = _music = null;
        }

        public override void ClearResources()
        {
            CloseStores();
        }
    }
}
