namespace Uo.Render;

/// <summary>
/// clip-2 depth model (switch depth_model, sprite.hlsl GeoDepth/PSMainGeo mirror this): the depth of a sprite pixel is
/// X + Y of the 3D surface point it shows (bigger = nearer). In UO's projection, screen x = (X − Y)·22 and screen
/// y = (X + Y)·22 − 22 − 4Z (world pixels, the tile's north vertex at (x − y)·22, (x + y)·22 − 22 − 4z), so every world
/// point on one screen pixel lies on (X, Y, Z) + t·(1, 1, 5.5) and X + Y orders them. One number per sprite cannot order
/// a mobile against both the floor it stands on (the feet must win) and a wall one diagonal ahead (the wall must win):
/// per-pixel depth by shape does.
/// <list type="bullet">
/// <item>flat (land, and statics of height 0 that are Background or Surface): the plane at z, (sy + 22 + 4z) / 22, lowered by
/// <see cref="FlatDrop"/>, but never behind its own tile's back edges (x + y + |s|, see <see cref="FlatFloored"/>). A mobile's
/// rows below its centre (feet, hooves, a titan's legs) hang over the floors ahead; the mobile is capped by its own tile's
/// front faces so walls on those edges hide them, and the drop lets them still beat the floors. Flats keep their order among
/// themselves; a non-flat less than FlatDrop · 5.5 z below a flat (same pixel) wins;</item>
/// <item>wall (tiledata Wall): vertical faces on the tile's back edges (north-east Y = y, north-west X = x, corners both):
/// x + y + |s| with s = (sx − north vertex column) / 22. Orientation free: each half of the art can only be one edge;</item>
/// <item>box (every other static, and items, multi pieces and captured statics with the same layout): over its tile from z
/// to z + height: max(back edges, min(top plane, centre)), with back = x + y + |s| (art above the top diamond, e.g. tree
/// crowns, stays a vertical plane on the back edges) and centre = x + y + 1, the tile centre's plane, where the front faces
/// (x + y + 2 − |s|) would be. clip-3: a mobile standing on or in a box on its own tile (base z at or under the mobile's
/// z) is drawn over it, as the depth key does (bench6-000027: the player at Felucca 1451,1666 z 10 in item 0x0F6A,
/// height 12); with the front faces the box beat the mobile's billboard (also x + y + 1). The clamp only lowers values
/// above x + y + 1, which no mobile but the one on the box's own tile reaches, and walls on the box's front edges win
/// there with or without it;</item>
/// <item>mobile composite (body, mount and equipment share one anchor): a billboard through the tile centre (B = x + y + 1)
/// above the ground, the ground plane below the centre row (feet and hooves lie on the floor, so they beat it by the bias),
/// capped by the front faces of the tile under the column, B + |1 − (|s| mod 2)|: the own tile's B + 1 − |s|, and beside it
/// (wide mounts and creatures, |s| over 1) its side neighbours', which have the same tile sum (they lose to walls on those
/// edges, and tie the back edges of the floors ahead, which they beat by the bias).</item>
/// </list>
/// Ties (one plane) are broken by a small bias: walls over mobiles over flats and boxes, then z, then (baked land and
/// statics) the key's rank. Within a mobile composite every layer has the same depth, so the depth test (LESS) keeps the
/// first emitted layer, as the equal keys did.
/// </summary>
public static class GeoDepth
{
    /// <summary>Tie biases in tile sums. The target depth resolves about 1.5e-4 (GeoScale 1/2500 near 0.5), so ZBias and
    /// RankBias stay above it; RankBias · 7 stays under one ZBias step.</summary>
    public const float WallBias = 0.02f, MobileBias = 0.01f, ZBias = 2e-3f, RankBias = 2.5e-4f;

    /// <summary>Tile sums flats (land, floors) sit behind their plane (sprite.hlsl FLAT_DROP).</summary>
    public const float FlatDrop = 1.5f;

    /// <summary>Tile sums a buried static (not a flat, top at or under its tile's land: SpriteInstance.ZGeoBuried) sits behind
    /// its shape, as the flats do, so the land in front keeps covering it (a lower storey's walls under the ground, Felucca
    /// 1451-1454 1669 z -11 under land z 10, poked through the land ahead in bench6-000021's out poses). A static only partly
    /// under the ground keeps its shape: its buried part less than FlatDrop · 5.5 z under the land ahead still shows.</summary>
    public const float BuriedDrop = FlatDrop;

    /// <summary>Tile sums a wet flat (water, SpriteInstance.ZGeoWet) sits behind its plane with wet_surface_depth on (sprite.hlsl
    /// WET_DROP). FlatDrop lets feet beat slightly higher floors ahead; for water above a mobile it put the waterline 33 px
    /// (1.5 tile sums) too low (Felucca 1675 1705, z -15 under water at z -5: 7 px above the feet instead of 40). This small
    /// drop puts it 2.2 px low; a mobile at the water's own z still wins by MobileBias on its own tile's front faces, and
    /// loses only its rows more than 2.2 px under its centre row outside them (FlatDrop kept 33 px there).</summary>
    public const float WetDrop = 0.1f;

    /// <summary>A flat at height <paramref name="z"/> under world pixel row <paramref name="sy"/>, without its floor (see
    /// <see cref="FlatFloored"/>).</summary>
    public static float Flat(float sy, int z) => Plane(sy, z) - FlatDrop;

    /// <summary>
    /// render-order-1: a flat over tile (tx, ty) at z, lowered by <paramref name="drop"/> but never behind its tile's back edges:
    /// max(plane − drop, x + y + |s|). The drop is for the mobile rows that hang over the floor ahead; they are capped at their
    /// own tile's front faces, which are the floor ahead's back edges, so the floor ties them there and loses by MobileBias.
    /// Without the floor, art of the tile behind that hangs over this tile (a wall's base and foot, Moonglow bank's parapets at
    /// 4462 1159-1166 z 20 over the roof at 4463 z 20) beat the floor in a sawtooth, one diamond per tile: the wall's back
    /// edge depth is up to 2 more than the floor's plane there, and FlatDrop took 1.5 of it. The floor is at most the plane
    /// (inside the diamond, x + y + |s| ≤ plane), so a flat never moves in front of its own surface.
    /// </summary>
    public static float FlatFloored(int tx, int ty, int z, float sx, float sy, float drop = FlatDrop) =>
        MathF.Max(Plane(sy, z) - drop, Wall(tx, ty, sx));

    /// <summary>Tile sum of the plane at height <paramref name="z"/> under world pixel row <paramref name="sy"/>.</summary>
    public static float Plane(float sy, int z) => (sy + 22f + 4f * z) / 22f;

    /// <summary>A wall on tile (tx, ty) at world pixel column <paramref name="sx"/>.</summary>
    public static float Wall(int tx, int ty, float sx) => tx + ty + MathF.Abs(sx / 22f - (tx - ty));

    /// <summary>A box over tile (tx, ty) from z to z + <paramref name="height"/> at world pixel (sx, sy).</summary>
    public static float Box(int tx, int ty, int z, int height, float sx, float sy)
    {
        float s = MathF.Abs(sx / 22f - (tx - ty));
        float back = tx + ty + s, centre = tx + ty + 1f;
        float top = Plane(sy, z + height);
        return MathF.Max(back, MathF.Min(top, centre));
    }

    /// <summary>A mobile composite whose anchor (MobileView's draw point: tile centre, 3 rows up) is at world pixel
    /// (ax, ay), at height <paramref name="z"/>, seen at world pixel (sx, sy).</summary>
    public static float Mobile(float ax, float ay, int z, float sx, float sy)
    {
        float cy = ay + 3f;
        float b = Plane(cy, z);
        float ground = b + (sy - cy) / 22f;
        return MathF.Max(b, MathF.Min(ground, b + FrontFaces((sx - ax) / 22f)));
    }

    /// <summary>The front faces of the row of tiles a mobile stands in (its own tile and its side neighbours, one tile sum) at
    /// column <paramref name="s"/> tiles from its anchor, over the billboard: 1 at a tile's centre column, 0 at its side vertices.
    /// render-order-1: beside its own tile (|s| over 1) a mobile's lower rows used to stop at the billboard, so the floors ahead,
    /// floored at their back edges (<see cref="FlatFloored"/>), covered a wide mount's side hooves.</summary>
    public static float FrontFaces(float s) => MathF.Abs(1f - Mod2(MathF.Abs(s)));

    private static float Mod2(float a) => a - 2f * MathF.Floor(a * 0.5f);

    /// <summary>A baked static's depth with its biases, as the geo VS/PS give it (rank left out): wall, flat (floored, see
    /// <see cref="FlatFloored"/>) or box over tile (tx, ty) at z, lowered by <see cref="BuriedDrop"/> when <paramref name="buried"/>;
    /// a <paramref name="wet"/> flat (with wet_surface_depth on) is its plane lowered by <see cref="WetDrop"/>, with no floor
    /// (the floor would bind only in the 2.2 px under the back edges).</summary>
    public static float Static(int tx, int ty, int z, int height, bool wall, bool flat, bool buried, float sx, float sy, bool wet = false)
    {
        float d;
        if (wall) d = Wall(tx, ty, sx) + WallBias;
        else if (flat) d = wet ? Plane(sy, z) - WetDrop : FlatFloored(tx, ty, z, sx, sy);
        else d = Box(tx, ty, z, height, sx, sy);
        return d + z * ZBias - (buried ? BuriedDrop : 0f);
    }

    /// <summary>A flat land tile (x, y, z)'s depth at world pixel (sx, sy), with its bias (floored, see <see cref="FlatFloored"/>;
    /// stretched land keeps its corners' plane, sprite.hlsl FLAG_STRETCH).</summary>
    public static float Land(int tx, int ty, int z, float sx, float sy) => FlatFloored(tx, ty, z, sx, sy) + (z - 2) * ZBias;

    /// <summary>A mobile composite's depth with its biases (see <see cref="Mobile"/>).</summary>
    public static float MobileBiased(float ax, float ay, int z, float sx, float sy) => Mobile(ax, ay, z, sx, sy) + MobileBias + z * ZBias;

    /// <summary>Whether world pixel (sx, sy) lies on a static's shape over tile (tx, ty) at z (CUO_CLIP_ORDER_CHECK samples
    /// only there): a wall's faces on the back edges up to z + height, a box's top diamond and front faces down to z, a flat's
    /// diamond. Art beyond the shape (tree crowns, wall caps) is not sampled.</summary>
    public static bool OnShape(int tx, int ty, int z, int height, bool wall, bool flat, float sx, float sy)
    {
        var (nx, ny) = NorthVertex(tx, ty, z);
        float s = MathF.Abs(sx - nx);
        if (s > 22f) return false;
        float h = 4f * height;
        if (wall) return sy <= ny + s && sy >= ny + s - h;
        if (flat) return sy >= ny + s && sy <= ny + 44f - s;
        return sy >= ny - h + s && sy <= ny + 44f - s;
    }

    /// <summary>World pixel of tile (tx, ty, z)'s north vertex.</summary>
    public static (int X, int Y) NorthVertex(int tx, int ty, int z) => ((tx - ty) * 22, (tx + ty) * 22 - 22 - 4 * z);

    /// <summary>World pixel of a mobile's anchor standing on tile (tx, ty, z) (MobileView: posX + 22, posY + 19).</summary>
    public static (int X, int Y) MobileAnchor(int tx, int ty, int z) => ((tx - ty) * 22, (tx + ty) * 22 - 4 * z - 3);
}
