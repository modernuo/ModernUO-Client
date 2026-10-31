using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// ClassicUO's former hard-coded draw rules, applied once at conversion and stored as <see cref="TilePackFlags"/> in
/// tiledata.bin. This is the only code that knows them: clients and the renderer read the flags (<see cref="TileDraw"/>),
/// so a shard admin can hide or show a tile by editing the file. See "NoDraw flags" in docs/modern-client/pack-format.md.
/// Verify checks the result against an independent copy of the old client code (Verify.CheckNoDraw).
/// </summary>
public static class TileRules
{
    public static bool NameNoDraw(string? name) => !string.IsNullOrEmpty(name) && name.StartsWith("nodraw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Land.AllowedToDraw was graphic &gt; 2. ClassicUO does not look at land names (NameNoDraw is informational).</summary>
    public static TilePackFlags Land(int id, in LandTiles t) =>
        (id <= 2 ? TilePackFlags.NoDraw : 0) | (NameNoDraw(t.Name) ? TilePackFlags.NameNoDraw : 0);

    /// <summary>
    /// The static-data part of GameObject.CanBeDrawn (statics, items, multi components, effects); the gargoyle exception
    /// stays at runtime as <see cref="TilePackFlags.NoDrawUnlessGargoyle"/>. Also Item.RefreshGraphicState's multi rule
    /// (a multi item is drawn when its display graphic, the first component, is &gt; 2): <see cref="TilePackFlags.NoDrawAsMulti"/>.
    /// </summary>
    public static TilePackFlags Static(int g, StaticTiles[] stat, ClientVersion version)
    {
        var f = g < stat.Length && NameNoDraw(stat[g].Name) ? TilePackFlags.NameNoDraw : 0;
        if (g <= 2) f |= TilePackFlags.NoDrawAsMulti;
        switch (g)
        {
            case 0x0001: case 0x21BC: case 0xA1FE: case 0xA1FF: case 0xA200: case 0xA201:
                return f | TilePackFlags.NoDraw;
            case 0x9E4C: case 0x9E64: case 0x9E65: case 0x9E7D:
                return f | (g < stat.Length && (stat[g].IsBackground || stat[g].IsSurface) ? TilePackFlags.NoDraw : 0);
        }
        if (g == 0x63D3 || (g >= 0x2198 && g <= 0x21A4)) return f | TilePackFlags.NoDraw;
        // Easel: older clients' tiledata gives it NoDiagonal, which would hide it.
        if (g == 0x0F65 && version < ClientVersion.CV_60144) return f;
        if (g >= stat.Length || (f & TilePackFlags.NameNoDraw) != 0) return f | TilePackFlags.NoDraw;
        if (!stat[g].IsNoDiagonal) return f;
        return f | (stat[g].IsAnimated ? TilePackFlags.NoDrawUnlessGargoyle : TilePackFlags.NoDraw);
    }
}
