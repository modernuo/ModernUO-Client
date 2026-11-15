using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// Reading experimental Indexed8 anim pages back: each frame's cell (rect plus gutter) is looked up in that frame's
/// palette from anim/palettes.bin. Texels outside every cell must be index 0.
/// </summary>
public static class IndexedPages
{
    public sealed class AnimInfo
    {
        public AnimFrameEntry[] Frames = [];
        public uint[] FramePalette = [];
        public ushort[] Palettes = [];
        /// <summary>Per page: distinct cells (sprite rect, palette id).</summary>
        public Dictionary<int, List<(SpriteEntry E, uint Pal)>> Cells = new();
    }

    private static readonly ConcurrentDictionary<string, AnimInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static AnimInfo Info(string animDir) => Cache.GetOrAdd(Path.GetFullPath(animDir), d =>
    {
        var (_, _, frames) = AnimTableFile.Read(Path.Combine(d, AnimTableFile.FileName));
        var (fp, pal) = AnimPaletteFile.Read(Path.Combine(d, AnimPaletteFile.FileName));
        var info = new AnimInfo { Frames = frames, FramePalette = fp, Palettes = pal };
        var seen = new HashSet<(int, int, int)>();
        for (int i = 0; i < frames.Length; i++)
        {
            var e = frames[i].Sprite;
            if (!e.Present || !seen.Add((e.Page, e.X, e.Y))) continue;
            if (!info.Cells.TryGetValue(e.Page, out var l)) info.Cells[e.Page] = l = new();
            l.Add((e, fp[i]));
        }
        return info;
    });

    public static int PageOf(string path) => int.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(5));

    /// <summary>B5G5R5A1 mip0 rebuilt from indices + palettes; <paramref name="strayIndices"/> counts non-zero texels outside cells.</summary>
    public static ushort[] Reconstruct16(string animDir, int page, ReadOnlySpan<byte> idx, int s, out long strayIndices)
    {
        const int G = PackConst.Gutter;
        var info = Info(animDir);
        var rec = new ushort[s * s];
        var covered = new bool[s * s];
        if (info.Cells.TryGetValue(page, out var cells))
            foreach (var (e, pal) in cells)
            {
                var p = info.Palettes.AsSpan((int)pal * AnimPaletteFile.Entries, AnimPaletteFile.Entries);
                for (int y = e.Y - G; y < e.Y + e.H + G; y++)
                    for (int x = e.X - G; x < e.X + e.W + G; x++)
                    {
                        int i = y * s + x;
                        rec[i] = p[idx[i]];
                        covered[i] = true;
                    }
            }
        strayIndices = 0;
        for (int i = 0; i < rec.Length; i++) if (!covered[i] && idx[i] != 0) strayIndices++;
        return rec;
    }

    /// <summary>PageCodec.ReadMip for Indexed8 mip0: RGBA8 via the palettes (ClassicUO 5->8 expansion).</summary>
    public static uint[] Expand(string pagePath, ReadOnlySpan<byte> idx, int s)
    {
        var rec = Reconstruct16(Path.GetDirectoryName(pagePath)!, PageOf(pagePath), idx, s, out _);
        return PageCodec.DecodeB5G5R5A1(MemoryMarshal.AsBytes(rec.AsSpan()));
    }
}
