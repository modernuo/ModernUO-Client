using System.Buffers.Binary;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;

namespace Uo.Converter;

/// <summary>The files one facet is converted from. Chosen once per facet by <see cref="FacetSources.Resolve"/> and
/// used for the whole conversion: base map, base statics, then the diffs on top.</summary>
public sealed record FacetFiles(int Facet, int FileIndex, string Map, bool MapIsUop, string? StaIdx, string? Statics);

/// <summary>
/// Source selection for a facet, the converter's own (it no longer goes through ClassicUO's MapLoader.LoadMap /
/// ApplyPatches, whose patch loader opened the five
/// diff files only together, and whose unsigned staidx length took 0xFFFFFFFF for 1,024 statics).
/// <list type="bullet">
/// <item>File index: Trammel (facet 1) reads map0/staidx0/statics0 below 6.0.0.0 even when map1 exists, as ModernUO
/// does by default (TileMatrix: <c>Pre6000ClientSupport &amp;&amp; mapID == 1 ? 0 : fileIndex</c>,
/// <c>maps.enablePre6000Trammel</c>); its diffs are still mapdif1/stadif1 (TileMatrixPatch uses the map's fileIndex).</item>
/// <item>Map: <c>map{N}LegacyMUL.uop</c> on a UOP installation, else <c>map{N}.mul</c> (ClassicUO's precedence).</item>
/// <item>Statics: <c>staidx{N}.mul</c> + <c>statics{N}.mul</c>.</item>
/// <item>The x files (<c>map{N}x</c>, <c>staidx{N}x</c>, <c>statics{N}x</c>) are never read (owner, 2026-10-31): a shard
/// that wants them renames them to the regular names before importing.</item>
/// </list>
/// </summary>
public static class FacetSources
{
    public static int FileIndex(ClientVersion version, int facet) => facet == 1 && version < ClientVersion.CV_6000 ? 0 : facet;

    public static FacetFiles? Resolve(UOFileManager fm, int facet)
    {
        int f = FileIndex(fm.Version, facet);
        string P(string name) => fm.GetUOFilePath(name);
        string? map = null;
        bool uop = false;
        if (fm.IsUOPInstallation && File.Exists(P($"map{f}LegacyMUL.uop")))
        {
            uop = true;
            map = P($"map{f}LegacyMUL.uop");
        }
        else if (File.Exists(P($"map{f}.mul")))
        {
            map = P($"map{f}.mul");
        }

        if (map == null) return null;
        string idx = P($"staidx{f}.mul"), sta = P($"statics{f}.mul");
        bool hasStatics = File.Exists(idx) && File.Exists(sta);
        return new FacetFiles(facet, f, map, uop, hasStatics ? idx : null, hasStatics ? sta : null);
    }
}

/// <summary>
/// The block table of one facet: where each block's land and statics come from, after the diffs. Built from
/// <see cref="FacetFiles"/> only; ClassicUO's loader contributes nothing but the facet size.
/// </summary>
public sealed class FacetTable
{
    public const int BlockBytes = 4 + 64 * 3, IndexBytes = 12, StaticBytes = 7, MaxStatics = 1024; // MaxStatics: ClassicUO's cap
    public const byte None = 0, SrcMap = 1, SrcStatics = 2, SrcMapDif = 3, SrcStaDif = 4;

    public FacetFiles Files { get; }
    public int BlocksX { get; }
    public int BlocksY { get; }
    /// <summary>File contents by source id (index 0 unused).</summary>
    public byte[][] Sources { get; } = new byte[5][];
    public byte[] LandSrc { get; }
    public long[] LandOff { get; }
    public byte[] StaSrc { get; }
    public int[] StaOff { get; }
    public ushort[] StaCount { get; }
    public int LandPatches { get; private set; }
    public int StaticPatches { get; private set; }
    /// <summary>staidx/stadifi entries refused by <see cref="TryStaticRange"/> although they were not the empty marker.</summary>
    public int BadStaticEntries { get; private set; }

    private FacetTable(FacetFiles files, int bx, int by)
    {
        Files = files;
        BlocksX = bx;
        BlocksY = by;
        int n = bx * by;
        LandSrc = new byte[n];
        LandOff = new long[n];
        StaSrc = new byte[n];
        StaOff = new int[n];
        StaCount = new ushort[n];
        for (int i = 0; i < Sources.Length; i++) Sources[i] = [];
    }

    /// <summary>
    /// A staidx/stadifi entry as ModernUO reads it (signed offset and length; <c>offset &lt; 0 || length &lt;= 0</c> is an
    /// empty block), and in range of the statics file it points into. Offset 0 is valid (the first block of the file).
    /// The count is capped at 1,024 only after the entry is accepted, so 0xFFFFFFFF (-1) can no longer become 1,024
    /// records from the start of the file.
    /// </summary>
    public static bool TryStaticRange(int offset, int length, long fileLength, out int count)
    {
        count = 0;
        if (offset < 0 || length <= 0) return false;
        long records = length / StaticBytes;
        if (records == 0 || offset + records * StaticBytes > fileLength) return false;
        count = (int)Math.Min(MaxStatics, records);
        return true;
    }

    /// <summary>ModernUO's empty block: a negative offset (0xFFFFFFFF) or a length &lt;= 0 (0, or 0xFFFFFFFF read signed).</summary>
    private static bool IsEmptyMarker(int offset, int length) => offset < 0 || length <= 0;

    /// <param name="mapDiffs">apply mapdif/stadif as ModernUO does (land below 6.0.0.0, statics below 7.0.9.0).</param>
    public static FacetTable Build(UOFileManager fm, FacetFiles files, int blocksX, int blocksY, bool mapDiffs)
    {
        var t = new FacetTable(files, blocksX, blocksY);
        int n = blocksX * blocksY;
        var map = t.Sources[SrcMap] = File.ReadAllBytes(files.Map);

        if (files.MapIsUop)
        {
            // ClassicUO's UOP map layout: entry k of build/map{N}legacymul/{k:D8}.dat holds blocks 4096k..4096k+4095
            // A block outside its entry, or a missing entry, has no land.
            using var uop = new UOFileUop(files.Map, $"build/map{files.FileIndex}legacymul/{{0:D8}}.dat");
            uop.FillEntries();
            for (int b = 0; b < n; b++)
            {
                t.LandOff[b] = -1;
                int e = b >> 12;
                if (e >= uop.Entries.Length) continue;
                var entry = uop.Entries[e];
                long rel = (long)(b & 4095) * BlockBytes;
                if (entry.Offset <= 0 || rel + BlockBytes > entry.Length || entry.Offset + rel + BlockBytes > map.Length) continue;
                t.LandSrc[b] = SrcMap;
                t.LandOff[b] = entry.Offset + rel;
            }
        }
        else
        {
            for (int b = 0; b < n; b++)
            {
                bool ok = (long)(b + 1) * BlockBytes <= map.Length;
                t.LandSrc[b] = ok ? SrcMap : None;
                t.LandOff[b] = ok ? (long)b * BlockBytes : -1;
            }
        }

        if (files.StaIdx != null && files.Statics != null)
        {
            var idx = File.ReadAllBytes(files.StaIdx);
            var sta = t.Sources[SrcStatics] = File.ReadAllBytes(files.Statics);
            for (int b = 0; b < n && (long)(b + 1) * IndexBytes <= idx.Length; b++)
            {
                int off = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(b * IndexBytes));
                int len = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(b * IndexBytes + 4));
                if (TryStaticRange(off, len, sta.Length, out int c)) t.SetStatics(b, SrcStatics, off, c);
                else if (!IsEmptyMarker(off, len)) t.BadStaticEntries++;
            }
        }

        if (mapDiffs) t.ApplyDiffs(fm);
        return t;
    }

    private void SetStatics(int block, byte src, int off, int count)
    {
        StaSrc[block] = src;
        StaOff[block] = off;
        StaCount[block] = (ushort)count;
    }

    /// <summary>
    /// ModernUO's TileMatrixPatch: the land family (mapdifl + mapdif) below 6.0.0.0 and the statics family (stadifl +
    /// stadifi + stadif) below 7.0.9.0, each applied when its own files exist, independently of the other. Every listed
    /// block, in order, so later entries win. A stadifi entry with a negative offset or a length &lt;= 0 empties the
    /// block (deletion), so a deletion-only set with an empty stadif is applied too. An entry that points outside
    /// stadif also empties the block (ModernUO would read past the end).
    /// </summary>
    private void ApplyDiffs(UOFileManager fm)
    {
        int n = BlocksX * BlocksY, facet = Files.Facet;
        string P(string name) => fm.GetUOFilePath(name);
        if (fm.Version < ClientVersion.CV_6000 && File.Exists(P($"mapdifl{facet}.mul")) && File.Exists(P($"mapdif{facet}.mul")))
        {
            var difl = File.ReadAllBytes(P($"mapdifl{facet}.mul"));
            var dif = Sources[SrcMapDif] = File.ReadAllBytes(P($"mapdif{facet}.mul"));
            int count = Math.Min(difl.Length / 4, dif.Length / BlockBytes);
            for (int j = 0; j < count; j++)
            {
                int block = BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(j * 4));
                if ((uint)block >= (uint)n) continue;
                LandSrc[block] = SrcMapDif;
                LandOff[block] = (long)j * BlockBytes;
                LandPatches++;
            }
        }

        if (fm.Version < ClientVersion.CV_7090 && File.Exists(P($"stadifl{facet}.mul")) && File.Exists(P($"stadifi{facet}.mul")) && File.Exists(P($"stadif{facet}.mul")))
        {
            var difl = File.ReadAllBytes(P($"stadifl{facet}.mul"));
            var difi = File.ReadAllBytes(P($"stadifi{facet}.mul"));
            var dif = Sources[SrcStaDif] = File.ReadAllBytes(P($"stadif{facet}.mul"));
            int count = Math.Min(difl.Length / 4, difi.Length / IndexBytes);
            for (int j = 0; j < count; j++)
            {
                int block = BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(j * 4));
                if ((uint)block >= (uint)n) continue;
                int off = BinaryPrimitives.ReadInt32LittleEndian(difi.AsSpan(j * IndexBytes));
                int len = BinaryPrimitives.ReadInt32LittleEndian(difi.AsSpan(j * IndexBytes + 4));
                if (TryStaticRange(off, len, dif.Length, out int c)) SetStatics(block, SrcStaDif, off, c);
                else
                {
                    SetStatics(block, None, 0, 0);
                    if (!IsEmptyMarker(off, len)) BadStaticEntries++;
                }

                StaticPatches++;
            }
        }
    }
}
