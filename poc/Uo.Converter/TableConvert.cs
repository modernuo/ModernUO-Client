using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>Game data tables: tiledata (the single file, with the converter-derived draw flags), raw hues, radarcol, multis,
/// lights, tileart appearances, Equipconv.def.</summary>
public static class TableConvert
{
    public static string TileData(UOFileManager fm, string outDir)
    {
        var land = fm.TileData.LandData;
        var stat = fm.TileData.StaticData;
        var names = new string?[land.Length + stat.Length];
        for (int i = 0; i < land.Length; i++) names[i] = land[i].Name;
        for (int i = 0; i < stat.Length; i++) names[land.Length + i] = stat[i].Name;
        var table = TileDataFile.BuildNames(names, out var refs);
        var l = new LandTileInfo[land.Length];
        for (int i = 0; i < l.Length; i++)
            l[i] = new LandTileInfo
            {
                Flags = (ulong)land[i].Flags, TexId = land[i].TexID, NameOffset = refs[i].Offset, NameLength = refs[i].Length,
                PackFlags = TileRules.Land(i, land[i]),
            };
        var s = new StaticTileInfo[stat.Length];
        for (int i = 0; i < s.Length; i++)
        {
            ref readonly var t = ref stat[i];
            var r = refs[land.Length + i];
            s[i] = new StaticTileInfo
            {
                Flags = (ulong)t.Flags, Weight = t.Weight, Layer = t.Layer, Count = t.Count, AnimId = t.AnimID, Hue = t.Hue,
                LightIndex = t.LightIndex, Height = t.Height, NameOffset = r.Offset, NameLength = r.Length,
                PackFlags = TileRules.Static(i, stat, fm.Version),
            };
        }
        var path = Path.Combine(outDir, TableConst.TileDataFile);
        TileDataFile.Write(path, l, s, table);
        // tiledata_ext.bin is retired (tiledata.bin is the only tile data file). Drop a stale copy, e.g. one hard-linked
        // from an older pack into an --only output, so the manifest does not list it. Deleting a hard link leaves the source intact.
        PackOutputFiles.Delete(Path.Combine(outDir, TableConst.TileDataExtFileRetired));
        return $"{l.Length} land, {s.Length} statics, names {table.Length} B; {new FileInfo(path).Length} B";
    }

    public static string HuesRaw(UOFileManager fm, string outDir)
    {
        var groups = fm.Hues.HuesRange;
        var e = new HueEntry[groups.Length * 8];
        for (int g = 0; g < groups.Length; g++)
            for (int k = 0; k < 8; k++)
            {
                ref var src = ref groups[g].Entries[k];
                ref var dst = ref e[g * 8 + k];
                for (int c = 0; c < 32; c++) dst.Colors[c] = src.ColorTable[c];
                dst.TableStart = src.TableStart;
                dst.TableEnd = src.TableEnd;
                unsafe
                {
                    fixed (byte* n = src.Name)
                        for (int c = 0; c < 20; c++) dst.Name[c] = n[c];
                }
            }
        var path = Path.Combine(outDir, TableConst.HueRawFile);
        HueRawFile.Write(path, e);
        // hues.bin is retired (the LUT is built from hues_raw.bin at load). Drop a stale copy, e.g. one hard-linked from an
        // older pack into an --only output, so the manifest does not list it. Deleting a hard link leaves the source intact.
        var legacy = Path.Combine(outDir, HueFile.LegacyFileName);
        PackOutputFiles.Delete(legacy);
        return $"{e.Length} hues; {new FileInfo(path).Length} B";
    }

    public static string RadarCol(UOFileManager fm, string outDir)
    {
        var path = Path.Combine(outDir, TableConst.RadarColFile);
        RadarColFile.Write(path, fm.Hues.RadarCol);
        return $"{fm.Hues.RadarCol.Length} colours; {new FileInfo(path).Length} B";
    }

    /// <summary>MultiLoader.GetMultis for every id with an index entry (MultiCollection.uop on UOP installs, else multi.mul).</summary>
    public static string Multis(UOFileManager fm, string outDir)
    {
        var loader = fm.Multis;
        var entries = MultiEntries(loader);
        int count = 0;
        for (int i = 0; i < entries.Length; i++) if (HasMulti(entries, i)) count = i + 1;
        var multis = new MultiEntry[count];
        var comps = new List<MultiComponent>();
        int present = 0;
        for (int id = 0; id < count; id++)
        {
            if (!HasMulti(entries, id)) continue;
            var list = loader.GetMultis((uint)id);
            multis[id] = new MultiEntry { FirstComponent = (uint)comps.Count, ComponentCount = (uint)list.Count };
            foreach (var m in list)
                comps.Add(new MultiComponent { Graphic = m.ID, X = m.X, Y = m.Y, Z = m.Z, Flags = m.IsVisible ? MultiComponentFlags.Visible : 0 });
            if (list.Count > 0) present++;
        }
        var path = Path.Combine(outDir, TableConst.MultisFile);
        MultiFile.Write(path, multis, comps.ToArray());
        bool uop = fm.IsUOPInstallation && File.Exists(fm.GetUOFilePath("MultiCollection.uop"));
        return $"{(uop ? "MultiCollection.uop" : "multi.mul")}: {count} ids, {present} multis, {comps.Count} components; {new FileInfo(path).Length} B";
    }

    /// <summary>The loader's index entries (MultiLoader.File is internal), loading it first if needed.</summary>
    public static UOFileIndex[] MultiEntries(MultiLoader loader)
    {
        var prop = typeof(MultiLoader).GetProperty("File", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        if (prop.GetValue(loader) == null) loader.Load();
        return ((UOFile)prop.GetValue(loader)!).Entries;
    }

    public static bool HasMulti(UOFileIndex[] entries, int id)
    {
        ref readonly var e = ref entries[id];
        return e.Offset >= 0 && e.Length > 0 && e.Offset != 0x0000_0000_FFFF_FFFF;
    }

    /// <summary>light.mul texels per lightidx.mul entry, as LightsLoader.GetLight reads them.</summary>
    public static string Lights(UOFileManager fm, string outDir)
    {
        var loader = fm.Lights;
        if (loader.File == null) loader.Load();
        var src = File.ReadAllBytes(fm.GetUOFilePath("light.mul"));
        var file = loader.File!;
        var lights = new LightEntry[file.Entries.Length];
        var data = new MemoryStream();
        int present = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            ref var e = ref file.GetValidRefEntry(i);
            if (e.Width <= 0 || e.Height <= 0) continue;
            int n = e.Width * e.Height;
            if (e.Offset + n > src.Length) throw new InvalidDataException($"light {i} past the end of light.mul");
            lights[i] = new LightEntry { Offset = (uint)data.Length, Width = (ushort)e.Width, Height = (ushort)e.Height };
            data.Write(src, (int)e.Offset, n);
            present++;
        }
        var path = Path.Combine(outDir, TableConst.LightsFile);
        LightFile.Write(path, lights, data.ToArray());
        return $"{lights.Length} ids, {present} lights, {data.Length} texels; {new FileInfo(path).Length} B";
    }

    /// <summary>tileart.uop: the subtype-0 appearance map of every item with more than one appearance subtype (TileArtInfo.TryGetAppearance).</summary>
    public static string TileArt(UOFileManager fm, string outDir)
    {
        var path = Path.Combine(outDir, TableConst.TileArtFile);
        if (!File.Exists(fm.GetUOFilePath("tileart.uop")))
        {
            TileArtFile.Write(path, []);
            return "no tileart.uop";
        }
        var res = TileArtEntries(fm, out int items);
        TileArtFile.Write(path, res);
        return $"{items} items, {res.Length} appearances; {new FileInfo(path).Length} B";
    }

    public static TileArtAppearance[] TileArtEntries(UOFileManager fm, out int items)
    {
        using var own = new UOFileManager(fm.Version, fm.BasePath);
        own.TileArt.Load();
        var res = new List<TileArtAppearance>();
        items = 0;
        for (int g = 0; g <= ushort.MaxValue; g++)
        {
            if (!own.TileArt.TryGetTileArtInfo((uint)g, out var info) || info.Appearances.Count <= 1 || !info.Appearances.TryGetValue(0, out var map)) continue;
            items++;
            foreach (var (body, app) in map)
            {
                if (body > ushort.MaxValue) throw new InvalidDataException($"tileart {g}: body {body}");
                res.Add(new TileArtAppearance { Graphic = (ushort)g, Body = (ushort)body, Appearance = app });
            }
        }
        res.Sort((a, b) => a.Graphic != b.Graphic ? a.Graphic.CompareTo(b.Graphic) : a.Body.CompareTo(b.Body));
        return res.ToArray();
    }

    /// <summary>Port of AnimationsLoader.ProcessEquipConvDef: later rows replace earlier ones for the same (body, graphic).</summary>
    public static string EquipConv(UOFileManager fm, string outDir)
    {
        var map = new SortedDictionary<uint, EquipConvEntry>();
        var file = fm.GetUOFilePath("Equipconv.def");
        if (fm.Version >= ClientVersion.CV_300 && File.Exists(file))
        {
            using var r = new DefReader(file, 5);
            while (r.Next())
            {
                ushort body = (ushort)r.ReadInt();
                ushort graphic = (ushort)r.ReadInt();
                ushort newGraphic = (ushort)r.ReadInt();
                int gump = r.ReadInt();
                if (gump > ushort.MaxValue) continue;
                if (gump == 0) gump = graphic;
                else if (gump == 0xFFFF || gump == -1) gump = newGraphic;
                ushort color = (ushort)r.ReadInt();
                map[(uint)body << 16 | graphic] = new EquipConvEntry { Body = body, Graphic = graphic, NewGraphic = newGraphic, Gump = (ushort)gump, Color = color };
            }
        }
        var arr = map.Values.ToArray();
        var path = Path.Combine(outDir, TableConst.EquipConvFile);
        EquipConvFile.Write(path, arr);
        return $"{arr.Length} rows, {arr.Select(e => e.Body).Distinct().Count()} bodies; {new FileInfo(path).Length} B";
    }
}
