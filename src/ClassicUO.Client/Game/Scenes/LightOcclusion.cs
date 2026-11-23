// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_occlusion (light-edges, default off): the light layer is screen-space additive quads without depth, so at the
    /// Moonglow bank the halo of a lit window was drawn over the window and wall in front of it. With the switch on, the light
    /// pass (GameScene.DrawLight) writes the art of every occluder of a light into the light target's stencil and draws that
    /// light where the stencil does not hold it.
    /// Owner rule (2026-11-22, restated for round 4): a light's glow passes through its own emitter only; another object
    /// rendered over the light, between the viewer and the source, hides it, whatever its kind (another window, lit or not,
    /// a wall, a light-flagged static, a ground item). An occluder stands on a later diagonal (<see cref="InFront"/>) and its
    /// art covers the source's light point on screen (<see cref="LightPoint"/>, <see cref="CoverPoint"/> and the art's pixel
    /// mask). Objects the source stands on, or that stand beside it on the same wall line, are drawn after it but do not cover
    /// its point: they are lit by it, not blockers (round 3 masked every object drawn after the source, which cut the
    /// candles' glow on the bank table, the windows' halo on their own wall and the street lamps' halos on fences, roofs and
    /// flowers).
    /// A wall source (a lit window) is also hidden by wall pieces in front whose visible face turns away from it
    /// (<see cref="FacesAway"/>, the line taken from the art, <see cref="Facing"/>): the perpendicular line at an inside
    /// corner of the keep's zig-zag wall, a parallel line in front; never the pieces of its own line.
    /// Tiles that never hide anything: translucent ones (water, glass: drawn see-through), internal (no-draw) ones, and flat
    /// walkable surfaces (floors). Mobiles do not occlude.
    /// Round 5: an object hides only as the world draws it (<see cref="MinOccluderAlpha"/>, circle of transparency, custom house
    /// render flags, the drawn sprite and its offsets), and the search walks the drawn chunks whose recorded art and Z can reach
    /// the light (<see cref="ChunkReaches"/>). GameScene.AddLightCore's whole-light rejection (an object 5 Z above on tile x+1,
    /// y+1 drops the light) stays: without it the lit windows under a roof eave glow over the roof in front (round 5).
    /// CUO_LIGHT_OCCLUSION_CHECK=1 compares each light's occluders with a reference scan of every tile of every drawn chunk, in
    /// both states, and logs each difference.
    /// </summary>
    internal static class LightOcclusion
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightOcclusion ?? false);

        /// <summary>Round 7 (owner, Luna 1005 513 -30 Malas: the wall in front of a candelabra stayed dark inside its halo;
        /// folded into light_occlusion 2026-11-22): the light map multiplies the finished frame, so a mask only takes light off
        /// the occluder's own pixels; occlusion decides whether an object in front of a light is lit by it. A surface glow (a lit
        /// window's beam, LightAlphaFade.IsSurface) must not light the nearer walls (the Moonglow bank's window behind a
        /// window); a radial light (candelabra, lamp, torch, fire field, a mobile's light) does spill onto the objects in front
        /// of it. So a light gets occluders and masks only when it is a surface glow (<paramref name="surface"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Masks(bool surface) => surface;

        /// <summary>Round 5 (review P1 #2): an object hides a glow only where the world draws it at least half opaque (its
        /// AlphaHue, a custom house component's 192, an item sprite's alpha). The stencil is binary, and UO art texels are
        /// either clear or opaque, so this is the stencil's alpha test at 0.5: a fading roof or a foliage piece behind which
        /// the player stands (faded to 76) stops hiding the glow half way through its fade, a 75 % opaque custom house
        /// component still hides it.</summary>
        public const byte MinOccluderAlpha = 128;

        /// <summary>Round 5 (review P2 #5): what an item's drawn sprites may reach past the chunk's art record (Chunk
        /// StaticOver*): a moving item's render offset (up to a tile) and a stack's second sprite (5 px up-left).</summary>
        public const int ItemSlack = 48;

        /// <summary>An object at (dx, dy, dz) from the light's source stands in front of it: on a later diagonal (dx + dy
        /// greater), so it is drawn after it. Objects on the source's own tile are at its depth (drawn after it only by Z):
        /// they stand with the emitter, not in front of it (round 4: the fountain piece sharing the Moonglow fountain lamp's
        /// tile masked its glow), so they never hide it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InFront(int dx, int dy, int dz) => dx + dy > 0;

        /// <summary>Round 6a (owner, Moonglow 4474 1091: the one eave tile whose art covered a lamp post's light point stayed
        /// dark while the eave tiles around it were lit): an object whose bottom is at or above the source's top (dz &gt;=
        /// <paramref name="srcTop"/>, <see cref="SourceTop"/>) is above the light, which shines up onto it, so covering the
        /// light point does not make it hide the glow. A wall piece turned away from a wall source (<see cref="FacesAway"/>)
        /// still hides it at any height: its visible face is behind the source's plane (the bank's upper wall line in front
        /// of a ground floor window).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Above(int dz, int srcTop) => dz >= srcTop;

        /// <summary>Round 6a: the height of a light's source above its Z, for <see cref="Above"/>: its tiledata height,
        /// or the height of its light point above the tile (<see cref="LightPoint"/>, from the anchor's tile centre ay + 22)
        /// when that is higher (wall sconces, hanging lanterns and braziers have tiledata height 0 but their art, and the
        /// glow, stand above the tile); a mobile's light: <see cref="MobileTop"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SourceTop(int height, int ay, int py) => Math.Max(height, (ay + 22 - py) >> 2);

        /// <summary>Round 6a: a mobile's light's top: a person's height (the pathfinder's Constants.DEFAULT_BLOCK_HEIGHT).</summary>
        public const int MobileTop = Constants.DEFAULT_BLOCK_HEIGHT;

        /// <summary>The tiledata of an object that hides a light drawn behind it: anything but a translucent or internal tile
        /// or a flat walkable surface (a roof hides).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool OccluderData(ref StaticTiles data) =>
            !data.IsTranslucent && !data.IsInternal && (data.IsRoof || !(data.IsSurface && data.Height == 0));

        /// <summary>The light pass masks a light's halo over an object with tiledata <paramref name="data"/> at (dx, dy, dz)
        /// from its source: only with the switch on (<paramref name="on"/>), never over the light's own emitter
        /// (<paramref name="ownEmitter"/>), in front of the source, an occluder, and either its art covering the source's
        /// light point (<paramref name="coversSource"/>: <see cref="CoverPoint"/> and the art's pixel there) while not
        /// <see cref="Above"/> the source (<paramref name="srcTop"/>: its height, <see cref="SourceTop"/>), or, for a wall
        /// source, a wall piece whose visible face turns away from it (<paramref name="wallFacesAway"/>,
        /// <see cref="FacesAway"/>).</summary>
        public static bool Hides(bool on, bool ownEmitter, int dx, int dy, int dz, int srcTop, ref StaticTiles data, bool coversSource,
            bool wallFacesAway = false) =>
            on && !ownEmitter && (coversSource && !Above(dz, srcTop) || wallFacesAway) && InFront(dx, dy, dz) && OccluderData(ref data);

        /// <summary>A wall piece's line, from its art (44 px wide, its visible bounds bx..bx+bw): the slab on the tile's east
        /// edge (a line along Y, its visible face towards +X, e.g. 0x0162), on its south edge (a line along X, face towards
        /// +Y, e.g. 0x0163), or neither (posts, arches, odd art: <see cref="FacingNone"/>).</summary>
        public const byte FacingNone = 0, FacingEast = 1, FacingSouth = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Facing(int w, int bx, int bw)
        {
            if (w != 44 || bw <= 0)
            {
                return FacingNone;
            }

            int c = 2 * bx + bw; // twice the bounds' centre

            return c >= 52 ? FacingEast : c <= 36 ? FacingSouth : FacingNone;
        }

        /// <summary>Round 4 (owner 2026-11-22, the Moonglow keep's inside corners): a wall source's glow spilled onto the
        /// perpendicular wall in front of it, which does not cover the window itself. A wall piece facing
        /// <paramref name="occFacing"/> at (dx, dy) from a wall source facing <paramref name="srcFacing"/> turns its visible
        /// face away from the source when the source lies behind that face's plane: a piece of the same line (same facing,
        /// aligned: dx 0 for east-facing, dy 0 for south-facing) is lit, a parallel line in front or the perpendicular line
        /// at a corner is not. Unknown facings never count (the cover test still applies).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FacesAway(byte srcFacing, byte occFacing, int dx, int dy)
        {
            if (srcFacing == FacingNone)
            {
                return false;
            }

            return occFacing switch
            {
                FacingEast => srcFacing == FacingEast ? dx > 0 : dx >= 0,
                FacingSouth => srcFacing == FacingSouth ? dy > 0 : dy >= 0,
                _ => false
            };
        }

        /// <summary>The light point an occluder must cover: the centre of the emitter's visible art (its art w x h anchored at
        /// (ax, ay), View.DrawStatic; its visible bounds bx, by, bw, bh in the art): a window's glass, a lamp post's upper
        /// post, a candle. Pieces drawn after the emitter that overlap only the edge of its art (the next piece of the same wall
        /// line) and low objects in front of its foot (the counter a candle stands on, the fence before a street lamp) do not
        /// cover it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LightPoint(int ax, int ay, int w, int h, int bx, int by, int bw, int bh, out int px, out int py)
        {
            px = ax - ((w >> 1) - 22) + bx + (bw >> 1);
            py = ay - (h - 44) + by + (bh >> 1);
        }

        /// <summary>The art (w x h) of a static anchored at (ax, ay) (View.DrawStatic) holds the screen point (px, py): its
        /// pixel there is (lx, ly) in the art.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CoverPoint(int ax, int ay, int w, int h, int px, int py, out int lx, out int ly)
        {
            lx = px - (ax - ((w >> 1) - 22));
            ly = py - (ay - (h - 44));

            return (uint)lx < (uint)w && (uint)ly < (uint)h;
        }

        /// <summary>Round 5 (review P2 #5, replaces the tile window that assumed art up to 300 x 256 and Z within -30..+40 of
        /// the source): the sprites of chunk (cx, cy) (8 x 8 tiles), whose art reaches <paramref name="overLeft"/> /
        /// <paramref name="overRight"/> / <paramref name="overUp"/> px past a tile's 44 px box (Chunk.StaticOver*) and whose
        /// objects stand between Z <paramref name="zLo"/> and <paramref name="zHi"/> (Chunk.OccZLo/Hi; empty when low is above
        /// high), can overlap the screen rectangle (left, top, right, bottom) with the draw offset (offX, offY).</summary>
        public static bool ChunkReaches(int cx, int cy, int overLeft, int overRight, int overUp, int zLo, int zHi, int offX, int offY,
            int left, int top, int right, int bottom)
        {
            if (zLo > zHi)
            {
                return false;
            }

            int x0 = cx << 3, y0 = cy << 3;
            // anchors: ax = (x - y) * 22 - offX - 22, ay = (x + y) * 22 - 4z - offY - 22 over the chunk's tiles and Z range
            int axLo = (x0 - (y0 + 7)) * 22 - offX - 22, axHi = (x0 + 7 - y0) * 22 - offX - 22;
            int ayLo = (x0 + y0) * 22 - (zHi << 2) - offY - 22, ayHi = (x0 + y0 + 14) * 22 - (zLo << 2) - offY - 22;

            return axLo - overLeft - ItemSlack < right && axHi + 44 + overRight + ItemSlack > left &&
                ayLo - overUp - ItemSlack < bottom && ayHi + 44 + ItemSlack > top;
        }

        /// <summary>A tile anchored at screen x <paramref name="ax"/> can hold a sprite overlapping columns left..right, with
        /// its chunk's art record (<see cref="ChunkReaches"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TileReachesX(int ax, int overLeft, int overRight, int left, int right) =>
            ax - overLeft - ItemSlack < right && ax + 44 + overRight + ItemSlack > left;

        /// <summary>A sprite at (x, y), w x h, overlaps the screen rectangle.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RectOverlaps(int x, int y, int w, int h, int left, int top, int right, int bottom) =>
            x < right && x + w > left && y < bottom && y + h > top;

        /// <summary>The art rectangle of a static anchored at (ax, ay) (View.DrawStatic) overlaps the light quad.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ArtOverlaps(int ax, int ay, int w, int h, int left, int top, int right, int bottom)
        {
            int x0 = ax - ((w >> 1) - 22), y0 = ay - (h - 44);

            return x0 < right && x0 + w > left && y0 < bottom && y0 + h > top;
        }

        /// <summary>CUO_LIGHT_OCCLUSION_CHECK: an order-independent hash of one occluder.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Mix(int x, int y, int z, ushort graphic, bool item) => ViewMargin.Mix(x, y, z, graphic, item);

    }
}
