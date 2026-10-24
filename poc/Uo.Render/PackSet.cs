using System.Text.Json;
using Uo.Pack;

namespace Uo.Render;

/// <summary>Everything the renderer reads from a pack at startup (small tables). Pages and regions stream later.</summary>
public sealed class PackSet
{
    public readonly string Root;
    public readonly SpriteEntry[] Sprites;
    public readonly SpriteEntry[] Texmaps;
    public readonly LandTileData[] Land;
    public readonly StaticTileData[] Statics;
    public readonly int HueCount;
    public readonly uint[] Hues;

    /// <summary>Logical page id → file. Art pages first, then texmap pages (offset by <see cref="TexPageBase"/>).</summary>
    public readonly string[] PagePaths;
    public readonly int TexPageBase;

    /// <summary>Page pixel format, size and mip count, identical for every page of the pack (mixed packs are rejected).</summary>
    public readonly PixelFormat Format;
    public readonly int PageSize, PageMips;

    public readonly int Facet;
    public readonly string FacetDir;
    public readonly int MapWidth, MapHeight, BlocksX, BlocksY, RegionBlocks;

    public PackSet(string root, int facet)
    {
        Root = root;
        Sprites = SpriteTable.Read(Path.Combine(root, "art", "sprites.bin"));
        var texPath = Path.Combine(root, "tex", "texmaps.bin");
        Texmaps = File.Exists(texPath) ? SpriteTable.Read(texPath) : [];
        (Land, Statics) = TileDataFile.Read(Path.Combine(root, "tiledata.bin"));
        (HueCount, Hues) = HueFile.Read(Path.Combine(root, "hues.bin"));

        var artPages = CountPages(Path.Combine(root, "art"));
        var texPages = CountPages(Path.Combine(root, "tex"));
        TexPageBase = artPages;
        PagePaths = new string[artPages + texPages];
        for (int i = 0; i < artPages; i++) PagePaths[i] = PackConst.PagePath(Path.Combine(root, "art"), i);
        for (int i = 0; i < texPages; i++) PagePaths[artPages + i] = PackConst.PagePath(Path.Combine(root, "tex"), i);

        // One pack = one format: the Texture2DArray has a single format, so every page must match.
        for (int i = 0; i < PagePaths.Length; i++)
        {
            var h = PageFormats.ReadHeader(PagePaths[i]);
            if (i == 0) { Format = h.Format; PageSize = h.Width; PageMips = h.MipCount; continue; }
            if (h.Format != Format || h.Width != PageSize || h.Height != PageSize || h.MipCount != PageMips)
                throw new InvalidDataException($"mixed pack: {PagePaths[i]} is {PageFormats.Name(h.Format)} {h.Width}x{h.Height}/{h.MipCount} mips, " +
                    $"page 0 is {PageFormats.Name(Format)} {PageSize}/{PageMips}. One pack must use one page format.");
        }
        if (PagePaths.Length == 0) { Format = PixelFormat.Rgba8; PageSize = PackConst.PageSize; PageMips = PackConst.PageMipCount; }
        _ = PageFormats.Gpu(Format); // throws for unknown formats

        Facet = facet;
        FacetDir = Path.Combine(root, "map", facet.ToString());
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FacetDir, "facet.json")));
        var r = doc.RootElement;
        MapWidth = Get(r, "width");
        MapHeight = Get(r, "height");
        BlocksX = Get(r, "blocksX");
        BlocksY = Get(r, "blocksY");
        RegionBlocks = Get(r, "regionBlocks", PackConst.RegionBlocks);
    }

    static int Get(JsonElement e, string name, int? fallback = null)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetInt32();
        return fallback ?? throw new InvalidDataException($"facet.json: missing {name}");
    }

    static int CountPages(string dir)
    {
        int n = 0;
        while (File.Exists(PackConst.PagePath(dir, n))) n++;
        return n;
    }

    public int PageCount => PagePaths.Length;

    /// <summary>Default pack location: the converter's output, else the synthetic pack.</summary>
    public static string FindDefault()
    {
        const string real = @"C:\Repositories\uo-packs\classic";
        if (File.Exists(Path.Combine(real, "manifest.json")) && File.Exists(Path.Combine(real, "art", "sprites.bin")))
            return real;
        return @"C:\Repositories\uo-packs\synthetic";
    }
}
