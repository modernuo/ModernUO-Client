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

            if (_music != null)
            {
                for (int i = 0; i < _music.Slots.Length; i++)
                {
                    if (_music.Slots[i].Present && AudioConst.CheckMusicSlot(_music.Slots[i]) is { } err)
                    {
                        Log.Error($"Audio: music {i}: {err}; the track is left out");
                        _music.Slots[i] = default;
                    }
                }
            }

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
        /// The samples out of a WAV, if it is one this can play.
        ///
        /// The stream these end up in is fixed at 22,050 Hz, mono, 16-bit - Sound.cs hard-codes
        /// all three - so anything else is refused rather than played. Handing it a 44,100 stereo
        /// file plays it at half speed an octave down, which sounds like a broken file rather
        /// than like a wrong header, and nobody would know where to look.
        /// </summary>
        private static byte[] ReadWave(string path)
        {
            byte[] raw;

            try
            {
                raw = File.ReadAllBytes(path);
            }
            catch (IOException e)
            {
                Log.Warn($"could not read {path}: {e.Message}");
                return null;
            }

            if (raw.Length < 44
                || raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F'
                || raw[8] != 'W' || raw[9] != 'A' || raw[10] != 'V' || raw[11] != 'E')
            {
                Log.Warn($"{Path.GetFileName(path)} is not a WAV");
                return null;
            }

            // Walk the chunks rather than assuming the layout. A WAV written by anything but the
            // simplest encoder carries LIST and fact chunks before its data.
            int at = 12;
            int channels = 0, rate = 0, bits = 0;

            while (at + 8 <= raw.Length)
            {
                string kind = Encoding.ASCII.GetString(raw, at, 4);
                int size = BitConverter.ToInt32(raw, at + 4);

                if (size < 0 || at + 8 + size > raw.Length)
                {
                    size = raw.Length - at - 8;
                }

                if (kind == "fmt " && size >= 16)
                {
                    channels = BitConverter.ToInt16(raw, at + 10);
                    rate = BitConverter.ToInt32(raw, at + 12);
                    bits = BitConverter.ToInt16(raw, at + 22);
                }
                else if (kind == "data")
                {
                    if (channels != 1 || rate != 22050 || bits != 16)
                    {
                        Log.Warn(
                            $"{Path.GetFileName(path)} is {rate}Hz {channels}ch {bits}-bit; " +
                            "it has to be 22050 mono 16-bit, so it is being ignored");

                        return null;
                    }

                    byte[] body = new byte[size];
                    Buffer.BlockCopy(raw, at + 8, body, 0, size);

                    return body;
                }

                at += 8 + size + (size & 1);         // chunks are padded to an even length
            }

            Log.Warn($"{Path.GetFileName(path)} has no data chunk");

            return null;
        }

        /// <summary>
        /// The PCM of a sound (22,050 Hz mono 16-bit) and its name: a loose Sounds/&lt;id&gt;.wav first, else the pack's
        /// record, read whole with one positional read (the name trails the PCM in the same array).
        /// </summary>
        public bool TryGetSound(int sound, out ArraySegment<byte> data, out string name)
        {
            data = default;
            name = null;

            if (sound < 0)
            {
                return false;
            }

            // Ours first, so a replacement replaces and a new id works at all.
            if (_ours.TryGetValue(sound, out string mine))
            {
                byte[] wave = ReadWave(mine);

                if (wave != null)
                {
                    data = wave;
                    name = Path.GetFileNameWithoutExtension(mine);

                    return true;
                }
            }

            RecordStore store = _sounds;

            if (store == null || !store.TryGet(sound, out StoreSlot slot, out StoreRecord rec) || !_soundOk[slot.Record - 1])
            {
                return false;
            }

            byte[] payload = new byte[rec.RawBytes];

            try
            {
                store.Read(rec, payload);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ObjectDisposedException)
            {
                _soundOk[slot.Record - 1] = false;
                Log.Error($"Audio: sound {sound}: {e.Message}; left out for this session");

                return false;
            }

            name = AudioConst.Name(rec, payload);
            data = new ArraySegment<byte>(payload, 0, AudioConst.AudioBytes(rec));

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
