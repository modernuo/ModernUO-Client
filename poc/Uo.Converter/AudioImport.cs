using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassicUO.IO;
using ClassicUO.IO.Audio;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// audio/sounds.bin and audio/music.bin (docs/modern-client/pack-format.md, "Audio"). Reads a client folder the way
/// ClassicUO's SoundsLoader did (soundLegacyMUL.uop on UOP installations, else sound.mul + soundidx.mul; Sound.def
/// aliases; loose Sounds/&lt;id&gt;.wav files; Music/Digital/Config.txt or the built-in table for old clients) and writes
/// what the client resolved. Depends only on ClassicUO.IO and Uo.Pack, so the unit tests compile it in and run it
/// on synthetic files; the converter's verification checks the result against the frozen client loader instead.
/// </summary>
public static class AudioImport
{
    public const int MaxIds = AudioConst.MaxIds;

    public sealed record SoundStats(bool Uop, int Slots, int Present, int Records, int Aliases, int Ours, int Empty, long PcmBytes, long FileBytes, int Unplayable = 0, int LooseRefused = 0)
    {
        public override string ToString() =>
            $"{(Uop ? "soundLegacyMUL.uop" : "sound.mul")}: {Slots} slots, {Present} sounds, {Records} records ({Aliases} Sound.def/index aliases, {Ours} loose Sounds/*.wav, {Empty} empty entries left out{(Unplayable > 0 ? $", {Unplayable} unplayable left out" : "")}{(LooseRefused > 0 ? $", {LooseRefused} loose files refused" : "")}), " +
            $"PCM {PcmBytes / 1048576.0:F1} MiB, file {FileBytes / 1048576.0:F1} MiB";
    }

    public sealed record MusicStats(int Slots, int Present, int Records, int Looping, int Missing, int Invalid, bool BuiltinTable, long Mp3Bytes, long FileBytes)
    {
        public override string ToString() =>
            $"{Slots} slots, {Present} tracks ({Looping} looping), {Records} MP3 files{(BuiltinTable ? " (no Config.txt: the client's built-in table)" : "")}, " +
            $"{Missing} configured without a file, {Invalid} rejected by Mp3Validator; MP3 {Mp3Bytes / 1048576.0:F1} MiB, file {FileBytes / 1048576.0:F1} MiB";
    }

    // ------------------------------------------------------------------------------------------------- sounds

    /// <summary>The client's sound sources: the archive (with Sound.def applied) and the loose Sounds/*.wav files.</summary>
    public sealed class SoundSource : IDisposable
    {
        public UOFile File { get; }
        public bool Uop { get; }
        public Dictionary<int, string> Ours { get; } = new();

        private SoundSource(UOFile file, bool uop)
        {
            File = file;
            Uop = uop;
        }

        /// <summary>
        /// Opens the archive as ClassicUO's SoundsLoader.Load did: soundLegacyMUL.uop when <paramref name="uopInstallation"/>
        /// and the file exists, else sound.mul + soundidx.mul. <paramref name="resolve"/> maps a client file name to its path.
        /// </summary>
        public static SoundSource Open(Func<string, string> resolve, string basePath, bool uopInstallation)
        {
            string path = resolve("soundLegacyMUL.uop");
            SoundSource src;
            if (uopInstallation && System.IO.File.Exists(path))
            {
                src = new SoundSource(new UOFileUop(path, "build/soundlegacymul/{0:D8}.dat"), true);
            }
            else
            {
                path = resolve("sound.mul");
                string idx = resolve("soundidx.mul");
                if (!System.IO.File.Exists(path) || !System.IO.File.Exists(idx))
                    throw new FileNotFoundException("no sounds found (soundLegacyMUL.uop or sound.mul + soundidx.mul)");
                src = new SoundSource(new UOFileMul(path, idx), false);
            }
            src.File.FillEntries();
            src.LoadOurs(basePath);
            src.ApplyDef(resolve("Sound.def"));
            return src;
        }

        // SoundsLoader.LoadOurs: Sounds/<id>.wav, ids 0..0xFFFE, indexed once.
        private void LoadOurs(string basePath)
        {
            string folder = Path.Combine(basePath, "Sounds");
            if (!Directory.Exists(folder)) return;
            foreach (string p in Directory.EnumerateFiles(folder, "*.wav"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(p), out int id) && id >= 0 && id < MaxIds)
                    Ours[id] = p;
        }

        // SoundsLoader.Load's Sound.def pass, verbatim in effect: an id that already has data is skipped; each group
        // entry with data replaces the entry (the last one wins), -1 clears it.
        private void ApplyDef(string def)
        {
            if (!System.IO.File.Exists(def)) return;
            var entries = File.Entries;
            using var reader = new DefReader(def);
            while (reader.Next())
            {
                int index = reader.ReadInt();
                if (index < 0 || index >= MaxIds || index >= entries.Length || entries[index].Length > 0) continue;
                int[] group = reader.ReadGroup();
                if (group == null) continue;
                for (int i = 0; i < group.Length; i++)
                {
                    int check = group[i];
                    if (check < -1 || check >= MaxIds) continue;
                    if (check == -1)
                    {
                        entries[index] = default;
                    }
                    else
                    {
                        // the client indexes Entries[check] unchecked (and would throw past the end); out of range = no data
                        if (check >= entries.Length || entries[check].Length == 0) continue;
                        entries[index] = entries[check];
                    }
                }
            }
        }

        /// <summary>Loose Sounds/&lt;id&gt;.wav files the client would refuse (and play the archive's sound instead), with the reason.</summary>
        public List<(int Id, string Error)> RefusedLoose { get; } = new();

        /// <summary>
        /// The id's loose WAV, if it has one and the client plays it (AudioConst.ReadWaveFile: one handle, at most
        /// MaxWaveFileBytes, 22,050 Hz mono 16-bit, a non-empty even data chunk). A refused loose file is recorded in
        /// <see cref="RefusedLoose"/> and the caller falls back to the archive, as the client does at runtime (Codex
        /// audit P2: an invalid override used to hide a valid archive sound).
        /// </summary>
        public bool TryGetLoose(int id, out byte[] pcm, out byte[] name)
        {
            pcm = Array.Empty<byte>();
            name = Array.Empty<byte>();
            if (id < 0 || id >= MaxIds || !Ours.TryGetValue(id, out string? mine)) return false;
            byte[]? data = AudioConst.ReadWaveFile(mine, out string? error);
            if (data == null)
            {
                RefusedLoose.Add((id, error ?? "refused"));
                return false;
            }
            pcm = data;
            name = Truncate(Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(mine)), AudioConst.MaxSoundName);
            return true;
        }

        /// <summary>
        /// The archive entry an id resolves to after Sound.def, as (offset, length), which aliases share; false when it has
        /// none or the entry has no PCM (40 bytes or less: the name field only). Nothing is read.
        /// </summary>
        public bool TryGetArchiveEntry(int id, out (long Offset, int Length) entry)
        {
            entry = default;
            if (id < 0 || id >= MaxIds) return false;
            ref var e = ref File.GetValidRefEntry(id);
            if (e.Length <= AudioConst.MaxSoundName) return false;
            entry = (e.Offset, e.Length);
            return true;
        }

        /// <summary>Archive entries read so far (<see cref="ReadArchive"/>): one per distinct entry in WriteSounds, however many ids alias it.</summary>
        public int ArchiveReads { get; private set; }

        /// <summary>Reads an archive entry: the 40-byte name field (up to its first NUL), then the PCM.</summary>
        public void ReadArchive((long Offset, int Length) entry, out byte[] pcm, out byte[] name)
        {
            ArchiveReads++;
            byte[] raw = new byte[entry.Length];
            File.Seek(entry.Offset, SeekOrigin.Begin);
            File.Read(raw);
            int n = Array.IndexOf(raw, (byte)0, 0, AudioConst.MaxSoundName);
            name = raw.AsSpan(0, n < 0 ? AudioConst.MaxSoundName : n).ToArray();
            pcm = raw.AsSpan(AudioConst.MaxSoundName).ToArray();
        }

        public int MaxId => Math.Max(Math.Min(File.Entries.Length, MaxIds) - 1, Ours.Count == 0 ? -1 : Ours.Keys.Max());

        public void Dispose() => File.Dispose();
    }

    /// <summary>
    /// Verify's model of the client's sound resolution, built from the frozen pre-pack loader's answers (it is not the
    /// importer's code path): <paramref name="loader"/> is the old TryGetSound (loose file first), <paramref name="archive"/>
    /// the old loader's archive read alone. A loose file the client refuses (over <see cref="AudioConst.MaxWaveFileBytes"/>,
    /// vanished or unreadable, or data FNA cannot take) falls back to the archive, as SoundsLoader.TryGetSound does at
    /// runtime; an id plays only when its PCM passes <see cref="AudioConst.CheckPcmLength"/>. The loose file's size is
    /// checked first, through one open handle, and the frozen loader is only asked about a file within the cap (Codex
    /// round 2, P2: it used to read an oversized file whole before the size check). The file can still grow or be replaced
    /// before the frozen loader opens it again, so that loader also checks the cap on the one handle it reads through
    /// (Codex round 3, P2; "Not verbatim" there): an enlarged file is refused, never allocated, and the archive answers. An
    /// OverflowException from the old loader (an archive entry shorter than its 40-byte name: it threw on
    /// new byte[negative]) means no sound. Null data: the id is absent.
    /// </summary>
    public static (byte[]? Data, string? Name) ExpectedClientSound(string? loosePath, Func<(byte[]? Data, string? Name)> loader, Func<(byte[]? Data, string? Name)> archive)
    {
        (byte[]? Data, string? Name) r;
        if (loosePath == null) r = Call(loader, false);
        else if (!LooseWithinCap(loosePath)) r = Call(archive, false);
        else
        {
            // the file may still vanish or change before the old loader opens it: then the archive answers, as in the client
            r = Call(loader, true);
            if (r.Data == null || AudioConst.CheckPcmLength(r.Data.Length) != null) r = Call(archive, false);
        }
        return r.Data != null && AudioConst.CheckPcmLength(r.Data.Length) == null ? r : (null, null);

        static (byte[]? Data, string? Name) Call(Func<(byte[]? Data, string? Name)> f, bool loose)
        {
            try
            {
                return f();
            }
            catch (OverflowException)
            {
                return (null, null);
            }
            catch (Exception e) when (loose && (e is IOException || e is UnauthorizedAccessException))
            {
                return (null, null);
            }
        }
    }

    // the client's first check of a loose WAV: it opens, and that handle's length is within the cap
    private static bool LooseWithinCap(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return RandomAccess.GetLength(handle) <= AudioConst.MaxWaveFileBytes;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Truncate(byte[] b, int max) => b.Length <= max ? b : b.AsSpan(0, max).ToArray();

    /// <summary>
    /// Writes audio/sounds.bin from <paramref name="src"/>: one record per archive entry (aliases share it) or loose file.
    /// Each id's loose override is resolved first; an archive entry is read once, however many ids alias it, and its PCM
    /// is held once (Codex round 2, P2: every alias used to keep its own copy until the second pass deduplicated them, so
    /// one 64 MiB entry with 128 aliases held 8 GiB during conversion for a one-record pack).
    /// </summary>
    public static SoundStats WriteSounds(SoundSource src, string outDir, Action<string>? warn = null)
    {
        int max = src.MaxId;
        // first pass: what each id resolves to (slots sized to the highest present id, not to the archive's index)
        var sources = new List<(byte[] Pcm, byte[] Name, bool Loose)>();
        var byEntry = new Dictionary<(long, int), int>(); // archive entry -> index in sources, -1 when unplayable
        var ids = new List<(int Id, int Source)>();
        int empty = 0, unplayable = 0, aliases = 0;
        for (int id = 0; id <= max; id++)
        {
            if (src.TryGetLoose(id, out var pcm, out var name))
            {
                // ReadWaveFile already refuses what the client would not play
                ids.Add((id, sources.Count));
                sources.Add((pcm, name, true));
            }
            else if (src.TryGetArchiveEntry(id, out var entry))
            {
                if (byEntry.TryGetValue(entry, out int si))
                {
                    if (si >= 0) aliases++;
                }
                else
                {
                    src.ReadArchive(entry, out pcm, out name);
                    // the client refuses PCM that is empty, too long or not whole 16-bit samples (FNA would throw): left out
                    si = AudioConst.CheckPcmLength(pcm.Length) == null ? sources.Count : -1;
                    if (si >= 0) sources.Add((pcm, name, false));
                    byEntry[entry] = si;
                }
                if (si < 0)
                {
                    unplayable++;
                    warn?.Invoke($"sound {id}: {AudioConst.CheckPcmLength(entry.Length - AudioConst.MaxSoundName)}; left out");
                }
                else ids.Add((id, si));
            }
            else if (src.File.GetValidRefEntry(id).Length > 0 || src.Ours.ContainsKey(id)) empty++;
        }
        foreach (var (id, error) in src.RefusedLoose) warn?.Invoke($"sound {id}: loose Sounds/{id}.wav refused ({error}); the archive's sound, if any, is used");
        // second pass: one record per source, in the order of the first id that uses it
        var w = new RecordStoreWriter(ids.Count == 0 ? 1 : ids[^1].Id + 1);
        var record = new int[sources.Count];
        Array.Fill(record, -1);
        int ours = 0;
        long pcmBytes = 0;
        foreach (var (id, si) in ids)
        {
            if (record[si] < 0)
            {
                var (pcm, name, loose) = sources[si];
                var payload = AudioConst.Payload(pcm, name);
                record[si] = w.Add(payload, (uint)payload.Length, StoreConst.CodecRaw, AudioConst.SoundRate, AudioConst.SoundChannels, (ushort)name.Length);
                if (loose) ours++;
                pcmBytes += pcm.Length;
            }
            w.Slots[id] = new StoreSlot { Record = (uint)record[si] + 1 };
        }
        string path = Path.Combine(outDir, AudioConst.SoundsFile);
        w.Write(path, AudioConst.MagicSounds, src.Uop ? AudioConst.SoundFlagUop : 0);
        return new SoundStats(src.Uop, w.Slots.Length, ids.Count, w.RecordCount, aliases, ours, empty, pcmBytes, new FileInfo(path).Length, unplayable, src.RefusedLoose.Count);
    }

    // ------------------------------------------------------------------------------------------------- music

    /// <summary>One music id as the client resolved it: the name it looked up, the loop flag and the MP3 file's path.</summary>
    public sealed record MusicEntry(int Id, string Name, bool Loop, string Path);

    private static readonly char[] ConfigDelimiters = { ' ', ',', '\t' };

    /// <summary>
    /// The client's music table (SoundsLoader.Load): Music/Digital/Config.txt (Music/Config.txt before 4.0.11c,
    /// <paramref name="legacyConfig"/>) or, without it, the built-in table; each name resolved to a file as
    /// SoundsLoader.GetTrueFileName and Renderer.Sounds.Sound.GetMusic did. A later line for an id wins.
    /// </summary>
    public static List<MusicEntry> ReadMusicTable(Func<string, string> resolve, string basePath, bool legacyConfig, out bool builtinTable)
    {
        var table = new SortedDictionary<int, (string Name, bool Loop)>();
        string config = resolve(legacyConfig ? "Music/Config.txt" : "Music/Digital/Config.txt");
        builtinTable = !File.Exists(config);
        string musicDir = Path.Combine(basePath, "Music");
        string[] mp3s = Directory.Exists(musicDir) ? Directory.GetFiles(musicDir, "*.mp3", SearchOption.AllDirectories) : Array.Empty<string>();
        if (!builtinTable)
        {
            foreach (string line in File.ReadLines(config))
            {
                string[] s = line.Split(ConfigDelimiters);
                if (s.Length < 2 || s.Length > 3) continue;
                if (!int.TryParse(s[0], out int index))
                    throw new InvalidDataException($"{config}: '{line}': the id is not a number (the client throws on this line)");
                table[index] = (TrueFileName(mp3s, Path.GetFileNameWithoutExtension(s[1])), s.Length == 3 && s[2] == "loop");
            }
        }
        else
        {
            for (int i = 0; i < BuiltinTable.Length; i++) table[i] = BuiltinTable[i];
        }
        bool digital = Directory.Exists(Path.Combine(basePath, "Music", "Digital"));
        var list = new List<MusicEntry>();
        foreach (var (id, (name, loop)) in table)
        {
            if (id < 0 || id >= MaxIds) continue; // the client's music array has 0xFFFF slots
            string rel = digital ? $"Music/Digital/{name}" : $"Music/{name}";
            if (!rel.EndsWith(".mp3", StringComparison.InvariantCultureIgnoreCase)) rel += ".mp3";
            list.Add(new MusicEntry(id, name, loop, resolve(rel)));
        }
        return list;
    }

    // SoundsLoader.GetTrueFileName: the first *.mp3 under Music (any depth) matching ^{name}.mp3, ignoring case; the
    // name itself when none does (the play then fails). The pattern is the client's, unescaped.
    private static string TrueFileName(string[] mp3s, string name)
    {
        var pattern = new Regex($"^{name}.mp3", RegexOptions.IgnoreCase);
        foreach (string p in mp3s)
            if (pattern.IsMatch(Path.GetFileName(p))) return Path.GetFileName(p);
        return name;
    }

    /// <summary>Writes audio/music.bin: one record per distinct MP3 file (original bytes, then the file name); the loop flag on the slot.</summary>
    public static MusicStats WriteMusic(IReadOnlyList<MusicEntry> table, bool builtinTable, string outDir, Action<string>? warn = null)
    {
        int max = table.Count == 0 ? 0 : table.Max(e => e.Id);
        var w = new RecordStoreWriter(max + 1);
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int present = 0, looping = 0, missing = 0, invalid = 0;
        long bytes = 0;
        foreach (var e in table)
        {
            string full = Path.GetFullPath(e.Path);
            if (!byPath.TryGetValue(full, out int rec))
            {
                rec = -1;
                if (!File.Exists(full))
                {
                    missing++;
                    warn?.Invoke($"music {e.Id} ({e.Name}): no file {e.Path}");
                }
                else
                {
                    // bounded: an MP3 over Mp3Validator's limit is refused from its handle's length, never read (Codex round 2)
                    byte[]? mp3 = AudioConst.ReadFileBounded(full, Mp3Validator.MaxBytes, out string? readError);
                    string? error = mp3 == null ? readError : Mp3Validator.Validate(mp3) is { IsValid: false } check ? check.Error : null;
                    if (error != null)
                    {
                        invalid++;
                        warn?.Invoke($"music {e.Id} ({e.Name}): {e.Path} rejected by Mp3Validator: {error}");
                    }
                    else
                    {
                        byte[] name = Truncate(Encoding.UTF8.GetBytes(Path.GetFileName(full)), AudioConst.MaxMusicName);
                        var payload = AudioConst.Payload(mp3!, name);
                        rec = w.Add(payload, (uint)payload.Length, StoreConst.CodecRaw, AudioConst.MusicFormatMp3, 0, (ushort)name.Length);
                        bytes += mp3!.Length;
                    }
                }
                byPath[full] = rec;
            }
            if (rec < 0) continue;
            w.Slots[e.Id] = new StoreSlot { Record = (uint)rec + 1, Arg = e.Loop ? AudioConst.MusicArgLoop : (ushort)0 };
            present++;
            if (e.Loop) looping++;
        }
        string path = Path.Combine(outDir, AudioConst.MusicFile);
        w.Write(path, AudioConst.MagicMusic, builtinTable ? AudioConst.MusicFlagBuiltinTable : 0);
        return new MusicStats(w.Slots.Length, present, w.RecordCount, looping, missing, invalid, builtinTable, bytes, new FileInfo(path).Length);
    }

    /// <summary>SoundsLoader.Load's table for clients without a Config.txt (ids 0..66).</summary>
    public static readonly (string Name, bool Loop)[] BuiltinTable =
    {
        ("oldult01", true), ("create1", false), ("dragflit", false), ("oldult02", true), ("oldult03", true), ("oldult04", true),
        ("oldult05", true), ("oldult06", true), ("stones2", true), ("britain1", true), ("britain2", true), ("bucsden", true),
        ("jhelom", false), ("lbcastle", false), ("linelle", false), ("magincia", true), ("minoc", true), ("ocllo", true),
        ("samlethe", false), ("serpents", true), ("skarabra", true), ("trinsic", true), ("vesper", true), ("wind", true),
        ("yew", true), ("cave01", false), ("dungeon9", false), ("forest_a", false), ("intown01", false), ("jungle_a", false),
        ("mountn_a", false), ("plains_a", false), ("sailing", false), ("swamp_a", false), ("tavern01", false), ("tavern02", false),
        ("tavern03", false), ("tavern04", false), ("combat1", false), ("combat2", false), ("combat3", false), ("approach", false),
        ("death", false), ("victory", false), ("btcastle", false), ("nujelm", true), ("dungeon2", false), ("cove", true),
        ("moonglow", true), ("zento", true), ("tokunodungeon", true), ("Taiko", true), ("dreadhornarea", true), ("elfcity", true),
        ("grizzledungeon", true), ("melisandeslair", true), ("paroxysmuslair", true), ("gwennoconversation", true), ("goodendgame", true),
        ("goodvsevil", true), ("greatearthserpents", true), ("humanoids_u9", true), ("minocnegative", true), ("paws", true),
        ("selimsbar", true), ("serpentislecombat_u7", true), ("valoriaships", true),
    };
}
