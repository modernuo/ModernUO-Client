using System.Buffers.Binary;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// Independent reader of a MUL facet for the verifier: map/staidx/statics plus the mapdif/stadif diffs, applied the way
/// ModernUO's TileMatrixPatch does (land diffs below 6.0.0.0, statics diffs below 7.0.9.0, every listed block, later
/// entries win), and Trammel read from map0 when an old install has no map1. It shares no code with ClassicUO's
/// MapLoader or ClientLoad.ApplyLegacyMapRules, so a pack built through them is checked against a second reading.
/// </summary>
public sealed class MulMapOracle
{
    private const int BlockBytes = 4 + 64 * 3, StaticBytes = 7, MaxStatics = 1024; // MaxStatics: ClassicUO's cap
    private readonly (byte[] Buf, int Off)[] _land;
    private readonly (byte[] Buf, int Off, int Count)[] _statics;
    private static readonly byte[] Empty = [];

    public int LandPatches { get; }
    public int StaticPatches { get; }

    private MulMapOracle(int blocks, int landPatches, int staticPatches, (byte[], int)[] land, (byte[], int, int)[] statics)
    {
        _land = land;
        _statics = statics;
        LandPatches = landPatches;
        StaticPatches = staticPatches;
    }

    /// <summary>Null when the facet is not a MUL facet (UOP map, or missing files).</summary>
    public static MulMapOracle? TryOpen(UOFileManager fm, int facet, int blocksX, int blocksY)
    {
        int file = facet;
        if (!File.Exists(fm.GetUOFilePath($"map{facet}.mul")))
        {
            if (facet != 1 || fm.Version >= ClientVersion.CV_6000 || File.Exists(fm.GetUOFilePath("map1LegacyMUL.uop"))) return null;
            file = 0;
        }

        byte[] Read(string name) => File.Exists(fm.GetUOFilePath(name)) ? File.ReadAllBytes(fm.GetUOFilePath(name)) : Empty;
        var map = Read($"map{file}.mul");
        var idx = Read($"staidx{file}.mul");
        var sta = Read($"statics{file}.mul");
        if (map.Length == 0) return null;

        int n = blocksX * blocksY;
        var land = new (byte[], int)[n];
        var statics = new (byte[], int, int)[n];
        for (int b = 0; b < n; b++)
        {
            land[b] = (long)(b + 1) * BlockBytes <= map.Length ? (map, b * BlockBytes + 4) : (Empty, 0);
            statics[b] = (Empty, 0, 0);
            if ((long)(b + 1) * 12 > idx.Length) continue;
            uint pos = BinaryPrimitives.ReadUInt32LittleEndian(idx.AsSpan(b * 12));
            int len = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(b * 12 + 4));
            if (len > 0 && pos != 0xFFFF_FFFF) statics[b] = (sta, (int)pos, Math.Min(MaxStatics, len / StaticBytes));
        }

        int landPatches = 0, staticPatches = 0;
        if (ClientLoad.MapDiffs && fm.Version < ClientVersion.CV_6000)
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

        if (ClientLoad.MapDiffs && fm.Version < ClientVersion.CV_7090)
        {
            var difl = Read($"stadifl{facet}.mul");
            var difi = Read($"stadifi{facet}.mul");
            var dif = Read($"stadif{facet}.mul");
            for (int i = 0; i + 4 <= difl.Length && (i / 4 + 1) * 12 <= difi.Length; i += 4)
            {
                int block = BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(i));
                if ((uint)block >= (uint)n) continue;
                uint pos = BinaryPrimitives.ReadUInt32LittleEndian(difi.AsSpan(i * 3));
                int len = BinaryPrimitives.ReadInt32LittleEndian(difi.AsSpan(i * 3 + 4));
                statics[block] = len > 0 && pos != 0xFFFF_FFFF ? (dif, (int)pos, Math.Min(MaxStatics, len / StaticBytes)) : (Empty, 0, 0);
                staticPatches++;
            }
        }

        return new MulMapOracle(n, landPatches, staticPatches, land, statics);
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
