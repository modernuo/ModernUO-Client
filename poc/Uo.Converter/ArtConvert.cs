using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// Per-thread readers. ClassicUO's UOFile readers keep a seek position, so they are not thread-safe; each worker owns
/// a private UOFileManager whose Arts/Texmaps files are opened separately (memory-mapped, so this is cheap) and whose
/// index entries are copied from the fully-initialised main manager (which applied art.def / TexTerr.def).
/// </summary>
public sealed class WorkerReaders : IDisposable
{
    public readonly UOFileManager Fm;

    public WorkerReaders(UOFileManager main)
    {
        Fm = new UOFileManager(main.Version, main.BasePath);
        Fm.Arts.Load();
        CopyEntries(main.Arts.File, Fm.Arts.File);
        Fm.Texmaps.Load();
        CopyEntries(main.Texmaps.File, Fm.Texmaps.File);
    }

    private static void CopyEntries(UOFile src, UOFile dst)
    {
        if (src.Entries.Length != dst.Entries.Length) throw new InvalidOperationException("entry count mismatch");
        for (int i = 0; i < src.Entries.Length; i++)
        {
            var e = src.Entries[i];
            if (e.File == src) e.File = dst;               // .mul entries point at their own file
            else if (e.File != null) throw new InvalidOperationException("index entry backed by a foreign file - not supported");
            dst.Entries[i] = e;
        }
    }

    public void Dispose() => Fm.Dispose();
}

public static class ArtConvert
{
    public sealed record Stats(int TableSize, int Present, int Unique, int Aliased, int Empty, int Pages, double Occupancy, long Bytes, long BytesBrotliQ5);

    private static ThreadLocal<WorkerReaders> Workers(UOFileManager fm) => new(() => new WorkerReaders(fm), trackAllValues: true);

    public static Stats Art(UOFileManager fm, string outDir)
    {
        var dir = Path.Combine(outDir, "art");
        var land = fm.TileData.LandData;
        var stat = fm.TileData.StaticData;
        int tableSize = PackConst.StaticSpriteBase + stat.Length;
        var file = fm.Arts.File;

        // Phase A: find which ids exist and which are aliases of an earlier id (same bytes in the archive).
        var canonical = new int[tableSize];
        var seen = new Dictionary<(bool, long, int), int>();
        for (int id = 0; id < tableSize; id++)
        {
            canonical[id] = -1;
            ref var e = ref file.GetValidRefEntry(id);
            if (e.Length <= 0) continue;
            var key = (id < PackConst.StaticSpriteBase, e.Offset, e.Length);
            canonical[id] = seen.TryGetValue(key, out var first) ? first : (seen[key] = id);
        }

        var images = new SpriteImage?[tableSize];
        var preset = new SpriteEntry[tableSize];
        for (int i = 0; i < tableSize; i++) preset[i] = SpriteEntry.Absent;

        using (var workers = Workers(fm))
        {
            Parallel.For(0, tableSize, ConvertOptions.Par, id =>
            {
                if (canonical[id] != id) return;
                var art = workers.Value!.Fm.Arts.GetArt((uint)id);
                if (art.Pixels.IsEmpty || art.Width <= 0 || art.Height <= 0) return;
                var flags = Flags(id, land, stat);
                var img = SpriteImage.Trim(id, art.Pixels, art.Width, art.Height, flags);
                if (img == null)
                {
                    // Exists in the archive but is fully transparent: absent, but keep the original size.
                    preset[id].OrigW = (ushort)art.Width;
                    preset[id].OrigH = (ushort)art.Height;
                    return;
                }
                images[id] = img;
            });
            foreach (var w in workers.Values) w.Dispose();
        }

        DedupeByContent(images, canonical);
        var list = new List<SpriteImage>();
        int empty = 0;
        for (int id = 0; id < tableSize; id++)
        {
            int c = canonical[id];
            if (c < 0) continue;
            while (canonical[c] != c) c = canonical[c]; // content-dedupe may chain aliases
            if (c == id) { if (images[id] != null) list.Add(images[id]!); else empty++; continue; }
            if (images[c] == null) { preset[id] = preset[c]; empty++; continue; }
            list.Add(new SpriteImage { Id = id, AliasOf = c, Flags = Flags(id, land, stat) });
        }

        var res = AtlasBuilder.Build(dir, tableSize, list, preset, [0x0003, 0x4000 + 0x0CCA, 0x4000 + 0x0E75, 0x4000 + 0x0FAF, 0x4000 + 0x0064, 0x7191]);
        SpriteTable.Write(Path.Combine(dir, "sprites.bin"), res.Entries);
        return new Stats(tableSize, res.Packed + res.Aliased, res.Packed, res.Aliased, empty, res.PageCount, res.Occupancy, res.PageBytes, res.PageBytesBrotli);
    }

    /// <summary>
    /// Pixel-identical sprites (the many "UNUSED" / "NO DRAW" placeholders, recolour-free duplicates) share one atlas
    /// rect: later ids become aliases of the first. Trim and original size must match too so anchoring stays exact.
    /// </summary>
    private static void DedupeByContent(SpriteImage?[] images, int[] canonical)
    {
        var byHash = new Dictionary<ulong, List<int>>();
        for (int id = 0; id < images.Length; id++)
        {
            var img = images[id];
            if (img == null || canonical[id] != id) continue;
            var hc = new HashCode();
            hc.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.Pixels.AsSpan()));
            hc.Add(img.W); hc.Add(img.H); hc.Add(img.TrimX); hc.Add(img.TrimY); hc.Add(img.OrigW); hc.Add(img.OrigH);
            ulong h = (uint)hc.ToHashCode();
            if (!byHash.TryGetValue(h, out var ids)) { byHash[h] = [id]; continue; }
            int match = -1;
            foreach (var o in ids)
            {
                var a = images[o]!;
                if (a.W == img.W && a.H == img.H && a.TrimX == img.TrimX && a.TrimY == img.TrimY && a.OrigW == img.OrigW && a.OrigH == img.OrigH
                    && a.Pixels.AsSpan().SequenceEqual(img.Pixels)) { match = o; break; }
            }
            if (match < 0) { ids.Add(id); continue; }
            images[id] = null;
            canonical[id] = match;
        }
    }

    private static SpriteFlags Flags(int id, LandTiles[] land, StaticTiles[] stat)
    {
        if (id < PackConst.StaticSpriteBase)
        {
            var f = SpriteFlags.Land;
            if (id < land.Length && (land[id].Flags & TileFlag.PartialHue) != 0) f |= SpriteFlags.PartialHue;
            return f;
        }
        int g = id - PackConst.StaticSpriteBase;
        return g < stat.Length && stat[g].IsPartialHue ? SpriteFlags.PartialHue : SpriteFlags.None;
    }

    public static Stats Tex(UOFileManager fm, string outDir)
    {
        var dir = Path.Combine(outDir, "tex");
        var file = fm.Texmaps.File;
        int tableSize = file.Entries.Length;
        var canonical = new int[tableSize];
        var seen = new Dictionary<(long, int), int>();
        for (int id = 0; id < tableSize; id++)
        {
            canonical[id] = -1;
            ref var e = ref file.GetValidRefEntry(id);
            if (e.Length <= 0) continue;
            var key = (e.Offset, e.Length);
            canonical[id] = seen.TryGetValue(key, out var first) ? first : (seen[key] = id);
        }

        var images = new SpriteImage?[tableSize];
        using (var workers = Workers(fm))
        {
            Parallel.For(0, tableSize, ConvertOptions.Par, id =>
            {
                if (canonical[id] != id) return;
                var t = workers.Value!.Fm.Texmaps.GetTexmap((uint)id);
                if (t.Pixels.IsEmpty) return;
                images[id] = SpriteImage.Trim(id, t.Pixels, t.Width, t.Height, SpriteFlags.Texmap);
            });
            foreach (var w in workers.Values) w.Dispose();
        }

        DedupeByContent(images, canonical);
        var list = new List<SpriteImage>();
        int empty = 0;
        for (int id = 0; id < tableSize; id++)
        {
            int c = canonical[id];
            if (c < 0) continue;
            while (canonical[c] != c) c = canonical[c]; // content-dedupe may chain aliases
            if (images[c] == null) { empty++; continue; }
            list.Add(c == id ? images[id]! : new SpriteImage { Id = id, AliasOf = c, Flags = SpriteFlags.Texmap });
        }

        var res = AtlasBuilder.Build(dir, tableSize, list, null, [0x0003]);
        SpriteTable.Write(Path.Combine(dir, "texmaps.bin"), res.Entries);
        return new Stats(tableSize, res.Packed + res.Aliased, res.Packed, res.Aliased, empty, res.PageCount, res.Occupancy, res.PageBytes, res.PageBytesBrotli);
    }

    public static int Hues(UOFileManager fm, string outDir)
    {
        var groups = fm.Hues.HuesRange;
        int count = groups.Length * 8;
        var rgba = new uint[count * HueFile.Shades];
        for (int g = 0; g < groups.Length; g++)
            for (int e = 0; e < 8; e++)
                for (int s = 0; s < HueFile.Shades; s++)
                    rgba[(g * 8 + e) * HueFile.Shades + s] = HuesHelper.Color16To32(groups[g].Entries[e].ColorTable[s]) | 0xFF000000;
        HueFile.Write(Path.Combine(outDir, "hues.bin"), count, rgba);
        return count;
    }

    public static (int Land, int Statics) TileData(UOFileManager fm, string outDir)
    {
        var land = fm.TileData.LandData;
        var stat = fm.TileData.StaticData;
        var l = new LandTileData[land.Length];
        for (int i = 0; i < l.Length; i++) l[i] = new LandTileData { Flags = (ulong)land[i].Flags, TexId = land[i].TexID, PackFlags = LandPackFlags(i, land[i]) };
        var s = new StaticTileData[stat.Length];
        for (int i = 0; i < s.Length; i++)
            s[i] = new StaticTileData { Flags = (ulong)stat[i].Flags, Height = stat[i].Height, Layer = stat[i].Layer, PackFlags = StaticPackFlags(i, stat, fm.Version) };
        TileDataFile.Write(Path.Combine(outDir, "tiledata.bin"), l, s);
        return (l.Length, s.Length);
    }

    public static bool NameNoDraw(string? name) => !string.IsNullOrEmpty(name) && name.StartsWith("nodraw", StringComparison.OrdinalIgnoreCase);

    // Land.AllowedToDraw = graphic > 2. ClassicUO does not look at land names.
    public static TilePackFlags LandPackFlags(int id, in LandTiles t) =>
        (id <= 2 ? TilePackFlags.NoDraw : 0) | (NameNoDraw(t.Name) ? TilePackFlags.NameNoDraw : 0);

    /// <summary>Port of the static-data part of GameObject.CanBeDrawn. The gargoyle exception is left to the client.</summary>
    public static TilePackFlags StaticPackFlags(int g, StaticTiles[] stat, ClientVersion version)
    {
        var f = g < stat.Length && NameNoDraw(stat[g].Name) ? TilePackFlags.NameNoDraw : 0;
        switch (g)
        {
            case 0x0001: case 0x21BC: case 0xA1FE: case 0xA1FF: case 0xA200: case 0xA201:
                return f | TilePackFlags.NoDraw;
            case 0x9E4C: case 0x9E64: case 0x9E65: case 0x9E7D:
                return f | (g < stat.Length && (stat[g].IsBackground || stat[g].IsSurface) ? TilePackFlags.NoDraw : 0);
        }
        if (g == 0x63D3 || (g >= 0x2198 && g <= 0x21A4)) return f | TilePackFlags.NoDraw;
        if (g == 0x0F65 && version < ClientVersion.CV_60144) return f;
        if (g >= stat.Length || (f & TilePackFlags.NameNoDraw) != 0) return f | TilePackFlags.NoDraw;
        if (!stat[g].IsNoDiagonal) return f;
        return f | (stat[g].IsAnimated ? TilePackFlags.NoDrawUnlessGargoyle : TilePackFlags.NoDraw);
    }
}
