using System.Diagnostics;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.IO.Audio;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// The converter's sounds and music stages (AudioImport does the reading and writing) and their verification against
/// the frozen client loader (tools/ClassicUO.Assets.Mul/SoundsLoader.cs): every sound id byte-exact, every music id's
/// MP3 byte-exact with its loop flag.
/// </summary>
public static class AudioConvert
{
    public static AudioImport.SoundStats Sounds(UOFileManager fm, string outDir)
    {
        using var src = AudioImport.SoundSource.Open(fm.GetUOFilePath, fm.BasePath, fm.IsUOPInstallation);
        return AudioImport.WriteSounds(src, outDir, m => Console.WriteLine($"  warning: {m}"));
    }

    public static AudioImport.MusicStats Music(UOFileManager fm, string outDir)
    {
        var table = AudioImport.ReadMusicTable(fm.GetUOFilePath, fm.BasePath, LegacyMusicConfig(fm.Version), out bool builtin);
        return AudioImport.WriteMusic(table, builtin, outDir, m => Console.WriteLine($"  warning: {m}"));
    }

    /// <summary>
    /// Clients before 4.0.11c keep Config.txt in Music/, later ones in Music/Digital/ (ClassicUO's CV_4011C switchover).
    /// ClassicUO parses "4.0.11c" as 4.0.11.2 (letters count from a = 0) while CV_4011C carries the letter's ASCII
    /// code (0x04000B63), so no parsed 4.0.11x reached it and a 4.0.11c install (UOML) got the built-in table, whose
    /// names that install does not have (57 of 67 ids silent). The converter compares with the parsed form instead.
    /// </summary>
    public const uint ParsedCV4011C = (4u << 24) | (0u << 16) | (11u << 8) | 2u;

    public static bool LegacyMusicConfig(ClientVersion v) => (uint)v < ParsedCV4011C;

    private static UOFileManager? s_loaded;

    // The frozen loader keeps its music table in a static dictionary that its built-in table Add()s to: load it once.
    private static void EnsureLoaded(UOFileManager fm)
    {
        if (s_loaded == fm) return;
        fm.Sounds.Load();
        s_loaded = fm;
    }

    public static void VerifySounds(UOFileManager fm, string outDir)
    {
        var sw = Stopwatch.StartNew();
        EnsureLoaded(fm);
        using var store = AudioConst.OpenSoundStore(Path.Combine(outDir, AudioConst.SoundsFile));
        if (AudioConst.CheckNoOverlap(store.Records) is { } overlap) Verify.Fail($"sounds: {overlap}");
        bool uop = (store.Header.Flags & AudioConst.SoundFlagUop) != 0;
        int present = 0, bad = 0;
        long bytes = 0;
        var seenRec = new HashSet<ulong>();
        for (int id = 0; id < AudioConst.MaxIds; id++)
        {
            byte[]? data = null;
            string? name = null;
            try
            {
                if (!fm.Sounds.TryGetSound(id, out data, out name)) data = null;
            }
            catch (OverflowException)
            {
                data = null; // an entry shorter than its 40-byte name: the client threw (new byte[negative]); the pack leaves it out
            }
            // the client plays what the old loader read only when FNA can take it (whole 16-bit samples, not empty)
            bool expect = data != null && AudioConst.CheckPcmLength(data.Length) == null;
            bool has = store.TryGet(id, out _, out var rec);
            if (expect != has)
            {
                if (bad++ < 10) Verify.Fail($"sound {id}: client {(expect ? "has it" : "has none")}, pack {(has ? "has it" : "has none")}");
                continue;
            }
            if (!has) continue;
            if (AudioConst.CheckSoundRecord(rec) is { } err)
            {
                if (bad++ < 10) Verify.Fail($"sound {id}: {err}");
                continue;
            }
            // exactly what the client hands to FNA: SoundsLoader.TryGetSound's decode (AudioConst.ReadSound), whose whole
            // array UOSound submits (FNA's SubmitBuffer checks the array's length, not just the count: bench-000049)
            byte[] pcm = AudioConst.ReadSound(store, rec, out string packName);
            if (AudioConst.SubmitError(pcm, 0, pcm.Length) is { } submit)
            {
                if (bad++ < 10) Verify.Fail($"sound {id}: FNA would refuse it ({submit})");
                continue;
            }
            // the client's name is the 40-byte field decoded whole (NULs included) or the loose file's name
            string expectName = name!.Split('\0')[0];
            if (!pcm.AsSpan().SequenceEqual(data!) || packName != expectName)
            {
                if (bad++ < 10) Verify.Fail($"sound {id}: PCM or name differs ({pcm.Length} vs {data!.Length} bytes, '{packName}' vs '{expectName}')");
                continue;
            }
            present++;
            if (seenRec.Add(rec.Offset)) bytes += pcm.Length;
        }
        if (uop != (fm.IsUOPInstallation && File.Exists(fm.GetUOFilePath("soundLegacyMUL.uop")))) Verify.Fail("sounds: UOP flag");
        Console.WriteLine($"  sounds: {AudioConst.MaxIds} ids, {present} present, {store.Records.Length} records, {bytes / 1048576.0:F1} MiB PCM, every sound and name {(bad == 0 ? "EXACT" : $"{bad} MISMATCHES")} vs SoundsLoader, as the arrays handed to FNA ({sw.Elapsed.TotalSeconds:F1} s)");
    }

    public static void VerifyMusic(UOFileManager fm, string outDir)
    {
        var sw = Stopwatch.StartNew();
        EnsureLoaded(fm);
        using var store = AudioConst.OpenMusicStore(Path.Combine(outDir, AudioConst.MusicFile));
        if (AudioConst.CheckNoOverlap(store.Records) is { } overlap) Verify.Fail($"music: {overlap}");
        int present = 0, bad = 0, notPlayable = 0;
        long bytes = 0;
        var cache = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<uint>();
        for (int id = 0; id < AudioConst.MaxIds; id++)
        {
            byte[]? expect = null;
            bool loop = false;
            if (fm.Sounds.TryGetMusicData(id, out string name, out loop))
            {
                string path = Path.GetFullPath(fm.Sounds.MusicPath(name));
                if (!cache.TryGetValue(path, out expect))
                {
                    expect = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    if (expect != null && !Mp3Validator.Validate(expect).IsValid) expect = null; // the pack leaves out what the client's guard refuses
                    cache[path] = expect;
                }
                if (expect == null) notPlayable++;
            }
            bool has = store.TryGet(id, out var slot, out var rec);
            if ((expect != null) != has)
            {
                if (bad++ < 10) Verify.Fail($"music {id}: client {(expect != null ? "has it" : "has none")}, pack {(has ? "has it" : "has none")}");
                continue;
            }
            if (!has) continue;
            string? err = AudioConst.CheckMusicRecord(rec) ?? AudioConst.CheckMusicSlot(slot);
            if (err != null)
            {
                if (bad++ < 10) Verify.Fail($"music {id}: {err}");
                continue;
            }
            var payload = new byte[rec.RawBytes];
            store.Read(rec, payload);
            if (!payload.AsSpan(0, AudioConst.AudioBytes(rec)).SequenceEqual(expect) || ((slot.Arg & AudioConst.MusicArgLoop) != 0) != loop)
            {
                if (bad++ < 10) Verify.Fail($"music {id} ({name}): MP3 bytes or loop flag differ");
                continue;
            }
            present++;
            if (seen.Add(slot.Record)) bytes += expect!.Length;
        }
        Console.WriteLine($"  music: {present} ids, {store.Records.Length} MP3 records, {bytes / 1048576.0:F1} MiB, every MP3 byte and loop flag {(bad == 0 ? "EXACT" : $"{bad} MISMATCHES")} vs SoundsLoader + the files" +
                          $"{(notPlayable > 0 ? $"; {notPlayable} configured ids without a playable file (absent in both)" : "")} ({sw.Elapsed.TotalSeconds:F1} s)");
    }
}

/// <summary>
/// --audio-report &lt;pack&gt;: offline load and first-read costs of the pack's audio stores against the frozen client
/// loader on the same install (the pre-pack client path). Writes nothing.
/// </summary>
public static class AudioReport
{
    public static int Run(UOFileManager fm, string pack, int reps = 7)
    {
        string sp = Path.Combine(pack, AudioConst.SoundsFile), mp = Path.Combine(pack, AudioConst.MusicFile);

        // load: the old loader (index the archive, Sound.def, Config.txt with a Music folder scan per line) vs opening
        // both stores and checking every record
        var oldLoad = new List<double>();
        var newLoad = new List<double>();
        long oldAlloc = 0, newAlloc = 0;
        for (int r = 0; r < reps; r++)
        {
            var f = new UOFileManager(fm.Version, fm.BasePath);
            f.Sounds.ClearResources();
            long a = GC.GetAllocatedBytesForCurrentThread();
            var t = Stopwatch.GetTimestamp();
            f.Sounds.Load();
            oldLoad.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            oldAlloc = GC.GetAllocatedBytesForCurrentThread() - a;
            f.Sounds.ClearResources();

            a = GC.GetAllocatedBytesForCurrentThread();
            t = Stopwatch.GetTimestamp();
            using var s = AudioConst.OpenSoundStore(sp);
            using var m = AudioConst.OpenMusicStore(mp);
            foreach (ref readonly var rec in s.Records.AsSpan()) AudioConst.CheckSoundRecord(rec);
            foreach (ref readonly var rec in m.Records.AsSpan()) AudioConst.CheckMusicRecord(rec);
            AudioConst.CheckNoOverlap(s.Records);
            AudioConst.CheckNoOverlap(m.Records);
            newLoad.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            newAlloc = GC.GetAllocatedBytesForCurrentThread() - a;
        }
        // memory held after load (full collections around each)
        long h0 = GC.GetTotalMemory(true);
        var held = new UOFileManager(fm.Version, fm.BasePath);
        held.Sounds.ClearResources();
        held.Sounds.Load();
        long oldHeld = GC.GetTotalMemory(true) - h0;
        GC.KeepAlive(held);
        held.Sounds.ClearResources();
        held = null;
        h0 = GC.GetTotalMemory(true);
        var hs = AudioConst.OpenSoundStore(sp);
        var hm = AudioConst.OpenMusicStore(mp);
        long newHeld = GC.GetTotalMemory(true) - h0;
        GC.KeepAlive(hs);
        GC.KeepAlive(hm);
        hs.Dispose();
        hm.Dispose();
        Console.WriteLine($"  held after load (GC heap): old {oldHeld / 1024.0:F0} KB (plus a 160.6 MiB read-only mapping of soundLegacyMUL.uop); pack {newHeld / 1024.0:F0} KB (no mapping)");

        using var sounds = AudioConst.OpenSoundStore(sp);
        using var music = AudioConst.OpenMusicStore(mp);
        long tables = (long)sounds.Slots.Length * 8 + sounds.Records.Length * 24 + (long)music.Slots.Length * 8 + music.Records.Length * 24;
        Console.WriteLine($"  load: old SoundsLoader.Load {Med(oldLoad):F2} ms (min {oldLoad.Min():F2}), {oldAlloc / 1024} KB allocated; pack stores {Med(newLoad):F2} ms (min {newLoad.Min():F2}), {newAlloc / 1024} KB allocated; tables held {tables / 1024.0:F1} KB");

        // first play of a sound: one read of the whole entry/record (what GetSound does once per id), every sound
        fm.Sounds.ClearResources();
        var f2 = new UOFileManager(fm.Version, fm.BasePath);
        f2.Sounds.Load();
        var oldRead = new List<double>();
        var newRead = new List<double>();
        for (int id = 0; id < sounds.Slots.Length; id++)
        {
            if (!sounds.TryGet(id, out _, out var rec)) continue;
            var t = Stopwatch.GetTimestamp();
            f2.Sounds.TryGetSound(id, out _, out _);
            oldRead.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            t = Stopwatch.GetTimestamp();
            var buf = new byte[rec.RawBytes];
            sounds.Read(rec, buf);
            newRead.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        Console.WriteLine($"  sound first read ({newRead.Count} sounds, warm file cache): old p50 {Pct(oldRead, 50):F3} / p99 {Pct(oldRead, 99):F3} / max {oldRead.Max():F2} ms; pack p50 {Pct(newRead, 50):F3} / p99 {Pct(newRead, 99):F3} / max {newRead.Max():F2} ms");

        // music: getting the MP3 bytes into memory for the decoder (decode itself is unchanged: dr_mp3 on the worker)
        var oldMp3 = new List<double>();
        var newMp3 = new List<double>();
        for (int id = 0; id < music.Slots.Length; id++)
        {
            if (!music.TryGet(id, out _, out var rec) || !f2.Sounds.TryGetMusicData(id, out string name, out _)) continue;
            string path = f2.Sounds.MusicPath(name);
            var t = Stopwatch.GetTimestamp();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                var b = new byte[fs.Length];
                fs.ReadExactly(b);
            }
            oldMp3.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            t = Stopwatch.GetTimestamp();
            using (var rs = music.OpenRawStream(rec, 0, AudioConst.AudioBytes(rec)))
            {
                var b = new byte[rs.Length];
                rs.ReadExactly(b);
            }
            newMp3.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        Console.WriteLine($"  music bytes read ({newMp3.Count} ids): loose file p50 {Pct(oldMp3, 50):F2} / max {oldMp3.Max():F2} ms; pack record p50 {Pct(newMp3, 50):F2} / max {newMp3.Max():F2} ms");
        return 0;
    }

    private static double Med(List<double> l) => Pct(l, 50);

    private static double Pct(List<double> l, int p)
    {
        var s = l.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, s.Count * p / 100)];
    }
}
