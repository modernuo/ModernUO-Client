using Uo.Pack;

namespace Uo.Render;

/// <summary>The draw decision for a ground item in the poc client (Uo.Client RenderExtract), matching ClassicUO's
/// Item.AllowedToDraw: a corpse is always drawn, anything else by the pack's draw flags (<see cref="TileDraw.Static"/>) for
/// the player's race.</summary>
public static class GroundItemDraw
{
    /// <summary>ClassicUO Item.IsCorpse.</summary>
    public const int CorpseGraphic = 0x2006;

    public static bool Drawn(StaticTileData[] staticData, int g, bool playerIsGargoyle) =>
        (uint)g < (uint)staticData.Length && (g == CorpseGraphic || TileDraw.Static(staticData[g].PackFlags, playerIsGargoyle));

    /// <summary>ClassicUO Mobile.CheckGraphicChange sets RaceType.GARGOYLE for these bodies; their ghosts keep it.</summary>
    public static bool IsGargoyleBody(int body) => body is 0x029A or 0x029B or 0x02B6 or 0x02B7;
}
