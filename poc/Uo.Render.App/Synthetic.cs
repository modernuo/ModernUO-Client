using Uo.Pack;

namespace Uo.Render;

/// <summary>Writes a small synthetic pack (a few colored sprites, two art pages, one texmap page, 2×2 regions near Britain)
/// so the GPU path can be built before the real converter output exists. <c>Uo.Render --make-synthetic [dir]</c>.</summary>
public static class Synthetic
{
    const int S = PackConst.PageSize;

    public static void Make(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "art"));
        Directory.CreateDirectory(Path.Combine(root, "tex"));
        var sprites = new SpriteEntry[PackConst.StaticSpriteBase + 0x10000];
        for (int i = 0; i < sprites.Length; i++) sprites[i] = SpriteEntry.Absent;

        // --- art page 0: land tiles + a few statics; page 1: more statics (residency test)
        var pages = new uint[2][];
        pages[0] = new uint[S * S];
        pages[1] = new uint[S * S];
        int px = 2, py = 2, rowH = 0;
        int curPage = 0;

        void Place(int id, int w, int h, Func<int, int, uint> pix, int page)
        {
            if (page != curPage) { curPage = page; px = 2; py = 2; rowH = 0; }
            if (px + w + 2 > S) { px = 2; py += rowH + 4; rowH = 0; }
            var dst = pages[page];
            for (int y = -2; y < h + 2; y++)
                for (int x = -2; x < w + 2; x++)
                {
                    int sx = Math.Clamp(x, 0, w - 1), sy = Math.Clamp(y, 0, h - 1);
                    dst[(py + y) * S + px + x] = pix(sx, sy);
                }
            sprites[id] = new SpriteEntry { Page = (ushort)page, X = (ushort)px, Y = (ushort)py, W = (ushort)w, H = (ushort)h, OrigW = (ushort)w, OrigH = (ushort)h, Flags = id < PackConst.StaticSpriteBase ? SpriteFlags.Land : SpriteFlags.None };
            px += w + 4;
            rowH = Math.Max(rowH, h);
        }

        static uint Rgba(int r, int g, int b) => 0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | (uint)r;
        static bool InDiamond(int x, int y) => Math.Abs(x - 21.5f) + Math.Abs(y - 21.5f) <= 22.5f;

        uint[] landColors = [Rgba(60, 140, 50), Rgba(130, 100, 60), Rgba(40, 70, 170), Rgba(120, 120, 120)];
        for (int t = 0; t < landColors.Length; t++)
        {
            var c = landColors[t];
            Place(3 + t, 44, 44, (x, y) => InDiamond(x, y) ? Shade(c, ((x + y) & 7) == 0 ? 0.85f : 1f) : 0u, 0);
        }

        // tree: trunk + crown, 60×100
        Place(PackConst.StaticSpriteBase + 0x100, 60, 100, (x, y) =>
        {
            if (y > 70 && x > 26 && x < 34) return Rgba(90, 60, 30);
            float dx = x - 30, dy = y - 38;
            return dx * dx + dy * dy < 30 * 30 ? Shade(Rgba(30, 110, 40), 0.7f + 0.3f * ((x ^ y) & 3) / 3f) : 0u;
        }, 0);
        // wall facing south-west: a parallelogram on the left half of the tile, 44×80
        Place(PackConst.StaticSpriteBase + 0x101, 44, 80, (x, y) =>
        {
            int top = 58 - x / 2 - 58 + 22 + 0; // slope
            int yt = 22 - x / 2 + 0, yb = 80 - x / 2;
            return x < 22 && y >= yt + 0 && y < yb ? Rgba(170, 160, 140) : (x >= 22 && y >= x / 2 - 11 && y < 80 - (43 - x) / 2 - 0 ? Rgba(140, 130, 115) : 0u);
        }, 0);
        // gray barrel (hue target), 30×36
        Place(PackConst.StaticSpriteBase + 0x102, 30, 36, (x, y) =>
        {
            int g = 80 + x * 5;
            return Rgba(g, g, g);
        }, 0);
        // partial hue: gray body with a red band
        Place(PackConst.StaticSpriteBase + 0x103, 30, 50, (x, y) => y > 20 && y < 26 ? Rgba(200, 30, 30) : Rgba(90 + y * 3, 90 + y * 3, 90 + y * 3), 0);
        // page 1 statics: rock + bush
        Place(PackConst.StaticSpriteBase + 0x200, 40, 30, (x, y) => (x - 20) * (x - 20) / 400f + (y - 15) * (y - 15) / 225f < 1 ? Rgba(110, 105, 100) : 0u, 1);
        Place(PackConst.StaticSpriteBase + 0x201, 36, 30, (x, y) => (x - 18) * (x - 18) / 324f + (y - 15) * (y - 15) / 225f < 1 ? Rgba(50, 150, 60) : 0u, 1);

        for (int p = 0; p < pages.Length; p++)
            AtlasPage.Write(PackConst.PagePath(Path.Combine(root, "art"), p), S, PackConst.PageMipCount, BuildChain(pages[p]));
        SpriteTable.Write(Path.Combine(root, "art", "sprites.bin"), sprites);

        // --- texmap page: one 64×64 grass texture as texmap 1
        var tex = new uint[S * S];
        var texEntries = new SpriteEntry[16];
        for (int i = 0; i < texEntries.Length; i++) texEntries[i] = SpriteEntry.Absent;
        for (int y = -2; y < 66; y++)
            for (int x = -2; x < 66; x++)
            {
                int sx = Math.Clamp(x, 0, 63), sy = Math.Clamp(y, 0, 63);
                tex[(2 + y) * S + 2 + x] = Shade(landColors[0], ((sx * 7 + sy * 13) % 11) / 30f + 0.8f);
            }
        texEntries[1] = new SpriteEntry { Page = 0, X = 2, Y = 2, W = 64, H = 64, OrigW = 64, OrigH = 64, Flags = SpriteFlags.Texmap };
        AtlasPage.Write(PackConst.PagePath(Path.Combine(root, "tex"), 0), S, PackConst.PageMipCount, BuildChain(tex));
        SpriteTable.Write(Path.Combine(root, "tex", "texmaps.bin"), texEntries);

        // --- tiledata
        var land = new LandTileData[PackConst.LandTileCount];
        land[3].TexId = 1;
        land[5].Flags = 0x80; // wet
        var stat = new StaticTileData[0x10000];
        stat[0x100] = new StaticTileData { Height = 20 };
        stat[0x101] = new StaticTileData { Height = 20, Flags = 0x10 };
        stat[0x102] = new StaticTileData { Height = 5 };
        stat[0x103] = new StaticTileData { Height = 5, Flags = 0x00040000 };
        stat[0x200] = new StaticTileData { Height = 0, Flags = 0x1 };
        stat[0x201] = new StaticTileData { Height = 0, Flags = 0x20000 };
        TileDataFile.Write(Path.Combine(root, "tiledata.bin"), land, stat);

        // --- hues: 3000 entries of a 15-bit ramp toward a per-entry color (hues_raw.bin; the renderer builds the LUT)
        const int hueCount = 3000;
        var hues = new HueEntry[hueCount];
        for (int h = 0; h < hueCount; h++)
        {
            var rnd = new Random(h);
            int r = rnd.Next(32), g = rnd.Next(32), b = rnd.Next(32);
            for (int s = 0; s < HueLut.Shades; s++)
                hues[h].Colors[s] = (ushort)((r * s / 31) << 10 | (g * s / 31) << 5 | (b * s / 31));
        }
        HueRawFile.Write(Path.Combine(root, TableConst.HueRawFile), hues);

        // --- facet 0, regions (2..3, 2..3) = tiles 1024..2047
        var facetDir = Path.Combine(root, "map", "0");
        Directory.CreateDirectory(facetDir);
        File.WriteAllText(Path.Combine(facetDir, "facet.json"), "{ \"width\": 7168, \"height\": 4096, \"blocksX\": 896, \"blocksY\": 512, \"regionBlocks\": 64 }");
        for (int ry = 2; ry <= 3; ry++)
            for (int rx = 2; rx <= 3; rx++)
                WriteRegion(facetDir, rx, ry);

        var m = new Manifest { SourcePath = "synthetic", Created = DateTime.UtcNow };
        m.Facets.Add(new FacetInfo { Index = 0, Width = 7168, Height = 4096, BlocksX = 896, BlocksY = 512 });
        ManifestFileIo.Write(Path.Combine(root, "manifest.json"), m);
        Console.WriteLine($"synthetic pack written to {root}");
    }

    static void WriteRegion(string facetDir, int rx, int ry)
    {
        const int RB = PackConst.RegionBlocks;
        var blocks = new BlockData[RB * RB];
        var statics = new List<StaticCell>();
        for (int by = 0; by < RB; by++)
            for (int bx = 0; bx < RB; bx++)
            {
                int gbx = rx * RB + bx, gby = ry * RB + by;
                var landCells = new LandCell[64];
                statics.Clear();
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int tx = gbx * 8 + x, ty = gby * 8 + y;
                        double hill = Math.Sin(tx * 0.05) * Math.Cos(ty * 0.04) * 12;
                        int z = (int)Math.Round(Math.Max(0, hill));
                        ushort tile = (ushort)(((tx / 16 + ty / 16) % 7 == 0) ? 4 : 3);
                        if (Math.Abs((tx % 64) - 32) < 3) { tile = 6; z = 0; } // stone road
                        if (hill < -9) { tile = 5; z = -5; } // water
                        landCells[y * 8 + x] = new LandCell { TileId = tile, Z = (sbyte)z };

                        int h = Hash(tx, ty);
                        bool road = tile == 6;
                        // small walled "houses" on a 32-tile grid
                        int hx = tx % 32, hy = ty % 32;
                        if (!road && hx >= 8 && hx <= 14 && hy >= 8 && hy <= 14 && (hx == 8 || hy == 8 || hx == 14 || hy == 14))
                            statics.Add(new StaticCell { Graphic = 0x101, X = (byte)x, Y = (byte)y, Z = (sbyte)z });
                        else if (!road && tile == 3 && h % 23 == 0)
                            statics.Add(new StaticCell { Graphic = 0x100, X = (byte)x, Y = (byte)y, Z = (sbyte)z });
                        else if (!road && tile == 3 && h % 31 == 1)
                            statics.Add(new StaticCell { Graphic = 0x200, X = (byte)x, Y = (byte)y, Z = (sbyte)z, });
                        else if (!road && tile == 3 && h % 37 == 2)
                            statics.Add(new StaticCell { Graphic = 0x201, X = (byte)x, Y = (byte)y, Z = (sbyte)z });
                        else if (road && h % 17 == 3)
                            statics.Add(new StaticCell { Graphic = (ushort)(h % 2 == 0 ? 0x102 : 0x103), X = (byte)x, Y = (byte)y, Z = (sbyte)z, Hue = (ushort)(1 + h % 2000) });
                    }
                blocks[by * RB + bx] = new BlockData(landCells, statics.ToArray());
            }
        var header = new RegionHeader { Facet = 0, Rx = (ushort)rx, Ry = (ushort)ry, BlocksW = RB, BlocksH = RB };
        RegionFile.Write(PackConst.RegionPath(facetDir, rx, ry), header, blocks);
    }

    static int Hash(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (int)(h & 0x7FFFFFFF);
    }

    static uint Shade(uint c, float f)
    {
        int r = (int)Math.Min(255, (c & 0xFF) * f), g = (int)Math.Min(255, ((c >> 8) & 0xFF) * f), b = (int)Math.Min(255, ((c >> 16) & 0xFF) * f);
        return 0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | (uint)r;
    }

    /// <summary>Box-filter mip chain (straight alpha, alpha-weighted color), mip0 first.</summary>
    public static byte[] BuildChain(uint[] mip0)
    {
        var chain = new byte[AtlasPage.ChainBytes(S, PackConst.PageMipCount)];
        Buffer.BlockCopy(mip0, 0, chain, 0, mip0.Length * 4);
        int off = 0;
        for (int m = 1; m < PackConst.PageMipCount; m++)
        {
            int ss = S >> (m - 1), ds = S >> m;
            int srcOff = off, dstOff = off + ss * ss * 4;
            for (int y = 0; y < ds; y++)
                for (int x = 0; x < ds; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int si = srcOff + (((y * 2 + (k >> 1)) * ss) + x * 2 + (k & 1)) * 4;
                        int pa = chain[si + 3];
                        r += chain[si] * pa; g += chain[si + 1] * pa; b += chain[si + 2] * pa; a += pa;
                    }
                    int di = dstOff + (y * ds + x) * 4;
                    if (a > 0) { chain[di] = (byte)(r / a); chain[di + 1] = (byte)(g / a); chain[di + 2] = (byte)(b / a); }
                    chain[di + 3] = (byte)(a / 4);
                }
            off = dstOff;
        }
        return chain;
    }
}
