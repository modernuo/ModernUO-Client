using System.Buffers.Binary;
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

    /// <summary>Bytes per sample frame of that format (FNA's nBlockAlign: channels x 2).</summary>
    public const int SoundBlockAlign = SoundChannels * 2;

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
        return CheckPcmLength((long)r.RawBytes - r.Meta2);
    }

    /// <summary>
    /// A PCM length the client can play: 1 .. <see cref="MaxSoundBytes"/> bytes, whole sample frames
    /// (<see cref="SoundBlockAlign"/>). Null when it is playable.
    /// </summary>
    public static string? CheckPcmLength(long bytes)
    {
        if (bytes <= 0) return "no PCM";
        if (bytes > MaxSoundBytes) return $"{bytes} bytes of PCM (at most {MaxSoundBytes})";
        if (bytes % SoundBlockAlign != 0) return $"{bytes} bytes of PCM is not a whole number of {SoundBlockAlign}-byte samples";
        return null;
    }

    /// <summary>
    /// FNA's DynamicSoundEffectInstance.SubmitBuffer(array, offset, count) argument rules, which it enforces by
    /// throwing ArgumentException: the WHOLE array's length (not just count) must be a non-zero multiple of the
    /// block alignment, and so must offset and count, with offset + count inside the array. Null when FNA accepts it.
    /// </summary>
    public static string? SubmitError(byte[]? array, int offset, int count, int blockAlign = SoundBlockAlign)
    {
        if (array == null || array.Length == 0 || array.Length % blockAlign != 0)
            return $"array of {array?.Length ?? 0} bytes is not a non-zero multiple of {blockAlign}";
        if ((uint)offset >= (uint)array.Length || offset % blockAlign != 0) return $"offset {offset}";
        if (count <= 0 || (uint)(offset + count) > (uint)array.Length || count % blockAlign != 0) return $"count {count}";
        return null;
    }

    /// <summary>
    /// The client's decode of a sound record (a record that passed <see cref="CheckSoundRecord"/>): the PCM in an array
    /// of exactly its length (FNA checks the whole array, so the trailing name must not share it) and the name.
    /// Two positional reads. Throws IOException / EndOfStreamException on a short file.
    /// </summary>
    public static byte[] ReadSound(RecordStore store, in StoreRecord rec, out string name)
    {
        int pcmBytes = AudioBytes(rec);
        var pcm = new byte[pcmBytes];
        store.ReadRaw(rec, 0, pcm);
        Span<byte> nameBytes = stackalloc byte[MaxSoundName];
        nameBytes = nameBytes.Slice(0, rec.Meta2);
        store.ReadRaw(rec, pcmBytes, nameBytes);
        name = Encoding.UTF8.GetString(nameBytes);
        return pcm;
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
    /// (RecordStore.Open has already checked that every payload lies after the tables and inside the file.) The sweep
    /// keeps the furthest end of all earlier non-empty payloads, so an empty record between two overlapping ones (or a
    /// short one inside a long one) cannot hide the overlap; empty records occupy no bytes and never reset it.
    /// </summary>
    public static string? CheckNoOverlap(StoreRecord[] records)
    {
        var order = new int[records.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => records[a].Offset.CompareTo(records[b].Offset));
        ulong end = 0;
        int endRecord = -1;
        foreach (int k in order)
        {
            ref readonly var cur = ref records[k];
            if (cur.StoredBytes == 0) continue;
            if (endRecord >= 0 && cur.Offset < end) return $"records {endRecord} and {k} overlap";
            // Open checked StoredBytes <= fileLength - Offset, so this cannot wrap
            ulong curEnd = cur.Offset + cur.StoredBytes;
            if (endRecord < 0 || curEnd > end)
            {
                end = curEnd;
                endRecord = k;
            }
        }
        return null;
    }

    /// <summary>
    /// Sound StoreSlot checks: <c>arg</c> and <c>reserved</c> must be 0 (pack-format.md, "Audio"). A slot that fails is
    /// absent; the record and its other ids are unaffected.
    /// </summary>
    public static string? CheckSoundSlot(in StoreSlot s) =>
        s.Arg != 0 ? $"slot arg 0x{s.Arg:X4} (sounds have none)" : s.Reserved != 0 ? $"slot reserved 0x{s.Reserved:X4}" : null;

    /// <summary>Music StoreSlot checks: only <see cref="MusicArgLoop"/> may be set in <c>arg</c>, and <c>reserved</c> must be 0.</summary>
    public static string? CheckMusicSlot(in StoreSlot s) =>
        (s.Arg & ~MusicArgLoop) != 0 ? $"unknown slot flags 0x{s.Arg:X4}" : s.Reserved != 0 ? $"slot reserved 0x{s.Reserved:X4}" : null;

    /// <summary>Largest loose WAV file read at all: <see cref="MaxSoundBytes"/> of PCM plus 1 MiB of headers and other chunks. Checked before the read.</summary>
    public const long MaxWaveFileBytes = MaxSoundBytes + (1L << 20);

    /// <summary>
    /// The data chunk of a RIFF/WAVE file whose fmt chunk says 22,050 Hz mono 16-bit, as ClassicUO's SoundsLoader.ReadWave
    /// walked it (chunks in order, padded to even sizes, a size running past the end clamped to the end), with every size
    /// compared by subtraction so no chunk size can overflow the walk (Codex audit: a 44-byte file declaring 0x7FFFFFF0
    /// data bytes). Returns the PCM's range in <paramref name="raw"/>, or an error. The length is not checked here: see
    /// <see cref="CheckPcmLength"/>.
    /// </summary>
    public static string? ParseWave(ReadOnlySpan<byte> raw, out int dataOffset, out int dataLength)
    {
        dataOffset = dataLength = 0;
        if (raw.Length < 44 || !raw.Slice(0, 4).SequenceEqual("RIFF"u8) || !raw.Slice(8, 4).SequenceEqual("WAVE"u8)) return "not a WAV";
        int at = 12, channels = 0, rate = 0, bits = 0;
        while (raw.Length - at >= 8)
        {
            ReadOnlySpan<byte> kind = raw.Slice(at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(at + 4));
            int avail = raw.Length - at - 8;
            if (size < 0 || size > avail) size = avail;
            if (kind.SequenceEqual("fmt "u8) && size >= 16)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(at + 10));
                rate = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(at + 12));
                bits = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(at + 22));
            }
            else if (kind.SequenceEqual("data"u8))
            {
                if (channels != 1 || rate != SoundRate || bits != 16) return $"{rate} Hz {channels} ch {bits}-bit (the client plays 22050 mono 16-bit)";
                dataOffset = at + 8;
                dataLength = size;
                return null;
            }
            // at + 8 + size <= raw.Length here, so this stays at most raw.Length + 1
            at += 8 + size + (size & 1);
        }
        return "no data chunk";
    }

    /// <summary>
    /// A loose Sounds/&lt;id&gt;.wav as the client plays it: the file's size is checked against
    /// <see cref="MaxWaveFileBytes"/> before it is read, the chunks are walked by <see cref="ParseWave"/>, and the PCM
    /// must pass <see cref="CheckPcmLength"/>. Returns an array of exactly the PCM, or null with <paramref name="error"/>;
    /// never throws for a bad or unreadable file. The client and the converter both use it, so a WAV that one refuses
    /// the other refuses too, and the archive's sound is used instead.
    /// </summary>
    public static byte[]? ReadWaveFile(string path, out string? error)
    {
        byte[] raw;
        try
        {
            long length = new FileInfo(path).Length;
            if (length > MaxWaveFileBytes)
            {
                error = $"{length} bytes (a loose WAV is at most {MaxWaveFileBytes})";
                return null;
            }
            raw = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }
        return ReadWave(raw, out error);
    }

    /// <summary><see cref="ReadWaveFile"/> on bytes already in memory.</summary>
    public static byte[]? ReadWave(ReadOnlySpan<byte> raw, out string? error)
    {
        error = ParseWave(raw, out int offset, out int length) ?? CheckPcmLength(length);
        return error == null ? raw.Slice(offset, length).ToArray() : null;
    }

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
