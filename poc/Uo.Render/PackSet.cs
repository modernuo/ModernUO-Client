using System.Text.Json;
using Uo.Pack;

namespace Uo.Render;

/// <summary>Tables a host already loaded from the same pack (ClassicUO's loaders), shared instead of read again. Null
/// members are read from the pack.</summary>
public sealed class PackTables
{
    public SpriteEntry[] Sprites, Texmaps;
    public int HueCount;
    public uint[] Hues;
    public AnimBodyEntry[] AnimBodies;
    public AnimDirEntry[] AnimDirs;
    public AnimFrameEntry[] AnimFrames;
    /// <summary>Per static graphic: the host keeps it opaque inside the circle of transparency (ClassicUO: trees).</summary>
    public bool[] StaticNoCot;
}

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
    /// <summary>Host table (<see cref="PackTables.StaticNoCot"/>), or null.</summary>
    public readonly bool[] StaticNoCot;
    /// <summary>Land graphic replacement (0x4000 entries, e.g. the host's season tiles), or null. Set it through
    /// <see cref="WorldRenderer.SetLandRemap"/>, which rebakes the chunks.</summary>
    public ushort[] LandRemap;
    /// <summary>Static graphic replacement (0x10000 entries, e.g. the host's season graphics), or null: the bake draws the
    /// replacement's sprite and tiledata but keeps the original graphic in the object key, so overrides still match. Set
    /// it through <see cref="WorldRenderer.SetStaticRemap"/>, which rebakes the chunks.</summary>
    public ushort[] StaticRemap;
    /// <summary>Bake a <see cref="SpriteInstance.LightWobble"/> copy of each wet flat land tile (the host's animated water).
    /// Set it through <see cref="WorldRenderer.SetWaterWobble"/>, which rebakes the chunks. Not copied by <see cref="WithFacet"/>.</summary>
    public bool WaterWobble;

    /// <summary>Bake tiledata Translucent statics at alpha 178 on the chunk's translucent sub-list (ClassicUO's settled
    /// alpha). Needs a translucent pass that lists them: <see cref="WorldRenderer"/> sets it; consumers that drive
    /// <see cref="Renderer"/> directly without one leave it off, and those statics bake opaque.</summary>
    public bool BakeTranslucentAlpha;

    /// <summary>Logical page id → file. Art pages first, then texmap pages (offset by <see cref="TexPageBase"/>).</summary>
    public readonly string[] PagePaths;
    public readonly int TexPageBase;

    /// <summary>Page pixel format, size and mip count, identical for every page of the pack (mixed packs are rejected).</summary>
    public readonly PixelFormat Format;
    public readonly int PageSize, PageMips;

    /// <summary>Animations (null when the pack has none). <see cref="AnimPageBase"/> is anim page 0's logical id in the GPU
    /// page table. 16-bit anim pages matching the art page format join the art array (appended to <see cref="PagePaths"/>);
    /// Indexed8 pages get their own R8_UINT array (<see cref="AnimPagePaths"/>) plus the palette table.</summary>
    public readonly AnimBodyEntry[] AnimBodies;
    public readonly AnimDirEntry[] AnimDirs;
    public readonly AnimFrameEntry[] AnimFrames;
    public readonly string AnimDir;
    public readonly int AnimPageBase;
    public readonly bool AnimIndexed;
    public readonly string[] AnimPagePaths = [];
    public readonly PixelFormat AnimFormat;
    public readonly int AnimPageSize, AnimPageMips;
    public readonly uint[] FramePalette;
    public readonly ushort[] Palettes;
    public bool HasAnims => AnimBodies != null;

    public readonly int Facet;
    public readonly string FacetDir;
    public readonly int MapWidth, MapHeight, BlocksX, BlocksY, RegionBlocks;

    /// <param name="animRoot">Pack (or its anim/ directory) to take animations from; null = this pack's own.</param>
    public PackSet(string root, int facet, string animRoot = null) : this(root, facet, animRoot, null) { }

    /// <param name="shared">Tables the host already loaded from this pack (see <see cref="PackTables"/>), or null.</param>
    public PackSet(string root, int facet, string animRoot, PackTables shared)
    {
        Root = root;
        Sprites = shared?.Sprites ?? SpriteTable.Read(Path.Combine(root, "art", "sprites.bin"));
        var texPath = Path.Combine(root, "tex", "texmaps.bin");
        Texmaps = shared?.Texmaps ?? (File.Exists(texPath) ? SpriteTable.Read(texPath) : []);
        (Land, Statics) = TileDataFile.Read(Path.Combine(root, "tiledata.bin"));
        if (shared?.Hues != null) (HueCount, Hues) = (shared.HueCount, shared.Hues);
        else (HueCount, Hues) = HueFile.Read(Path.Combine(root, "hues.bin"));
        StaticNoCot = shared?.StaticNoCot;

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

        AnimDir = ResolveAnimDir(animRoot ?? root);
        if (AnimDir != null)
        {
            if (shared?.AnimFrames != null) (AnimBodies, AnimDirs, AnimFrames) = (shared.AnimBodies, shared.AnimDirs, shared.AnimFrames);
            else (AnimBodies, AnimDirs, AnimFrames) = AnimTableFile.Read(Path.Combine(AnimDir, AnimTableFile.FileName));
            int n = CountPages(AnimDir);
            var paths = new string[n];
            for (int i = 0; i < n; i++) paths[i] = PackConst.PagePath(AnimDir, i);
            if (n > 0)
            {
                var h = PageFormats.ReadHeader(paths[0]); // the converter writes one format per anim set
                AnimFormat = h.Format; AnimPageSize = h.Width; AnimPageMips = h.MipCount;
            }
            AnimPageBase = PagePaths.Length;
            if (AnimFormat == PixelFormat.Indexed8)
            {
                AnimIndexed = true;
                AnimPageMips = 1; // mips_NNNN.bin sidecars (variant b) are ignored: mip0 + manual filtering
                AnimPagePaths = paths;
                (FramePalette, Palettes) = AnimPaletteFile.Read(Path.Combine(AnimDir, AnimPaletteFile.FileName));
            }
            else if (n > 0)
            {
                if (AnimFormat != Format || AnimPageSize != PageSize || AnimPageMips != PageMips)
                    throw new InvalidDataException($"{AnimDir}: 16-bit anim pages are {PageFormats.Name(AnimFormat)} {AnimPageSize}/{AnimPageMips} mips but art is " +
                        $"{PageFormats.Name(Format)} {PageSize}/{PageMips}; they share the art array, so they must match (or use Indexed8 anims)");
                PagePaths = [.. PagePaths, .. paths];
            }
        }

        (Facet, FacetDir, MapWidth, MapHeight, BlocksX, BlocksY, RegionBlocks) = ReadFacet(root, facet);
    }

    /// <summary>The same pack (all tables shared) on another facet.</summary>
    public PackSet WithFacet(int facet) => facet == Facet ? this : new PackSet(this, facet);

    PackSet(PackSet o, int facet)
    {
        Root = o.Root; Sprites = o.Sprites; Texmaps = o.Texmaps; Land = o.Land; Statics = o.Statics; HueCount = o.HueCount; Hues = o.Hues; StaticNoCot = o.StaticNoCot; LandRemap = o.LandRemap;
        StaticRemap = o.StaticRemap;
        PagePaths = o.PagePaths; TexPageBase = o.TexPageBase; Format = o.Format; PageSize = o.PageSize; PageMips = o.PageMips;
        AnimBodies = o.AnimBodies; AnimDirs = o.AnimDirs; AnimFrames = o.AnimFrames; AnimDir = o.AnimDir; AnimPageBase = o.AnimPageBase;
        AnimIndexed = o.AnimIndexed; AnimPagePaths = o.AnimPagePaths; AnimFormat = o.AnimFormat; AnimPageSize = o.AnimPageSize;
        AnimPageMips = o.AnimPageMips; FramePalette = o.FramePalette; Palettes = o.Palettes;
        (Facet, FacetDir, MapWidth, MapHeight, BlocksX, BlocksY, RegionBlocks) = ReadFacet(Root, facet);
    }

    static (int, string, int, int, int, int, int) ReadFacet(string root, int facet)
    {
        var dir = Path.Combine(root, "map", facet.ToString());
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "facet.json")));
        var r = doc.RootElement;
        return (facet, dir, Get(r, "width"), Get(r, "height"), Get(r, "blocksX"), Get(r, "blocksY"), Get(r, "regionBlocks", PackConst.RegionBlocks));
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
    /// <summary>Logical pages in the GPU page table: art + tex (+ 16-bit anims), then Indexed8 anim pages.</summary>
    public int TablePageCount => PagePaths.Length + AnimPagePaths.Length;

    static string ResolveAnimDir(string dir)
    {
        if (File.Exists(Path.Combine(dir, AnimTableFile.FileName))) return dir;
        var sub = Path.Combine(dir, "anim");
        return File.Exists(Path.Combine(sub, AnimTableFile.FileName)) ? sub : null;
    }

    /// <summary>Default pack location: the converter's output, else the synthetic pack.</summary>
    public static string FindDefault()
    {
        const string real = @"C:\Repositories\uo-packs\classic";
        if (File.Exists(Path.Combine(real, "manifest.json")) && File.Exists(Path.Combine(real, "art", "sprites.bin")))
            return real;
        return @"C:\Repositories\uo-packs\synthetic";
    }
}
