using System.Diagnostics;
using ClassicUO.Assets;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// The last files the client read from the UO folder (docs/dev/pack-format.md, "Skills, skill groups, multimap, string
/// dictionary" and "Loose text files"): skills.mul/Skills.idx, skillgrp.mul, Multimap.rle + facet0*.mul,
/// string_dictionary.uop through the frozen loaders in tools/ClassicUO.Assets.Mul, and the hand-edited text/def files
/// copied verbatim. A source file the install lacks has no pack file (a stale one from an earlier run is removed).
/// </summary>
public static class MiscConvert
{
    public const int Quality = 9;
    /// <summary>A multimap record whose raw size is at most this is stored uncompressed.</summary>
    public const int SmallRaw = 16384;

    private static string Out(string outDir, string rel) => Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));

    private static bool SkillFilesExist(UOFileManager fm) =>
        File.Exists(fm.GetUOFilePath("skills.mul")) && File.Exists(fm.GetUOFilePath("Skills.idx"));

    // ---------------------------------------------------------------- skills, skill groups

    public static string Skills(UOFileManager fm, string outDir)
    {
        var parts = new List<string>();
        var path = Out(outDir, MiscConst.SkillsFile);
        if (SkillFilesExist(fm))
        {
            var s = fm.Skills;
            if (s.SkillsCount == 0) s.Load();
            StringListFile.Write(path, MiscConst.MagicSkills,
                s.Skills.Select(e => (e.HasAction ? MiscConst.SkillHasAction : 0u, e.Name)).ToList(), brotli: false);
            parts.Add($"{s.SkillsCount} skills, {new FileInfo(path).Length} B");
        }
        else
        {
            PackOutputFiles.Delete(path);
            parts.Add("no skills.mul/Skills.idx: no skills table");
        }

        var gpath = Out(outDir, MiscConst.SkillGroupsFile);
        if (SkillGroupsMul.TryRead(fm.GetUOFilePath("skillgrp.mul"), out var names, out var assignments, out var why))
        {
            SkillGroupsFile.Write(gpath, names, assignments);
            parts.Add($"{names.Length + 1} skill groups, {assignments.Length} assignments, {new FileInfo(gpath).Length} B");
        }
        else
        {
            PackOutputFiles.Delete(gpath);
            parts.Add($"skillgrp.mul {why}: no skill group table (the client uses its built-in groups)");
        }
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- multimap

    public static string MultiMap(UOFileManager fm, string outDir)
    {
        var m = fm.MultiMaps;
        m.Load();
        var path = Out(outDir, MiscConst.MultiMapFile);
        string? rle = m.RlePath;
        var facets = m.FacetPaths;
        if (rle == null && facets.Length == 0)
        {
            PackOutputFiles.Delete(path);
            return "no Multimap.rle or facet0*.mul: no multimap table";
        }
        if (facets.Length > MiscConst.MaxMultiMapFacets) throw new InvalidDataException($"{facets.Length} facet images; the pack holds at most {MiscConst.MaxMultiMapFacets}");

        var sources = new string?[1 + facets.Length];
        sources[MiscConst.MultiMapRleSlot] = rle;
        for (int i = 0; i < facets.Length; i++) sources[MiscConst.MultiMapFacetSlot(i)] = facets[i];
        var encoded = new (byte[] Raw, byte[] Payload, ushort Codec)[sources.Length];
        Parallel.For(0, sources.Length, ConvertOptions.Par, i =>
        {
            if (sources[i] == null) return;
            var raw = File.ReadAllBytes(sources[i]!);
            ushort codec = TextConvert.Raw || raw.Length <= SmallRaw ? StoreConst.CodecRaw : StoreConst.CodecBrotli;
            var p = RecordStoreWriter.Encode(raw, codec, Quality);
            encoded[i] = codec == StoreConst.CodecBrotli && p.Length >= raw.Length ? (raw, raw, StoreConst.CodecRaw) : (raw, p, codec);
        });
        var w = new RecordStoreWriter(sources.Length);
        long rawTotal = 0;
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] == null) continue;
            var (raw, payload, codec) = encoded[i];
            int rec = w.Add(payload, (uint)raw.Length, codec, (ushort)(i == MiscConst.MultiMapRleSlot ? 0 : 1), (ushort)Math.Max(0, i - 1));
            w.Slots[i] = new StoreSlot { Record = (uint)rec + 1 };
            rawTotal += raw.Length;
        }
        w.Write(path, MiscConst.MagicMultiMap, 0);
        return $"{(rle != null ? "Multimap.rle" : "no Multimap.rle")} + {facets.Length} facet images ({string.Join(",", facets.Select(Path.GetFileName))}), " +
               $"{rawTotal / 1048576.0:F2} MiB -> {new FileInfo(path).Length / 1048576.0:F2} MiB";
    }

    // ---------------------------------------------------------------- string dictionary

    public static string StringDict(UOFileManager fm, string outDir)
    {
        var d = fm.StringDictionary;
        if (d.Strings.Length == 0) d.Load();
        var path = Out(outDir, MiscConst.StringDictFile);
        if (d.Strings.Length == 0)
        {
            PackOutputFiles.Delete(path);
            return "no string_dictionary.uop (or no strings in it): no string dictionary table";
        }
        StringListFile.Write(path, MiscConst.MagicStringDict, d.Strings.Select(s => (0u, s)).ToList(), brotli: !TextConvert.Raw);
        return $"{d.Strings.Length} strings, {new FileInfo(path).Length / 1024.0:F0} KiB";
    }

    // ---------------------------------------------------------------- loose files

    /// <summary>The install's copy of a loose file (files_override applied, case-insensitive), or null when it has none.</summary>
    public static string? LooseSource(UOFileManager fm, string name)
    {
        var p = fm.GetUOFilePath(name);
        if (File.Exists(p)) return p;
        // Windows matches any casing already; elsewhere the frozen lookup only retries for a missing file name it was
        // given, so list the folder for the stock casings (Prof.txt, Citytext.enu, Anim1.def)
        var dir = Path.GetDirectoryName(Path.GetFullPath(p));
        if (dir == null || !Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    public static string Loose(UOFileManager fm, string outDir)
    {
        var copied = new List<string>();
        var missing = new List<string>();
        long bytes = 0;
        foreach (var name in MiscConst.LooseFiles)
        {
            var dest = Out(outDir, MiscConst.LoosePath(name));
            var src = LooseSource(fm, name);
            if (src == null)
            {
                PackOutputFiles.Delete(dest);
                missing.Add(name);
                continue;
            }
            long len = new FileInfo(src).Length;
            if (len > MiscConst.MaxLooseBytes) throw new InvalidDataException($"{src}: {len} bytes; a loose file is at most {MiscConst.MaxLooseBytes}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            ReplacingFile.Copy(src, dest);
            copied.Add(name);
            bytes += len;
        }
        return $"{copied.Count} files, {bytes / 1024.0:F0} KiB ({string.Join(", ", copied)})" + (missing.Count > 0 ? $"; not in the install: {string.Join(", ", missing)}" : "");
    }

    // ---------------------------------------------------------------- verify

    private static void Fail(string m) => Verify.Fail(m);

    /// <summary>skills.bin against the frozen SkillsLoader: count, order, name and action flag; absent when the install has no skills files.</summary>
    public static void VerifySkills(UOFileManager fm, string outDir)
    {
        var path = Out(outDir, MiscConst.SkillsFile);
        int bad = 0;
        if (!SkillFilesExist(fm))
        {
            if (File.Exists(path)) Fail("skills: the pack has skills.bin, the install has no skills.mul/Skills.idx");
            Console.WriteLine("  skills: none in the install, none in the pack");
        }
        else if (!File.Exists(path)) Fail("skills: skills.bin missing");
        else
        {
            var s = fm.Skills;
            if (s.SkillsCount == 0) s.Load();
            var got = StringListFile.Read(path, MiscConst.MagicSkills);
            if (got.Length != s.SkillsCount) { Fail($"skills: {got.Length} entries, the client has {s.SkillsCount}"); bad++; }
            for (int i = 0; i < Math.Min(got.Length, s.SkillsCount); i++)
            {
                var e = s.Skills[i];
                if (got[i].Text != e.Name || got[i].Value != (e.HasAction ? MiscConst.SkillHasAction : 0u))
                    if (bad++ < 5) Fail($"skills: entry {i} '{got[i].Text}'/{got[i].Value} vs '{e.Name}'/{e.HasAction}");
            }
            Console.WriteLine($"  skills: {got.Length} skills (name, action flag, order) {(bad == 0 ? "EXACT" : "MISMATCH")} vs SkillsLoader");
        }

        var gpath = Out(outDir, MiscConst.SkillGroupsFile);
        bool src = SkillGroupsMul.TryRead(fm.GetUOFilePath("skillgrp.mul"), out var names, out var assignments, out var why);
        if (!src)
        {
            if (File.Exists(gpath)) Fail($"skill groups: the pack has skillgrp.bin, the install's skillgrp.mul is {why}");
            Console.WriteLine($"  skill groups: skillgrp.mul {why}, none in the pack");
            return;
        }
        if (!File.Exists(gpath)) { Fail("skill groups: skillgrp.bin missing"); return; }
        var (gotNames, gotAssign) = SkillGroupsFile.Read(gpath);
        bool same = gotNames.AsSpan().SequenceEqual(names) && gotAssign.AsSpan().SequenceEqual(assignments);
        if (!same) Fail($"skill groups: pack [{string.Join("|", gotNames)}] {gotAssign.Length} assignments vs skillgrp.mul [{string.Join("|", names)}] {assignments.Length}");
        Console.WriteLine($"  skill groups: {names.Length + 1} groups, {assignments.Length} assignments {(same ? "EXACT" : "MISMATCH")} vs skillgrp.mul (frozen LoadMULFile reads)");
    }

    /// <summary>multimap.bin: every slot's bytes against the file the frozen MultiMapLoader opened for it, slot count included.</summary>
    public static void VerifyMultiMap(UOFileManager fm, string outDir)
    {
        var m = fm.MultiMaps;
        m.Load();
        var path = Out(outDir, MiscConst.MultiMapFile);
        var facets = m.FacetPaths;
        if (m.RlePath == null && facets.Length == 0)
        {
            if (File.Exists(path)) Fail("multimap: the pack has multimap.bin, the install has no Multimap.rle or facet0*.mul");
            Console.WriteLine("  multimap: none in the install, none in the pack");
            return;
        }
        if (!File.Exists(path)) { Fail("multimap: multimap.bin missing"); return; }
        var sw = Stopwatch.StartNew();
        using var store = MiscConst.OpenMultiMapStore(path);
        if (store.SlotCount != 1 + facets.Length) Fail($"multimap: {store.SlotCount} slots, expected {1 + facets.Length}");
        int bad = 0;
        long bytes = 0;
        for (int i = 0; i < 1 + facets.Length; i++)
        {
            string? src = i == MiscConst.MultiMapRleSlot ? m.RlePath : facets[i - 1];
            bool has = store.TryGet(i, out _, out var rec);
            if (src == null) { if (has) { Fail($"multimap slot {i}: present, the install has no file"); bad++; } continue; }
            if (!has) { Fail($"multimap slot {i}: missing ({Path.GetFileName(src)})"); bad++; continue; }
            var got = new byte[rec.RawBytes];
            store.Read(rec, got);
            var want = File.ReadAllBytes(src);
            bytes += want.Length;
            if (!got.AsSpan().SequenceEqual(want)) { Fail($"multimap slot {i}: bytes differ from {Path.GetFileName(src)}"); bad++; }
        }
        Console.WriteLine($"  multimap: {(m.RlePath != null ? 1 : 0)} rle + {facets.Length} facet images, {bytes / 1048576.0:F2} MiB {(bad == 0 ? "EXACT" : "MISMATCH")} vs the files MultiMapLoader opens ({sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>text/stringdict.bin against the frozen StringDictionaryLoader, string by string.</summary>
    public static void VerifyStringDict(UOFileManager fm, string outDir)
    {
        var d = fm.StringDictionary;
        if (d.Strings.Length == 0) d.Load();
        var path = Out(outDir, MiscConst.StringDictFile);
        if (d.Strings.Length == 0)
        {
            if (File.Exists(path)) Fail("string dictionary: the pack has one, the client loads no strings");
            Console.WriteLine("  string dictionary: none in the install, none in the pack");
            return;
        }
        if (!File.Exists(path)) { Fail("string dictionary: text/stringdict.bin missing"); return; }
        var got = StringListFile.Read(path, MiscConst.MagicStringDict);
        int bad = got.Length == d.Strings.Length ? 0 : 1;
        if (bad != 0) Fail($"string dictionary: {got.Length} strings, the client has {d.Strings.Length}");
        for (int i = 0; i < Math.Min(got.Length, d.Strings.Length); i++)
            if ((got[i].Text != d.Strings[i] || got[i].Value != 0) && bad++ < 5) Fail($"string dictionary {i}: '{got[i].Text}' vs '{d.Strings[i]}'");
        Console.WriteLine($"  string dictionary: {got.Length} strings {(bad == 0 ? "EXACT" : "MISMATCH")} vs StringDictionaryLoader");
    }

    /// <summary>loose/: every listed file byte-identical to the install's copy, and absent when the install has none.</summary>
    public static void VerifyLoose(UOFileManager fm, string outDir)
    {
        int bad = 0, same = 0;
        foreach (var name in MiscConst.LooseFiles)
        {
            var dest = Out(outDir, MiscConst.LoosePath(name));
            var src = LooseSource(fm, name);
            if (src == null)
            {
                if (File.Exists(dest)) { Fail($"loose: {name} in the pack, not in the install"); bad++; }
                continue;
            }
            if (!File.Exists(dest)) { Fail($"loose: {name} missing"); bad++; continue; }
            if (!File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(src))) { Fail($"loose: {name} differs from {src}"); bad++; }
            else same++;
        }
        var dir = Out(outDir, MiscConst.LooseDir);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir))
                if (!ReplacingFile.IsTempFile(f) && !MiscConst.LooseFiles.Contains(Path.GetFileName(f), StringComparer.Ordinal)) { Fail($"loose: unexpected file {Path.GetFileName(f)}"); bad++; }
        Console.WriteLine($"  loose: {same} files byte-identical, {MiscConst.LooseFiles.Length - same - bad} not in the install {(bad == 0 ? "EXACT" : "MISMATCH")}");
    }
}
