using System.Text;

namespace Uo.Pack;

// Sounds and music in the pack (docs/modern-client/pack-format.md, "Audio"):
//
//   audio/sounds.bin   sound effects: a record store, one record per distinct sound, raw 16-bit PCM (no decode at play)
//   audio/music.bin    music: a record store, one record per distinct MP3 file, the original bytes (decoded by dr_mp3)
//
// Both payloads end with the item's name (meta2 bytes), after the audio, so the audio starts at the payload's first
// byte. Every check here treats the pack as untrusted: a bad record is reported (an error string), never thrown, so
// the client can make it absent.

public static class AudioConst
{
    public const uint MagicSounds = 0x53444E53; // "SNDS"
    public const uint MagicMusic = 0x4353554D;  // "MUSC"

    public const string SoundsFile = "audio/sounds.bin";
    public const string MusicFile = "audio/music.bin";

    /// <summary>
    /// Ids the client addresses: 0 .. 0xFFFE (ClassicUO's MAX_SOUND_DATA_INDEX_COUNT = 0xFFFF is an exclusive bound),
    /// for sounds and music alike. A store may declare at most this many slots.
    /// </summary>
    public const int MaxIds = 0xFFFF;

    /// <summary>The only sample format a 1.0 client plays: 22,050 Hz, mono, 16-bit signed little-endian.</summary>
    public const int SoundRate = 22050;
    public const int SoundChannels = 1;

    /// <summary>Largest PCM payload of one sound: 64 MiB (25 min). The largest stock sound (beatSlow) is 21.2 MB.</summary>
    public const int MaxSoundBytes = 64 << 20;

    /// <summary>Longest stored sound name: soundidx/UOP entries carry a 40-byte name field.</summary>
    public const int MaxSoundName = 40;

    /// <summary>Largest MP3 payload: ClassicUO.IO.Audio.Mp3Validator.MaxBytes (32 MiB; the largest stock track is 12.9 MB).</summary>
    public const int MaxMusicBytes = 32 << 20;

    /// <summary>Longest stored music name (the file name, e.g. "Britain1.mp3").</summary>
    public const int MaxMusicName = 255;

    /// <summary>Sounds StoreHeader.Flags bit 0: converted from soundLegacyMUL.uop (else sound.mul/soundidx.mul).</summary>
    public const uint SoundFlagUop = 1;

    /// <summary>Music StoreHeader.Flags bit 0: the folder had no Config.txt and the client's built-in table was used.</summary>
    public const uint MusicFlagBuiltinTable = 1;

    /// <summary>Music StoreSlot.Arg bit 0: the track loops (Config.txt's "loop"). Other bits must be 0.</summary>
    public const ushort MusicArgLoop = 1;

    /// <summary>Music StoreRecord.Meta0: the payload's audio format. 1 = MP3 (the only one a 1.0 reader accepts).</summary>
    public const ushort MusicFormatMp3 = 1;

    /// <summary>Opens audio/sounds.bin: 1..<see cref="MaxIds"/> slots, checked before the tables are allocated.</summary>
    public static RecordStore OpenSoundStore(string path) => RecordStore.Open(path, MagicSounds, 1, MaxIds);

    /// <summary>Opens audio/music.bin: 1..<see cref="MaxIds"/> slots, checked before the tables are allocated.</summary>
    public static RecordStore OpenMusicStore(string path) => RecordStore.Open(path, MagicMusic, 1, MaxIds);

    /// <summary>
    /// The type checks of a sound record (the store's extents and codec are <see cref="RecordStore.Open(string, uint)"/>'s):
    /// codec 0 (raw: sounds are never compressed, so a play is one read), 22,050 Hz mono, a name of at most 40 bytes,
    /// and at most <see cref="MaxSoundBytes"/> of PCM. Null when the record is valid.
    /// </summary>
    public static string? CheckSoundRecord(in StoreRecord r)
    {
        if (r.Codec != StoreConst.CodecRaw) return $"codec {r.Codec} (sounds are raw)";
        if (r.Meta0 != SoundRate || r.Meta1 != SoundChannels) return $"{r.Meta0} Hz, {r.Meta1} channel(s) (the client plays 22050 Hz mono)";
        if (r.Meta2 > MaxSoundName) return $"name of {r.Meta2} bytes (at most {MaxSoundName})";
        if (r.RawBytes < r.Meta2) return "shorter than its name";
        if (r.RawBytes - r.Meta2 > MaxSoundBytes) return $"{r.RawBytes - r.Meta2} bytes of PCM (at most {MaxSoundBytes})";
        return null;
    }

    /// <summary>
    /// The type checks of a music record: codec 0, format MP3, meta1 0, a name of at most 255 bytes, and 1 ..
    /// <see cref="MaxMusicBytes"/> bytes of MP3. The MP3 bytes themselves are checked by Mp3Validator before decoding.
    /// </summary>
    public static string? CheckMusicRecord(in StoreRecord r)
    {
        if (r.Codec != StoreConst.CodecRaw) return $"codec {r.Codec} (music is stored as is)";
        if (r.Meta0 != MusicFormatMp3) return $"format {r.Meta0} (1.0 knows MP3 = 1)";
        if (r.Meta1 != 0) return $"meta1 {r.Meta1} (reserved)";
        if (r.Meta2 > MaxMusicName) return $"name of {r.Meta2} bytes (at most {MaxMusicName})";
        if (r.RawBytes <= r.Meta2) return "no MP3 bytes";
        if (r.RawBytes - r.Meta2 > MaxMusicBytes) return $"{r.RawBytes - r.Meta2} bytes of MP3 (at most {MaxMusicBytes})";
        return null;
    }

    /// <summary>
    /// The payloads of an audio store must not overlap: ids share content through their slots, never through records
    /// pointing into each other's bytes, so an overlap means a forged or corrupt table. The whole store is rejected.
    /// (RecordStore.Open has already checked that every payload lies after the tables and inside the file.)
    /// </summary>
    public static string? CheckNoOverlap(StoreRecord[] records)
    {
        var order = new int[records.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => records[a].Offset.CompareTo(records[b].Offset));
        for (int k = 1; k < order.Length; k++)
        {
            ref readonly var prev = ref records[order[k - 1]];
            ref readonly var cur = ref records[order[k]];
            if (prev.Offset + prev.StoredBytes > cur.Offset && cur.StoredBytes > 0 && prev.StoredBytes > 0)
                return $"records {order[k - 1]} and {order[k]} overlap";
        }
        return null;
    }

    /// <summary>Checks a music slot's arg: only <see cref="MusicArgLoop"/> may be set.</summary>
    public static string? CheckMusicSlot(in StoreSlot s) => (s.Arg & ~MusicArgLoop) != 0 ? $"unknown slot flags 0x{s.Arg:X4}" : null;

    /// <summary>Audio bytes of a record whose payload is audio then a name of meta2 bytes.</summary>
    public static int AudioBytes(in StoreRecord r) => (int)(r.RawBytes - r.Meta2);

    /// <summary>The name at the end of a payload (meta2 bytes; UTF-8, invalid bytes as U+FFFD).</summary>
    public static string Name(in StoreRecord r, ReadOnlySpan<byte> payload) =>
        r.Meta2 == 0 ? "" : Encoding.UTF8.GetString(payload.Slice(payload.Length - r.Meta2, r.Meta2));

    /// <summary>Builds a payload: the audio, then the name's bytes.</summary>
    public static byte[] Payload(ReadOnlySpan<byte> audio, ReadOnlySpan<byte> name)
    {
        var p = new byte[audio.Length + name.Length];
        audio.CopyTo(p);
        name.CopyTo(p.AsSpan(audio.Length));
        return p;
    }
}
