using System.Buffers.Binary;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// The facets a conversion must produce and their sizes, worked out from the source files alone (map size, ModernUO's
/// facet heights; the static index size is reported when it disagrees; a UOP map's one trailing block is ignored), without reading the pack or ClassicUO's facet
/// table, so a pack cut to the wrong width or missing a facet fails verification before any block is compared. Source
/// precedence is restated here, not shared with <see cref="FacetSources"/>: UOP over MUL on a UOP installation, never an
/// x file, and Trammel from map0 below 6.0.0.0 (ModernUO's default).
/// </summary>
public static class SourceFacets
{
    public sealed record Expected(int Facet, int FileIndex, string MapPath, bool Uop, int Width, int Height, string? Problem, string? Note);

    // ModernUO Distribution/Data/map-definitions.json: Felucca, Trammel, Ilshenar, Malas, Tokuno, TerMur.
    private static readonly int[] Heights = [4096, 4096, 1600, 2048, 1448, 4096];
    private const int BlockBytes = 196;

    public static string? MapPath(UOFileManager fm, int facet, out bool uop, out int fileIndex)
    {
        int fi = fileIndex = facet == 1 && fm.Version < ClientVersion.CV_6000 ? 0 : facet;
        string P(string n) => fm.GetUOFilePath(n);
        uop = fm.IsUOPInstallation && File.Exists(P($"map{fi}LegacyMUL.uop"));
        string normal = uop ? P($"map{fi}LegacyMUL.uop") : P($"map{fi}.mul");
        return File.Exists(normal) ? normal : null;
    }

    public static List<Expected> For(UOFileManager fm, ICollection<int>? only)
    {
        var list = new List<Expected>();
        for (int facet = 0; facet < Math.Min(MapLoader.MAPS_COUNT, Heights.Length); facet++)
        {
            if (only != null && !only.Contains(facet)) continue;
            var map = MapPath(fm, facet, out bool uop, out int fi);
            if (map == null) continue;
            long bytes = uop ? UopBytes(map, fi) : new FileInfo(map).Length;
            int bh = Heights[facet] / 8;
            string? problem = null, note = null;
            long blocks = bytes / BlockBytes;
            // The stock LegacyMUL.uop maps (7.0.103.0) hold one block more than the facet (e.g. 458,753 for 7168x4096;
            // their staidx has 458,752 entries); that one trailing block is not a column.
            if (uop && blocks % bh == 1) blocks--;
            if (bytes % BlockBytes != 0) problem = $"{Path.GetFileName(map)}: {bytes} B is not a whole number of 196-byte blocks";
            else if (blocks % bh != 0) problem = $"{Path.GetFileName(map)}: {blocks} blocks is not a whole number of {bh}-block columns";
            string idx = fm.GetUOFilePath($"staidx{fi}.mul");
            if (File.Exists(idx) && new FileInfo(idx).Length / 12 != blocks)
                note = $"{Path.GetFileName(idx)} holds {new FileInfo(idx).Length / 12} entries for {blocks} map blocks";
            list.Add(new Expected(facet, fi, map, uop, (int)(blocks / bh) * 8, Heights[facet], problem, note));
        }

        return list;
    }

    /// <summary>Total length of the map entries of a LegacyMUL.uop.</summary>
    private static long UopBytes(string path, int fileIndex)
    {
        using var uop = new ClassicUO.IO.UOFileUop(path, $"build/map{fileIndex}legacymul/{{0:D8}}.dat");
        uop.FillEntries();
        long total = 0;
        foreach (var e in uop.Entries)
            if (e.Offset > 0 && e.Length > 0) total += e.Length;
        return total;
    }
}

/// <summary>
/// Independent reader of a MUL facet for the verifier: map/staidx/statics plus the mapdif/stadif diffs, applied the way
/// ModernUO's TileMatrixPatch does (land diffs below 6.0.0.0, statics diffs below 7.0.9.0, each family when its own
/// files exist, every listed block, later entries win, a stadifi entry with offset &lt; 0 or length &lt;= 0 empties the
/// block), and Trammel read from map0 below 6.0.0.0. Source precedence (UOP first, no x files) is restated, not shared.
/// It shares no code with the converter's FacetSources/FacetTable or ClassicUO's MapLoader, so a pack built through
/// them is checked against a second reading. Its deviations from ModernUO: ClassicUO's 1,024-static cap, and an index
/// entry pointing outside its file reads as empty.
/// </summary>
public sealed class MulMapOracle
{
    private const int BlockBytes = 4 + 64 * 3, StaticBytes = 7, MaxStatics = 1024; // MaxStatics: ClassicUO's cap
    private readonly (byte[] Buf, int Off)[] _land;
    private readonly (byte[] Buf, int Off, int Count)[] _statics;
    private static readonly byte[] Empty = [];

    public int LandPatches { get; }
    public int StaticPatches { get; }
    public string MapFile { get; }

    private MulMapOracle(string mapFile, int landPatches, int staticPatches, (byte[], int)[] land, (byte[], int, int)[] statics)
    {
        MapFile = mapFile;
        _land = land;
        _statics = statics;
        LandPatches = landPatches;
        StaticPatches = staticPatches;
    }

    private static (byte[], int, int) StaticEntry(byte[] file, int pos, int len)
    {
        // ModernUO: offset < 0 || length <= 0 is an empty block. Checked signed, before any count is computed.
        if (pos < 0 || len <= 0) return (Empty, 0, 0);
        int count = len / StaticBytes;
        if (count == 0 || pos + (long)count * StaticBytes > file.Length) return (Empty, 0, 0);
        return (file, pos, Math.Min(MaxStatics, count));
    }

    /// <summary>Null when the facet's selected map is not a MUL file (UOP is chosen first on a UOP installation), or
    /// there is no map. <paramref name="blocksX"/>/<paramref name="blocksY"/> come from <see cref="SourceFacets"/>.</summary>
    public static MulMapOracle? TryOpen(UOFileManager fm, int facet, int blocksX, int blocksY)
    {
        int file = facet == 1 && fm.Version < ClientVersion.CV_6000 ? 0 : facet;
        bool Has(string name) => File.Exists(fm.GetUOFilePath(name));
        if (fm.IsUOPInstallation && Has($"map{file}LegacyMUL.uop")) return null;
        string mapName = $"map{file}.mul";
        if (!Has(mapName)) return null;

        byte[] Read(string name) => Has(name) ? File.ReadAllBytes(fm.GetUOFilePath(name)) : Empty;
        var map = Read(mapName);
        bool staticsPair = Has($"staidx{file}.mul") && Has($"statics{file}.mul");
        var idx = staticsPair ? Read($"staidx{file}.mul") : Empty;
        var sta = staticsPair ? Read($"statics{file}.mul") : Empty;

        int n = blocksX * blocksY;
        var land = new (byte[], int)[n];
        var statics = new (byte[], int, int)[n];
        for (int b = 0; b < n; b++)
        {
            land[b] = (long)(b + 1) * BlockBytes <= map.Length ? (map, b * BlockBytes + 4) : (Empty, 0);
            statics[b] = (Empty, 0, 0);
            if ((long)(b + 1) * 12 > idx.Length) continue;
            statics[b] = StaticEntry(sta, BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(b * 12)), BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(b * 12 + 4)));
        }

        int landPatches = 0, staticPatches = 0;
        if (ClientLoad.MapDiffs && fm.Version < ClientVersion.CV_6000 && Has($"mapdifl{facet}.mul") && Has($"mapdif{facet}.mul"))
        {
            var difl = Read($"mapdifl{facet}.mul");
            var dif = Read($"mapdif{facet}.mul");
            for (int i = 0; i + 4 <= difl.Length && (long)(i / 4 + 1) * BlockBytes <= dif.Length; i += 4)
            {
                int block = BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(i));
                if ((uint)block >= (uint)n) continue;
                land[block] = (dif, i / 4 * BlockBytes + 4);
                landPatches++;
            }
        }

        if (ClientLoad.MapDiffs && fm.Version < ClientVersion.CV_7090 && Has($"stadifl{facet}.mul") && Has($"stadifi{facet}.mul") && Has($"stadif{facet}.mul"))
        {
            var difl = Read($"stadifl{facet}.mul");
            var difi = Read($"stadifi{facet}.mul");
            var dif = Read($"stadif{facet}.mul");
            for (int i = 0; i + 4 <= difl.Length && (i / 4 + 1) * 12 <= difi.Length; i += 4)
            {
                int block = BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(i));
                if ((uint)block >= (uint)n) continue;
                statics[block] = StaticEntry(dif, BinaryPrimitives.ReadInt32LittleEndian(difi.AsSpan(i * 3)), BinaryPrimitives.ReadInt32LittleEndian(difi.AsSpan(i * 3 + 4)));
                staticPatches++;
            }
        }

        return new MulMapOracle(mapName, landPatches, staticPatches, land, statics);
    }

    /// <summary>True when the pack block equals the oracle's block (land ids and z; statics after ClassicUO's
    /// Chunk.Load filter, in stored order, with z and hue).</summary>
    public bool Matches(int block, in BlockData packed)
    {
        var (lb, lo) = _land[block];
        if (lb.Length == 0) return packed.IsEmpty;
        if (packed.IsEmpty) return false;
        for (int c = 0; c < 64; c++)
        {
            int p = lo + c * 3;
            var cell = packed.Land[c];
            if (cell.TileId != (ushort)((lb[p] | (lb[p + 1] << 8)) & 0x3FFF) || cell.Z != (sbyte)lb[p + 2]) return false;
        }

        var (sb, so, count) = _statics[block];
        int k = 0;
        for (int i = 0; i < count; i++)
        {
            long p = so + (long)i * StaticBytes;
            if (p + StaticBytes > sb.Length) break;
            ushort g = (ushort)(sb[p] | (sb[p + 1] << 8));
            byte x = sb[p + 2], y = sb[p + 3];
            if (g == 0 || g == 0xFFFF || x > 7 || y > 7) continue;
            if (k >= packed.Statics.Length) return false;
            var s = packed.Statics[k++];
            if (s.Graphic != g || s.X != x || s.Y != y || s.Z != (sbyte)sb[p + 4] || s.Hue != (ushort)(sb[p + 5] | (sb[p + 6] << 8))) return false;
        }

        return k == packed.Statics.Length;
    }
}
