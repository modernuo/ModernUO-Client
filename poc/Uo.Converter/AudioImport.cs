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

    public sealed record SoundStats(bool Uop, int Slots, int Present, int Records, int Aliases, int Ours, int Empty, long PcmBytes, long FileBytes)
    {
        public override string ToString() =>
            $"{(Uop ? "soundLegacyMUL.uop" : "sound.mul")}: {Slots} slots, {Present} sounds, {Records} records ({Aliases} Sound.def/index aliases, {Ours} loose Sounds/*.wav, {Empty} empty entries left out), " +
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

        /// <summary>
        /// One sound as the client resolved it: a loose WAV first (if it is one the client plays), else the archive entry
        /// (40-byte name, then PCM). An entry of 40 bytes or less has no PCM and is absent. <paramref name="alias"/> is the
        /// archive entry's (offset, length), which aliases share; null for a loose file.
        /// </summary>
        public bool TryGet(int id, out byte[] pcm, out byte[] name, out (long, int)? alias)
        {
            pcm = Array.Empty<byte>();
            name = Array.Empty<byte>();
            alias = null;
            if (id < 0 || id >= MaxIds) return false;
            if (Ours.TryGetValue(id, out string? mine))
            {
                byte[]? data = ReadWave(mine, out _);
                if (data != null)
                {
                    pcm = data;
                    name = Truncate(Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(mine)), AudioConst.MaxSoundName);
                    return data.Length > 0;
                }
            }
            ref var e = ref File.GetValidRefEntry(id);
            if (e.Length <= AudioConst.MaxSoundName) return false;
            byte[] raw = new byte[e.Length];
            File.Seek(e.Offset, SeekOrigin.Begin);
            File.Read(raw);
            int n = Array.IndexOf(raw, (byte)0, 0, AudioConst.MaxSoundName);
            name = raw.AsSpan(0, n < 0 ? AudioConst.MaxSoundName : n).ToArray();
            pcm = raw.AsSpan(AudioConst.MaxSoundName).ToArray();
            alias = (e.Offset, e.Length);
            return true;
        }

        public int MaxId => Math.Max(Math.Min(File.Entries.Length, MaxIds) - 1, Ours.Count == 0 ? -1 : Ours.Keys.Max());

        public void Dispose() => File.Dispose();
    }

    private static byte[] Truncate(byte[] b, int max) => b.Length <= max ? b : b.AsSpan(0, max).ToArray();

    /// <summary>
    /// ClassicUO SoundsLoader.ReadWave, in effect: the data chunk of a RIFF/WAVE file whose fmt chunk says 22,050 Hz
    /// mono 16-bit, chunks walked in order (padded to even sizes; a size running past the end is clamped). Null, with
    /// <paramref name="error"/>, for anything else.
    /// </summary>
    public static byte[]? ReadWave(string path, out string? error)
    {
        byte[] raw;
        try
        {
            raw = System.IO.File.ReadAllBytes(path);
        }
        catch (IOException e)
        {
            error = e.Message;
            return null;
        }
        return ReadWave(raw, out error);
    }

    public static byte[]? ReadWave(byte[] raw, out string? error)
    {
        error = null;
        if (raw.Length < 44 || raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F' || raw[8] != 'W' || raw[9] != 'A' || raw[10] != 'V' || raw[11] != 'E')
        {
            error = "not a WAV";
            return null;
        }
        int at = 12, channels = 0, rate = 0, bits = 0;
        while (at + 8 <= raw.Length)
        {
            string kind = Encoding.ASCII.GetString(raw, at, 4);
            int size = BitConverter.ToInt32(raw, at + 4);
            if (size < 0 || at + 8 + size > raw.Length) size = raw.Length - at - 8;
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
                    error = $"{rate} Hz {channels} ch {bits}-bit (the client plays 22050 mono 16-bit)";
                    return null;
                }
                return raw.AsSpan(at + 8, size).ToArray();
            }
            at += 8 + size + (size & 1);
        }
        error = "no data chunk";
        return null;
    }

    /// <summary>Writes audio/sounds.bin from <paramref name="src"/>: one record per archive entry (aliases share it) or loose file.</summary>
    public static SoundStats WriteSounds(SoundSource src, string outDir)
    {
        int max = src.MaxId;
        // first pass: what each id resolves to (slots sized to the highest present id, not to the archive's index)
        var ids = new List<(int Id, byte[] Pcm, byte[] Name, (long, int)? Alias)>();
        int empty = 0;
        for (int id = 0; id <= max; id++)
        {
            if (src.TryGet(id, out var pcm, out var name, out var alias)) ids.Add((id, pcm, name, alias));
            else if (src.File.GetValidRefEntry(id).Length > 0 || src.Ours.ContainsKey(id)) empty++;
        }
        var w = new RecordStoreWriter(ids.Count == 0 ? 1 : ids[^1].Id + 1);
        var byEntry = new Dictionary<(long, int), int>();
        int aliases = 0, ours = 0;
        long pcmBytes = 0;
        foreach (var (id, pcm, name, alias) in ids)
        {
            int rec;
            if (alias is { } key && byEntry.TryGetValue(key, out rec))
            {
                aliases++;
            }
            else
            {
                var payload = AudioConst.Payload(pcm, name);
                rec = w.Add(payload, (uint)payload.Length, StoreConst.CodecRaw, AudioConst.SoundRate, AudioConst.SoundChannels, (ushort)name.Length);
                if (alias is { } k) byEntry[k] = rec;
                else ours++;
                pcmBytes += pcm.Length;
            }
            w.Slots[id] = new StoreSlot { Record = (uint)rec + 1 };
        }
        string path = Path.Combine(outDir, AudioConst.SoundsFile);
        w.Write(path, AudioConst.MagicSounds, src.Uop ? AudioConst.SoundFlagUop : 0);
        return new SoundStats(src.Uop, w.Slots.Length, ids.Count, w.RecordCount, aliases, ours, empty, pcmBytes, new FileInfo(path).Length);
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
                    byte[] mp3 = File.ReadAllBytes(full);
                    var check = Mp3Validator.Validate(mp3);
                    if (!check.IsValid)
                    {
                        invalid++;
                        warn?.Invoke($"music {e.Id} ({e.Name}): {e.Path} rejected by Mp3Validator: {check.Error}");
                    }
                    else
                    {
                        byte[] name = Truncate(Encoding.UTF8.GetBytes(Path.GetFileName(full)), AudioConst.MaxMusicName);
                        var payload = AudioConst.Payload(mp3, name);
                        rec = w.Add(payload, (uint)payload.Length, StoreConst.CodecRaw, AudioConst.MusicFormatMp3, 0, (ushort)name.Length);
                        bytes += mp3.Length;
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
